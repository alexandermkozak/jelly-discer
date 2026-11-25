using JellyDiscer.Core.Analysis;
using JellyDiscer.Core.Audio;
using JellyDiscer.Jellyfin;

namespace JellyDiscer.App;

/// <summary>Track loading, grid edits and transport for one deck. UI thread only.</summary>
public sealed class DeckController(int index, Engine engine, Store store, Assists assists)
{
    public Deck Deck => engine.Decks[index];
    public Channel Channel => engine.Channels[index];
    public int Index => index;
    public string Letter => index == 0 ? "A" : "B";
    public Track? Track { get; private set; }
    public TrackData Data { get; private set; } = new();
    public TrackBuffer? Buffer { get; private set; }
    public byte[]? Art { get; private set; }
    public string Status { get; private set; } = "Empty";
    public float Range = 0.08f;
    public int LoopBeats = 4;
    public event Action? Changed;
    int _version;
    readonly List<double> _taps = [];
    DateTime _lastTap;

    public double Seconds => Deck.Position / TrackBuffer.SampleRate;
    public double EffectiveBpm => (Data.Grid?.Bpm ?? 0) * (1 + Deck.Tempo);

    void Set(string s) { Status = s; Changed?.Invoke(); }

    public async Task<bool> LoadAsync(JellyfinClient client, TrackCache cache, Track t)
    {
        if (Deck.Playing) { Set("Pause this deck before loading"); return false; }
        int v = ++_version;
        try
        {
            Set("Downloading " + t.Title);
            var path = await cache.GetAsync(client, t.Id, new Progress<double>(p => { if (v == _version && Status.StartsWith("Down")) Set($"Downloading {p:P0}"); }));
            if (v != _version) return false;
            Set("Decoding");
            var buf = await Task.Run(() => TrackBuffer.DecodeAsync(path));
            if (v != _version) return false;
            var data = store.Get(t.Id);
            if (Deck.Playing) { Set("Pause this deck before loading"); return false; }
            Track = t; Buffer = buf; Data = data ?? new(); Art = null;
            Channel.TrimDb = (float)Math.Clamp(t.GainDb ?? 0, -12, 12);
            engine.Send(index, DeckOp.Load, Data.Cue, 0, buf);
            Set(data == null ? "Analyzing beats" : "");
            if (data == null)
            {
                var (g, w) = await Task.Run(() => (Analyzer.DetectGrid(buf), Analyzer.BuildWaveform(buf)));
                if (v != _version) return false;
                Data.Grid = g; Data.Waveform = w;
                Save();
                Set("");
            }
            try { Art = await client.ImageAsync(t.Id); } catch (HttpRequestException) { }
            if (v == _version) Changed?.Invoke();
            return v == _version;
        }
        catch (Exception e) { if (v == _version) Set("Could not load: " + e.Message); return false; }
    }

    void Save() { if (Track != null && Data.Waveform != null) store.Save(Track.Id, Data); }
    void Send(DeckOp op, double a = 0, double b = 0) => engine.Send(index, op, a, b);

    public void PlayPause() => Send(Deck.Playing ? DeckOp.Pause : DeckOp.Play);
    public void Cue(bool down)
    {
        if (down && !Deck.Playing && Track != null) { Data.Cue = Deck.Position; Save(); }
        Send(down ? DeckOp.CueDown : DeckOp.CueUp);
    }
    public void Seek(double sec) => Send(DeckOp.Seek, Math.Max(0, sec) * TrackBuffer.SampleRate);
    /// <summary>Sets an empty hot cue or jumps to a set one. Quantized, cues are set on the beat and jumps made while
    /// playing cover whole beats, so the mix stays in time. Needs a trustworthy grid; otherwise positions are exact.</summary>
    public void HotCue(int i)
    {
        if (Track == null) return;
        var grid = assists.Quantize && Data.Grid is { Confident: true } ? Data.Grid : null;
        if (Data.HotCues[i] < 0)
        {
            Data.HotCues[i] = grid == null ? Deck.Position : Math.Max(0, grid.NearestBeat(Seconds)) * TrackBuffer.SampleRate;
            Save();
            Changed?.Invoke();
        }
        else if (grid != null && Deck.Playing)
            Send(DeckOp.JumpBy, grid.BeatsTo(Seconds, Data.HotCues[i] / TrackBuffer.SampleRate) * grid.Period * TrackBuffer.SampleRate);
        else Send(DeckOp.HotPlay, Data.HotCues[i]);
    }
    public void ClearHotCue(int i) { Data.HotCues[i] = -1; Save(); Changed?.Invoke(); }

    /// <summary>Loops LoopBeats beats, starting on the nearest beat when there is a grid (at 120 BPM otherwise).</summary>
    public void ToggleLoop()
    {
        if (Deck.Looping) { Send(DeckOp.LoopOff); return; }
        double start = Seconds, beat = 0.5;
        if (Data.Grid is { } g) { start = Math.Max(0, g.TimeOf(Math.Floor(g.BeatAt(start)))); beat = g.Period; }
        Send(DeckOp.LoopOn, start * TrackBuffer.SampleRate, (start + beat * LoopBeats) * TrackBuffer.SampleRate);
    }
    public void StepTempo(int dir) => Deck.Tempo = Math.Clamp(Deck.Tempo + dir * 0.0005f, -Range, Range);

    public string? Sync(DeckController other)
    {
        if (Data.Grid is not { } me || other.Data.Grid is not { } o) return "Both decks need a beatgrid to sync";
        double ratio = other.EffectiveBpm / me.Bpm;
        while (ratio > 1.42) ratio /= 2;
        while (ratio < 0.71) ratio *= 2;
        if (Math.Abs(ratio - 1) > Range + 1e-6) return $"Needs {(ratio - 1) * 100:+0.0;-0.0} %, outside the tempo range";
        Deck.Tempo = (float)(ratio - 1);
        if (Deck.Playing && other.Deck.Playing)
            Send(DeckOp.JumpBy, -BeatGrid.PhaseDiff(me, Seconds, o, other.Seconds) * me.Period * TrackBuffer.SampleRate);
        return null;
    }

    void SetGrid(BeatGrid g) { Data.Grid = g; Save(); Changed?.Invoke(); }
    public void ShiftGrid(double ms) { if (Data.Grid is { } g) SetGrid(g with { Offset = g.Offset + ms / 1000, Confidence = 1 }); }
    public void ScaleBpm(double f) { if (Data.Grid is { } g && g.Bpm * f is > 30 and < 400) SetGrid(g with { Bpm = g.Bpm * f }); }
    public void SetDownbeat() { if (Track != null) SetGrid((Data.Grid ?? new(120, 0, 1)) with { Offset = Seconds, Confidence = 1 }); }
    public void Tap()
    {
        if (Track == null) return;
        if ((DateTime.UtcNow - _lastTap).TotalSeconds > 2) _taps.Clear();
        _lastTap = DateTime.UtcNow;
        _taps.Add(Seconds);
        if (_taps.Count < 5 || _taps[^1] <= _taps[0]) return;
        // least-squares line through the taps
        double n = _taps.Count, mx = (n - 1) / 2, my = _taps.Average(), num = 0, den = 0;
        for (int i = 0; i < n; i++) { num += (i - mx) * (_taps[i] - my); den += (i - mx) * (i - mx); }
        double period = num / den;
        if (period is > 0.15 and < 2) SetGrid(new(Math.Round(60 / period, 2), my - mx * period, 1));
    }
}
