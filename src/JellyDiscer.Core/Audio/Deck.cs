namespace JellyDiscer.Core.Audio;

public enum DeckOp { Load, Play, Pause, CueDown, CueUp, SetCue, Seek, JumpBy, HotPlay, LoopOn, LoopOff }

/// <summary>Transport and playback. Fields marked volatile are set by the UI; the rest belongs to the audio thread.</summary>
public sealed class Deck
{
    public volatile float Tempo;      // fraction: 0.03 = +3 %
    public volatile float Nudge;      // fraction, while held
    public volatile bool KeyLock;

    const int Sub = 64;
    const float EnvStep = 1f / 3;     // per Sub, a 4 ms fade
    TrackBuffer? _track;
    double _pos, _cue, _jump, _loopStart, _loopEnd;
    bool _playing, _previewing, _loop, _hasJump, _jumpRelative, _stretching;
    float _env, _rate = 1;
    readonly Stretcher? _stretch = Stretcher.TryCreate();

    // published for the UI
    double _pubPos, _pubCue, _pubLoopStart, _pubLoopEnd, _pubRate = 1;
    volatile bool _pubPlaying, _pubLoop;
    public bool KeyLockAvailable => _stretch != null;
    /// <summary>Position being heard, in frames.</summary>
    public double Position => Volatile.Read(ref _pubPos);
    public double CuePoint => Volatile.Read(ref _pubCue);
    public double LoopStart => Volatile.Read(ref _pubLoopStart);
    public double LoopEnd => Volatile.Read(ref _pubLoopEnd);
    public double Rate => Volatile.Read(ref _pubRate);
    public bool Playing => _pubPlaying;
    public bool Looping => _pubLoop;

    internal void Apply(in Command c)
    {
        switch (c.Op)
        {
            case DeckOp.Load:
                _track = c.Track;
                _pos = 0;
                _cue = c.A;
                _playing = _previewing = _loop = _hasJump = _stretching = false;
                _env = 0;
                JumpTo(c.A);
                break;
            case DeckOp.Play: if (_track != null) { _playing = true; _previewing = false; } break;
            case DeckOp.Pause: _playing = _previewing = false; break;
            case DeckOp.SetCue: _cue = c.A; break;
            case DeckOp.CueDown:
                if (_playing) { _playing = false; JumpTo(_cue); }
                else if (Math.Abs(Heard - _cue) > 480) _cue = Heard;
                else if (_track != null) { JumpTo(_cue); _playing = _previewing = true; }
                break;
            case DeckOp.CueUp:
                if (_previewing) { _playing = _previewing = false; JumpTo(_cue); }
                break;
            case DeckOp.Seek: JumpTo(c.A); break;
            case DeckOp.HotPlay: JumpTo(c.A); if (_track != null) _playing = true; break;
            case DeckOp.JumpBy:
                if (_hasJump) _jump += c.A;
                else { _jump = c.A; _jumpRelative = _hasJump = true; }
                break;
            case DeckOp.LoopOn:
                if (c.B > c.A + 64) { _loopStart = c.A; _loopEnd = c.B; _loop = true; }
                break;
            case DeckOp.LoopOff: _loop = false; break;
        }
    }

    void JumpTo(double p) { _jump = p; _jumpRelative = false; _hasJump = true; }

    double Heard
    {
        get
        {
            if (!_stretching) return _pos;
            double p = _pos - _stretch!.Ahead * _rate;
            if (_loop && p < _loopStart && _pos >= _loopStart) p += _loopEnd - _loopStart;
            return Math.Max(0, p);
        }
    }

    internal void Render(Span<float> o)
    {
        for (int i = 0; i < o.Length; i += Sub * 2) RenderSub(o.Slice(i, Math.Min(Sub * 2, o.Length - i)));
        Volatile.Write(ref _pubPos, _hasJump && !_playing && !_jumpRelative ? _jump : Heard);
        Volatile.Write(ref _pubCue, _cue);
        Volatile.Write(ref _pubLoopStart, _loopStart);
        Volatile.Write(ref _pubLoopEnd, _loopEnd);
        Volatile.Write(ref _pubRate, _rate);
        _pubPlaying = _playing;
        _pubLoop = _loop;
        GC.KeepAlive(_track);
    }

    void RenderSub(Span<float> o)
    {
        if (_track == null) { o.Clear(); return; }
        bool wantStretch = KeyLock && _stretch != null;
        if (wantStretch != _stretching && !_hasJump) { _jump = 0; _jumpRelative = _hasJump = true; } // re-align with a fade
        if (_env <= 0 && _hasJump)
        {
            double p = _jumpRelative ? Heard + _jump : _jump;
            _pos = Math.Clamp(p, 0, _track.Frames);
            if (_loop && (_pos >= _loopEnd || _pos < _loopStart)) _loop = false;
            _hasJump = false;
            _stretching = wantStretch;
            if (_stretching) _stretch!.Reset(1 / Math.Max(0.1, TargetRate));
        }
        if (_pos >= _track.Frames && Heard >= _track.Frames) _playing = _previewing = false;
        float target = _playing && !_hasJump ? 1 : 0, start = _env;
        if (target == 0 && start <= 0) { o.Clear(); _rate = TargetRate; return; }
        float end = target > start ? Math.Min(target, start + EnvStep) : Math.Max(target, start - EnvStep);
        _env = end;
        if (_stretching) { _stretch!.SetPitch(1 / Math.Max(0.1, _rate)); _stretch.Process(this, o); }
        else ReadSource(o);
        if (start != 1 || end != 1)
            for (int i = 0; i < o.Length; i += 2)
            {
                float g = start + (end - start) * i / o.Length;
                o[i] *= g;
                o[i + 1] *= g;
            }
    }

    float TargetRate => (1 + Tempo) * (1 + Nudge);

    internal void ReadSource(Span<float> o)
    {
        ReadOnlySpan<float> s = _track!.Samples;
        int frames = _track.Frames;
        float target = TargetRate;
        for (int i = 0; i < o.Length; i += 2)
        {
            _rate += (target - _rate) * 0.002f;
            int k = (int)_pos;
            float t = (float)(_pos - k);
            if (k >= 1 && k + 2 < frames)
            {
                int j = (k - 1) * 2;
                o[i] = Dsp.Hermite(s[j], s[j + 2], s[j + 4], s[j + 6], t);
                o[i + 1] = Dsp.Hermite(s[j + 1], s[j + 3], s[j + 5], s[j + 7], t);
            }
            else if (k >= 0 && k < frames) { o[i] = s[2 * k]; o[i + 1] = s[2 * k + 1]; }
            else o[i] = o[i + 1] = 0;
            double before = _pos;
            _pos += _rate;
            if (_loop && before < _loopEnd && _pos >= _loopEnd) _pos -= _loopEnd - _loopStart;
        }
    }
}

internal struct Command
{
    public int Deck;
    public DeckOp Op;
    public double A, B;
    public TrackBuffer? Track;
}
