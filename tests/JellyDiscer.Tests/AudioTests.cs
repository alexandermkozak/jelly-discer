using JellyDiscer.Core.Audio;
using JellyDiscer.Core.Analysis;
using JellyDiscer.Core.Learning;
namespace JellyDiscer.Tests;

public class AudioTests
{
    public static float[] Sine(double hz, double sec, float amp = 0.25f)
    {
        var s = new float[(int)(sec * 48000) * 2];
        for (int i = 0; i < s.Length / 2; i++) s[2 * i] = s[2 * i + 1] = amp * (float)Math.Sin(2 * Math.PI * hz * i / 48000);
        return s;
    }
    static double Rms(ReadOnlySpan<float> s) { double q = 0; foreach (var v in s) q += v * v; return Math.Sqrt(q / s.Length); }
    static float[] Run(Engine e, int frames, Action<int>? at = null)
    {
        var o = new float[frames * 2];
        var c = new float[Engine.MaxBlock * 2];
        for (int i = 0; i < frames; i += 512)
        {
            at?.Invoke(i);
            int n = Math.Min(512, frames - i) * 2;
            e.Render(o.AsSpan(i * 2, n), c.AsSpan(0, n));
        }
        return o;
    }
    /// <summary>Goertzel-style level of one frequency.</summary>
    static double Level(ReadOnlySpan<float> s, double hz)
    {
        double a = 0, b = 0; int n = s.Length / 2;
        for (int i = 0; i < n; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n); a += w * s[2 * i] * Math.Cos(2 * Math.PI * hz * i / 48000); b += w * s[2 * i] * Math.Sin(2 * Math.PI * hz * i / 48000); }
        return 4 * Math.Sqrt(a * a + b * b) / n;
    }

    [Theory] [InlineData(40)] [InlineData(250)] [InlineData(800)] [InlineData(2500)] [InlineData(10000)]
    public void EqIsFlatAtUnity(double hz)
    {
        var s = Sine(hz, 1);
        double before = Rms(s);
        new Eq3().Process(s, 1, 1, 1);
        Assert.InRange(Dsp.ToDb(Rms(s.AsSpan(48000)) / before), -0.3, 0.3);
    }
    [Theory] [InlineData(60, 0, 1, 1)] [InlineData(800, 1, 0, 1)] [InlineData(10000, 1, 1, 0)]
    public void EqKillRemovesBand(double hz, float l, float m, float h)
    {
        var s = Sine(hz, 1);
        double before = Rms(s);
        new Eq3().Process(s, l, m, h);
        Assert.True(Dsp.ToDb(Rms(s.AsSpan(48000)) / before) < -18);
    }
    [Fact]
    public void KnobFilter()
    {
        double F(double hz, float k) { var s = Sine(hz, 1); double b = Rms(s); new KnobFilter().Process(s, k); return Dsp.ToDb(Rms(s.AsSpan(48000)) / b); }
        Assert.InRange(F(1000, 0), -0.01, 0.01);
        Assert.True(F(8000, -0.8f) < -20); Assert.InRange(F(50, -0.5f), -1.5, 1.5);
        Assert.True(F(60, 0.8f) < -20); Assert.InRange(F(12000, 0.5f), -1.5, 1.5);
    }
    [Fact]
    public void CrossfaderCurves()
    {
        Assert.Equal((1f, 0f), Dsp.Crossfade(0, 0));
        var (a, b) = Dsp.Crossfade(0.5f, 0);
        Assert.Equal(1, a * a + b * b, 3);
        Assert.Equal((1f, 1f), Dsp.Crossfade(0.5f, 1));
        Assert.Equal(0, Dsp.Crossfade(1, 1).a);
    }
    [Fact]
    public void LimiterNeverExceedsCeiling()
    {
        var s = Sine(100, 1, 4);
        var l = new Limiter(); l.Process(s);
        Assert.True(s.Max(Math.Abs) <= 0.971); Assert.True(l.Reduction < 0.9);
    }
    [Theory] [InlineData(0.05f, false)] [InlineData(-0.08f, false)] [InlineData(0.05f, true)] [InlineData(-0.08f, true)]
    public void TempoMovesPositionAndKeyLockKeepsPitch(float tempo, bool key)
    {
        var e = new Engine(); var d = e.Decks[0];
        if (key && !d.KeyLockAvailable) return;
        e.Crossfader = 0; d.Tempo = tempo; d.KeyLock = key;
        e.Send(0, DeckOp.Load, track: TrackBuffer.FromSamples(Sine(1000, 20))); e.Send(0, DeckOp.Play);
        Run(e, 48000);
        double p = d.Position; var o = Run(e, 48000 * 4);
        Assert.InRange((d.Position - p) / (48000 * 4), 1 + tempo - 0.004, 1 + tempo + 0.004);
        double same = Level(o, 1000), shifted = Level(o, 1000 * (1 + tempo));
        Assert.True(key ? same > 10 * shifted : shifted > 10 * same, $"1000Hz {same}, shifted {shifted}");
    }
    [Fact]
    public void KeyLockKeepsTiming()
    {
        // a burst at 2 s must be heard when the deck reports 2 s
        var s = new float[48000 * 2 * 6];
        var burst = Sine(600, 0.2);
        burst.CopyTo(s, 48000 * 2 * 2);
        var e = new Engine(); var d = e.Decks[0];
        if (!d.KeyLockAvailable) return;
        e.Crossfader = 0; d.KeyLock = true; d.Tempo = 0.06f;
        e.Send(0, DeckOp.Load, track: TrackBuffer.FromSamples(s)); e.Send(0, DeckOp.Play);
        double heard = -1; var m = new float[128]; var c = new float[128];
        for (int i = 0; i < 48000 * 4 / 64 && heard < 0; i++) { e.Render(m, c); if (m.Max(Math.Abs) > 0.05) heard = d.Position; }
        Assert.InRange(heard / 48000, 1.985, 2.015);
    }
    [Fact]
    public void LoopStaysInside()
    {
        var e = new Engine(); var d = e.Decks[0];
        e.Send(0, DeckOp.Load, track: TrackBuffer.FromSamples(Sine(200, 10)));
        e.Send(0, DeckOp.LoopOn, 48000, 72000); e.Send(0, DeckOp.HotPlay, 50000);
        Run(e, 48000 * 5, i => { if (i > 1024) Assert.InRange(d.Position, 48000, 72000); });
        Assert.True(d.Looping);
        // 5 s at unity, minus short fade-in: position is exact modulo the loop
        Assert.InRange(d.Position, 48000 + (2000 + 240000) % 24000 - 300, 48000 + (2000 + 240000) % 24000 + 300);
        e.Send(0, DeckOp.LoopOff); Run(e, 48000); Assert.True(d.Position > 72000 || !d.Looping);
    }
    [Fact]
    public void CueButtonBehaviour()
    {
        var e = new Engine(); var d = e.Decks[0];
        e.Send(0, DeckOp.Load, track: TrackBuffer.FromSamples(Sine(200, 10)));
        e.Send(0, DeckOp.Seek, 96000); Run(e, 2048);
        e.Send(0, DeckOp.CueDown); Run(e, 2048); Assert.Equal(96000, d.CuePoint); Assert.False(d.Playing);
        e.Send(0, DeckOp.CueUp); e.Send(0, DeckOp.CueDown); Run(e, 9600); Assert.True(d.Playing);
        e.Send(0, DeckOp.CueUp); Run(e, 2048); Assert.False(d.Playing); Assert.Equal(96000, d.Position);
        e.Send(0, DeckOp.Play); Run(e, 9600); e.Send(0, DeckOp.CueDown); Run(e, 2048);
        Assert.False(d.Playing); Assert.Equal(96000, d.Position);
    }
    [Fact]
    public void SplitMonoAndDelay()
    {
        var e = new Engine { PhoneMode = PhoneMode.SplitMono, Crossfader = 1 };
        e.Send(0, DeckOp.Load, track: TrackBuffer.FromSamples(Sine(500, 5))); e.Send(0, DeckOp.Play);
        e.Channels[0].Pfl = true;
        var m = new float[1024]; var c = new float[1024];
        for (int i = 0; i < 100; i++) e.Render(m, c);
        Assert.True(Enumerable.Range(0, 512).Max(i => Math.Abs(c[2 * i])) > 0.1);
        Assert.True(Enumerable.Range(0, 512).Max(i => Math.Abs(c[2 * i + 1])) < 0.001);
    }
    [Theory] [InlineData(200)] [InlineData(-200)] [InlineData(0)]
    public void DriftIsCorrected(double ppm)
    {
        var ring = new StereoRing(1 << 15); var r = new DriftReader(ring);
        var w = new float[1024]; var o = new float[1024];
        double tw = 0, tr = 0, dw = 512.0 / 48000, dr = dw / (1 + ppm * 1e-6), min = 1e9, max = 0, lo = 9, hi = 0;
        while (tr < 600)
        {
            if (tw <= tr) { ring.Write(w); tw += dw; }
            else { r.Read(o); tr += dr; if (tr > 120) { min = Math.Min(min, r.AverageFill); max = Math.Max(max, r.AverageFill); } if (tr > 400) { lo = Math.Min(lo, r.Ratio); hi = Math.Max(hi, r.Ratio); } }
        }
        Assert.Equal(0, r.Underruns);
        Assert.InRange(min, r.Target - 600, r.Target + 600); Assert.InRange(max, r.Target - 600, r.Target + 600);
        Assert.InRange(hi - lo, 0, 0.0005); // less than one cent of wobble
        Assert.InRange((lo + hi) / 2, 1 - ppm * 1e-6 - 5e-5, 1 - ppm * 1e-6 + 5e-5);
    }
    [Theory] [InlineData(512, 512, 0)] [InlineData(2048, 512, 0.011)] [InlineData(512, 1536, 0.004)] [InlineData(1024, 1024, 0.02)]
    public void CueBufferSettlesWithinFourSeconds(int wb, int rb, double phase)
    {
        var ring = new StereoRing(1 << 15); var r = new DriftReader(ring);
        var w = new float[wb * 2]; var o = new float[rb * 2];
        for (int i = 0; i < w.Length; i++) w[i] = 0.5f;
        double tw = 0, tr = phase, sum = 0; int n = 0; float maxStep = 0, prev = 0;
        while (tr < 30)
        {
            if (tw <= tr) { ring.Write(w); tw += wb / 48000.0; continue; }
            if (tr > 4) { sum += ring.Fill; n++; }
            r.Read(o);
            for (int i = 0; i < rb; i++) { maxStep = Math.Max(maxStep, Math.Abs(o[2 * i] - prev)); prev = o[2 * i]; }
            tr += rb / 48000.0 * (1 + 30e-6);
        }
        Assert.True(r.Settled); Assert.Equal(0, r.Underruns);
        Assert.InRange(sum / n, r.Target - 96, r.Target + 96);   // 2 ms
        Assert.InRange(r.AverageFill, r.Target - 144, r.Target + 144);
        Assert.True(maxStep < 0.01, $"click of {maxStep}");
    }
    [Fact]
    public void RenderDoesNotAllocate()
    {
        var e = new Engine();
        for (int d = 0; d < 2; d++) { e.Send(d, DeckOp.Load, track: TrackBuffer.FromSamples(Sine(300, 30))); e.Send(d, DeckOp.Play); e.Channels[d].Pfl = true; e.Decks[d].Tempo = 0.03f; }
        e.Decks[1].KeyLock = true; e.Recording = true;
        var m = new float[1024]; var c = new float[1024];
        var dr = new DriftReader(e.RecordRing);
        for (int i = 0; i < 400; i++) { e.Render(m, c); dr.Read(c); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 400; i++) { e.Render(m, c); dr.Read(c); }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
    [Fact]
    public async Task RecorderWritesWav()
    {
        var e = new Engine { Crossfader = 0 };
        e.Send(0, DeckOp.Load, track: TrackBuffer.FromSamples(Sine(300, 5))); e.Send(0, DeckOp.Play);
        string p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            using (var r = new Recorder(e, p)) Run(e, 48000 * 2);
            Assert.Equal(44 + 48000 * 2 * 4, new FileInfo(p).Length);
            var t = await TrackBuffer.DecodeAsync(p); Assert.Equal(96000, t.Frames);
        }
        finally { File.Delete(p); }
    }
    [Fact]
    public void LoudnessMatchesReference()
    {
        var m = new LoudnessMeter(); m.Process(Sine(997, 20, (float)Math.Pow(10, -20 / 20.0)));
        Assert.InRange(m.Lufs, -20.3, -19.7); // stereo -20 dBFS peak sine is -20 LUFS
    }
    [Fact]
    public void TestToneIsLeftThenRightAndQuiet()
    {
        var o = new float[48000 * 2 * 2];
        new TestTone().Render(o);
        float Max(int ch, double a, double b) => Enumerable.Range((int)(a * 48000), (int)((b - a) * 48000)).Max(i => Math.Abs(o[2 * i + ch]));
        Assert.InRange(Max(0, 0, 0.6), 0.1f, 0.121f); Assert.Equal(0, Max(1, 0, 0.8));
        Assert.Equal(0, Max(0, 0.6, 2)); Assert.InRange(Max(1, 0.8, 1.4), 0.1f, 0.121f); Assert.Equal(0, Max(1, TestTone.Seconds, 2));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void WholeBeatJumpKeepsPhase(bool key)
    {
        var e = new Engine();
        if (key && !e.Decks[0].KeyLockAvailable) return;
        var t = TrackBuffer.FromSamples(Sine(200, 60));
        var g = new BeatGrid(127.3, 0.21, 1);
        for (int d = 0; d < 2; d++) { e.Decks[d].Tempo = 0.04f; e.Decks[d].KeyLock = key; e.Send(d, DeckOp.Load, track: t); e.Send(d, DeckOp.HotPlay, 48000 * 20); }
        Run(e, 48000);
        double P() => BeatGrid.PhaseDiff(g, e.Decks[0].Position / 48000, g, e.Decks[1].Position / 48000) * g.Period * 1000;
        double before = P();
        foreach (double cue in new[] { 40.123, 5.077, 0.0 })
        {
            e.Send(0, DeckOp.JumpBy, g.BeatsTo(e.Decks[0].Position / 48000, cue) * g.Period * 48000);
            Run(e, 4800);
            Assert.InRange(e.Decks[0].Position / 48000 - cue, -0.2, 0.7);
            Assert.InRange(P() - before, -1.0, 1.0); // ms
        }
    }
    [Fact]
    public void BeatsToNeverGoesBeforeTheStart()
    {
        var g = new BeatGrid(120, 0.1, 1);
        Assert.Equal(-19, g.BeatsTo(9.9, 0)); Assert.Equal(-20, g.BeatsTo(10.1, 0)); Assert.Equal(4, g.BeatsTo(10, 12.2));
        Assert.Equal(2.1, g.NearestBeat(2.2), 9);
    }
    [Fact]
    public void Scores()
    {
        Assert.Equal(100, Scoring.Beatmatch(0.01, 3, 30)); Assert.Equal(0, Scoring.Beatmatch(3, 200, 10));
        Assert.True(Scoring.Beatmatch(0.01, 3, 300) == 80); Assert.Equal(100, Scoring.Level(0.4)); Assert.Equal(0, Scoring.Level(-7));
    }
}

public class AnalysisTests
{
    static TrackBuffer Clicks(double bpm, double offset, double sec, Func<double, double>? warp = null)
    {
        var s = new float[(int)(sec * 48000) * 2];
        var rnd = new Random(1);
        for (int b = 0; ; b++)
        {
            double t = offset + b * 60 / bpm;
            if (warp != null) t = warp(t);
            int k = (int)(t * 48000);
            if (k + 4800 >= s.Length / 2) break;
            float g = b % 4 == 0 ? 0.8f : 0.5f;
            for (int i = 0; i < 4800; i++)
            {
                double x = i / 48000.0;
                float v = g * (float)(Math.Sin(2 * Math.PI * 60 * x) * Math.Exp(-x * 30) + 0.4 * (rnd.NextDouble() - 0.5) * Math.Exp(-x * 300));
                s[2 * (k + i)] += v; s[2 * (k + i) + 1] += v;
            }
        }
        return TrackBuffer.FromSamples(s);
    }
    [Theory] [InlineData(128, 0.2)] [InlineData(174, 0.31)] [InlineData(124.37, 0.05)] [InlineData(95.5, 0.4)] [InlineData(140, 0.113)]
    public void FindsGrid(double bpm, double off)
    {
        var g = Analyzer.DetectGrid(Clicks(bpm, off, 120))!;
        double d = (g.Offset - off) / (60 / bpm); d -= Math.Round(d);
        Assert.True(Math.Abs(g.Bpm - bpm) < 0.05, $"bpm {g.Bpm}");
        Assert.True(Math.Abs(d * 60 / bpm) < 0.010, $"phase {d * 60000 / bpm:F1} ms");
        Assert.True(g.Confident, $"conf {g.Confidence}");
        double bar = (g.Offset - off) / (240 / bpm); bar -= Math.Round(bar);
        Assert.True(Math.Abs(bar) < 0.1, "downbeat");
    }
    [Fact]
    public void DriftingTempoIsNotConfident()
    {
        var g = Analyzer.DetectGrid(Clicks(120, 0.1, 120, t => t + 0.17 * Math.Sin(t / 7)))!;
        Assert.False(g.Confident, g.ToString());
    }
    [Fact]
    public void StoreRoundTrip()
    {
        string p = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var s = new Store(p);
            s.Save("a", new TrackData { Grid = new(128, 0.5, 0.9), Waveform = new([1, 2, 3]), Cue = 4, HotCues = [1, -1, 2, -1] });
            s.Save("b", new TrackData());
            Assert.Equal(128, s.Get("a")!.Grid!.Bpm); Assert.Equal(2, s.Get("a")!.HotCues[2]); Assert.Null(s.Get("b")!.Grid); Assert.Single(s.Grids());
            s.AddDrill(new(DateTime.UtcNow, "Beatmatch", 1, 2, 0, 3, 90)); Assert.Equal(90, s.Drills()[0].Score);
        }
        finally { File.Delete(p); }
    }
}
