namespace JellyDiscer.Core.Audio;

public sealed class Channel
{
    public volatile float TrimDb, Low = 1, Mid = 1, High = 1, Filter, Fader = 1;
    public volatile bool Pfl;
    public Meter Meter { get; } = new();
    public LoudnessMeter Loudness { get; } = new();
    readonly Eq3 _eq = new();
    readonly KnobFilter _filter = new();
    float _trim = 1, _fade;

    /// <summary>Trim, EQ and filter in place; this is also the cue tap.</summary>
    internal void Process(Span<float> s)
    {
        float t = MathF.Pow(10, TrimDb / 20);
        for (int i = 0; i < s.Length; i += 2) { _trim += (t - _trim) * 0.002f; s[i] *= _trim; s[i + 1] *= _trim; }
        _eq.Process(s, Low, Mid, High);
        _filter.Process(s, Filter);
        Meter.Process(s);
        Loudness.Process(s);
    }
    internal void MixTo(ReadOnlySpan<float> s, Span<float> o, float xf)
    {
        float t = Fader * Fader * xf;
        for (int i = 0; i < s.Length; i += 2) { _fade += (t - _fade) * 0.003f; o[i] += s[i] * _fade; o[i + 1] += s[i + 1] * _fade; }
    }
}

public enum PhoneMode { Blend, SplitMono }

/// <summary>Renders both decks and the mix. Knows nothing about devices.</summary>
public sealed class Engine
{
    public const int MaxBlock = 512;
    public Deck[] Decks { get; } = [new(), new()];
    public Channel[] Channels { get; } = [new(), new()];
    public volatile float Crossfader = 0.5f, Curve, MasterGain = 0.8f, CueGain = 0.8f, CueBlend;
    public volatile PhoneMode PhoneMode;
    /// <summary>Positive delays the cue output, negative delays the master (ms).</summary>
    public volatile float CueOffsetMs;
    /// <summary>Added to CueOffsetMs: cancels the known extra buffering of the cue path.</summary>
    public volatile float BaseOffsetMs;
    /// <summary>True when one device carries the headphone mix and there is no separate master.</summary>
    public volatile bool SingleDevice;
    public Meter MasterMeter { get; } = new();
    public Limiter Limiter { get; } = new();
    readonly Limiter _cueLimiter = new();
    public StereoRing RecordRing { get; } = new(1 << 18);
    public volatile bool Recording;
    int _recordDropped;
    public int RecordDropped => Volatile.Read(ref _recordDropped);

    readonly Command[] _q = new Command[256];
    long _qw, _qr;
    readonly Lock _qLock = new();
    readonly float[][] _deckBuf = [new float[MaxBlock * 2], new float[MaxBlock * 2]];
    readonly float[] _pfl = new float[MaxBlock * 2];
    readonly DelayLine _masterDelay = new(), _cueDelay = new();
    float _mg, _cg;

    /// <summary>Queues a command for the audio thread. The lock is only between callers; the audio thread never takes it.</summary>
    public bool Send(int deck, DeckOp op, double a = 0, double b = 0, TrackBuffer? track = null)
    {
        lock (_qLock)
        {
            if (_qw - Volatile.Read(ref _qr) >= _q.Length) return false;
            _q[_qw & 255] = new Command { Deck = deck, Op = op, A = a, B = b, Track = track };
            Volatile.Write(ref _qw, _qw + 1);
            return true;
        }
    }

    /// <summary>Renders up to MaxBlock frames. No allocation, locks or I/O.</summary>
    public void Render(Span<float> master, Span<float> cue)
    {
        while (_qr < Volatile.Read(ref _qw))
        {
            ref var c = ref _q[_qr & 255];
            Decks[c.Deck].Apply(c);
            c.Track = null;
            Volatile.Write(ref _qr, _qr + 1);
        }
        int n = master.Length;
        master.Clear();
        var pfl = _pfl.AsSpan(0, n);
        pfl.Clear();
        var (xa, xb) = Dsp.Crossfade(Crossfader, Curve);
        for (int d = 0; d < 2; d++)
        {
            var b = _deckBuf[d].AsSpan(0, n);
            Decks[d].Render(b);
            Channels[d].Process(b);
            if (Channels[d].Pfl)
                for (int i = 0; i < n; i++) pfl[i] += b[i];
            Channels[d].MixTo(b, master, d == 0 ? xa : xb);
        }
        for (int i = 0; i < n; i += 2) { _mg += (MasterGain - _mg) * 0.003f; master[i] *= _mg; master[i + 1] *= _mg; }
        Limiter.Process(master);
        MasterMeter.Process(master);
        if (Recording)
        {
            int wrote = RecordRing.Write(master);
            if (wrote < n / 2) _recordDropped += n / 2 - wrote;
        }

        bool split = PhoneMode == PhoneMode.SplitMono;
        float blend = CueBlend;
        for (int i = 0; i < n; i += 2)
        {
            _cg += (CueGain - _cg) * 0.003f;
            if (split)
            {
                cue[i] = (pfl[i] + pfl[i + 1]) * 0.5f * _cg;
                cue[i + 1] = (master[i] + master[i + 1]) * 0.5f * _cg;
            }
            else
            {
                cue[i] = (pfl[i] * (1 - blend) + master[i] * blend) * _cg;
                cue[i + 1] = (pfl[i + 1] * (1 - blend) + master[i + 1] * blend) * _cg;
            }
        }
        _cueLimiter.Process(cue);
        int off = SingleDevice ? 0 : (int)((CueOffsetMs + BaseOffsetMs) * 48);
        _cueDelay.Process(cue, Math.Max(0, off));
        _masterDelay.Process(master, Math.Max(0, -off));
    }
}
