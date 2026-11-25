namespace JellyDiscer.Core.Audio;

/// <summary>Lock-free single-producer single-consumer ring of stereo frames.</summary>
public sealed class StereoRing(int capacityPow2)
{
    readonly float[] _d = GC.AllocateArray<float>(capacityPow2 * 2, pinned: true);
    readonly int _mask = capacityPow2 - 1;
    long _w, _r;
    public int Capacity => capacityPow2;
    public int Fill => (int)(Volatile.Read(ref _w) - Volatile.Read(ref _r));

    public int Write(ReadOnlySpan<float> s)
    {
        long w = _w;
        int n = Math.Min(s.Length / 2, Capacity - (int)(w - Volatile.Read(ref _r)));
        for (int i = 0; i < n; i++)
        {
            int k = (int)((w + i) & _mask) * 2;
            _d[k] = s[2 * i];
            _d[k + 1] = s[2 * i + 1];
        }
        Volatile.Write(ref _w, w + n);
        return n;
    }

    public bool ReadFrame(out float l, out float r)
    {
        long p = _r;
        if (p >= Volatile.Read(ref _w)) { l = r = 0; return false; }
        int k = (int)(p & _mask) * 2;
        l = _d[k];
        r = _d[k + 1];
        Volatile.Write(ref _r, p + 1);
        return true;
    }

    public int Read(Span<float> s)
    {
        int n = 0;
        while (n < s.Length / 2 && ReadFrame(out s[2 * n], out s[2 * n + 1])) n++;
        return n;
    }

    /// <summary>Consumer side only.</summary>
    public void Skip(int frames) => Volatile.Write(ref _r, _r + Math.Min(frames, Fill));
}

/// <summary>Reads a ring at a rate that follows the fill level, so a device with its own clock
/// neither starves nor overflows.</summary>
public sealed class DriftReader(StereoRing ring, int target = 3072)
{
    readonly float[] _h = new float[8];
    double _frac, _avg, _integral;
    bool _primed;
    int _underruns;
    float _fade;
    // after priming: let the device settle, measure the mean fill, then correct all of the error at once
    const int Settle = 24000, Measure = 48000, Rounds = 2, FadeFrames = 480;
    int _round, _elapsed, _adjust, _hold;
    double _sum, _weight;
    bool _fadingOut;
    double _ratio = 1;
    public double Ratio => Volatile.Read(ref _ratio);
    public double AverageFill => Volatile.Read(ref _avg);
    public int Underruns => Volatile.Read(ref _underruns);
    public int Target => target;

    /// <summary>True once the start-up correction is finished and only slow tracking remains.</summary>
    public bool Settled => Volatile.Read(ref _round) >= Rounds;

    void Acquire()
    {
        Volatile.Write(ref _round, 0);
        _elapsed = _hold = 0;
        _sum = _weight = 0;
        _fadingOut = false;
    }

    public void Read(Span<float> o)
    {
        int frames = o.Length / 2;
        if (!_primed)
        {
            if (ring.Fill < target) { o.Clear(); return; }
            if (ring.Fill > target) ring.Skip(ring.Fill - target);
            _avg = target;
            _primed = true;
            _fade = 0;
            Acquire();
        }
        else if (ring.Fill > target * 3) { ring.Skip(ring.Fill - target); _avg = target; _fade = 0; Acquire(); }

        if (_fadingOut && _fade <= 0)
        {
            _fadingOut = false;
            if (_adjust > 0) ring.Skip(_adjust); else _hold = -_adjust;
            Volatile.Write(ref _avg, target);
            _integral = 0;
        }
        if (_hold > 0)
        {
            int h0 = Math.Min(_hold, frames);
            o[..(2 * h0)].Clear();
            _hold -= h0;
            o = o[(2 * h0)..];
            frames -= h0;
            if (frames == 0) return;
        }
        if (_round < Rounds && !_fadingOut)
        {
            _elapsed += frames;
            if (_elapsed > Settle) { _sum += (double)ring.Fill * frames; _weight += frames; }
            if (_elapsed >= Settle + Measure)
            {
                _adjust = (int)Math.Round(_sum / _weight - target);
                _round++;
                _elapsed = 0;
                _sum = _weight = 0;
                _fadingOut = Math.Abs(_adjust) > 24; // half a millisecond
            }
        }

        double dt = frames / (double)TrackBuffer.SampleRate;
        double avg = _avg + (ring.Fill - _avg) * (1 - Math.Exp(-dt / 2.0));
        Volatile.Write(ref _avg, avg);
        double err = (avg - target) / TrackBuffer.SampleRate;          // seconds of surplus
        _integral = Math.Clamp(_integral + err * dt * 1e-4, -0.002, 0.002);
        double ratio = 1 + Math.Clamp(err * 0.02 + _integral, -0.004, 0.004);
        Volatile.Write(ref _ratio, ratio);

        var h = _h;
        for (int i = 0; i < frames; i++)
        {
            _frac += ratio;
            while (_frac >= 1)
            {
                if (!ring.ReadFrame(out var l, out var r))
                {
                    _underruns++;
                    _primed = false;
                    o[(2 * i)..].Clear();
                    return;
                }
                for (int k = 0; k < 6; k++) h[k] = h[k + 2];
                h[6] = l;
                h[7] = r;
                _frac -= 1;
            }
            _fade = Math.Clamp(_fade + (_fadingOut ? -1f : 1f) / FadeFrames, 0, 1);
            float f = (float)_frac;
            o[2 * i] = _fade * Dsp.Hermite(h[0], h[2], h[4], h[6], f);
            o[2 * i + 1] = _fade * Dsp.Hermite(h[1], h[3], h[5], h[7], f);
        }
    }
}
