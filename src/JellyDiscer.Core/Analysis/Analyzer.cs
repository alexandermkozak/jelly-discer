using JellyDiscer.Core.Audio;

namespace JellyDiscer.Core.Analysis;

/// <summary>A constant-tempo beatgrid. Offset is the time of a downbeat in seconds.</summary>
public sealed record BeatGrid(double Bpm, double Offset, double Confidence)
{
    public double Period => 60 / Bpm;
    public bool Confident => Confidence >= 0.6;
    public double BeatAt(double seconds) => (seconds - Offset) / Period;
    public double TimeOf(double beat) => Offset + beat * Period;
    public double NearestBeat(double seconds) => TimeOf(Math.Round(BeatAt(seconds)));
    /// <summary>Whole number of beats that lands nearest to a target without going before the start of the track.</summary>
    public int BeatsTo(double now, double target)
    {
        int n = (int)Math.Round((target - now) / Period);
        while (now + n * Period < 0) n++;
        return n;
    }
    /// <summary>Signed distance between the beats of two decks, in beats (-0.5..0.5); positive = a is ahead.</summary>
    public static double PhaseDiff(BeatGrid a, double ta, BeatGrid b, double tb)
    {
        double d = a.BeatAt(ta) - b.BeatAt(tb);
        return d - Math.Round(d);
    }
}

/// <summary>Three bytes (low, mid, high peak) per bin.</summary>
public sealed record Waveform(byte[] Bins)
{
    public const int FramesPerBin = 320; // 150 bins per second
    public int Count => Bins.Length / 3;
}

public static class Analyzer
{
    const int N = 1024, Hop = 256;
    const double EnvRate = 48000.0 / Hop;
    /// <summary>How long before the window centre a spectral-flux onset shows up, measured on synthetic clicks.</summary>
    const double OnsetBias = 0.0075;

    public static Waveform BuildWaveform(TrackBuffer t)
    {
        var s = t.Samples;
        int n = t.Frames / Waveform.FramesPerBin;
        var bins = new byte[n * 3];
        Biquad lo = new(Biquad.Kind.Low, 200), hp = new(Biquad.Kind.High, 200), mid = new(Biquad.Kind.Low, 2500), hi = new(Biquad.Kind.High, 2500);
        for (int b = 0; b < n; b++)
        {
            float pl = 0, pm = 0, ph = 0;
            for (int i = 0; i < Waveform.FramesPerBin; i++)
            {
                int k = (b * Waveform.FramesPerBin + i) * 2;
                float l = s[k], r = s[k + 1];
                float ll = l, lr = r, ml = l, mr = r, hl = l, hr = r;
                lo.Process(ref ll, ref lr);
                hp.Process(ref ml, ref mr); mid.Process(ref ml, ref mr);
                hi.Process(ref hl, ref hr);
                pl = Math.Max(pl, Math.Abs(ll + lr)); pm = Math.Max(pm, Math.Abs(ml + mr)); ph = Math.Max(ph, Math.Abs(hl + hr));
            }
            bins[3 * b] = (byte)Math.Min(255, pl * 127);
            bins[3 * b + 1] = (byte)Math.Min(255, pm * 127);
            bins[3 * b + 2] = (byte)Math.Min(255, ph * 180);
        }
        GC.KeepAlive(t);
        return new(bins);
    }

    public static float[] OnsetEnvelope(TrackBuffer t)
    {
        var s = t.Samples;
        int n = Math.Max(0, (t.Frames - N) / Hop);
        var env = new float[n];
        var re = new float[N];
        var im = new float[N];
        var prev = new float[N / 2];
        var win = new float[N];
        for (int i = 0; i < N; i++) win[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / N);
        var fft = new Fft(N);
        for (int f = 0; f < n; f++)
        {
            for (int i = 0; i < N; i++)
            {
                int k = (f * Hop + i) * 2;
                re[i] = (s[k] + s[k + 1]) * 0.5f * win[i];
                im[i] = 0;
            }
            fft.Forward(re, im);
            float flux = 0;
            for (int k = 1; k < N / 2; k++)
            {
                float m = MathF.Log(1 + 10 * MathF.Sqrt(re[k] * re[k] + im[k] * im[k]));
                if (f > 0 && m > prev[k]) flux += (m - prev[k]) * (k < 6 ? 8 : 1); // weight the kick
                prev[k] = m;
            }
            env[f] = flux;
        }
        GC.KeepAlive(t);
        // remove the local mean so loud sections do not dominate
        var outp = new float[n];
        const int W = 94;
        double sum = 0;
        for (int i = 0; i < Math.Min(W, n); i++) sum += env[i];
        for (int i = 0; i < n; i++)
        {
            if (i + W < n) sum += env[i + W];
            if (i - W - 1 >= 0) sum -= env[i - W - 1];
            int cnt = Math.Min(n, i + W + 1) - Math.Max(0, i - W);
            outp[i] = Math.Max(0, env[i] - (float)(sum / cnt));
        }
        return outp;
    }

    public static BeatGrid? DetectGrid(TrackBuffer t)
    {
        var e = OnsetEnvelope(t);
        if (e.Length < EnvRate * 8) return null;

        // coarse tempo: autocorrelation with harmonics and a broad preference around 138 BPM
        int maxLag = (int)(EnvRate * 60 / 60 * 4) + 2;
        var ac = new double[maxLag + 1];
        int step = Math.Max(1, e.Length / 60000);
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double a = 0;
            for (int i = 0; i + lag < e.Length; i += step) a += e[i] * e[i + lag];
            ac[lag] = a / (e.Length - lag);
        }
        if (ac[0] <= 0) return null;
        double Ac(double lag) { int k = (int)lag; return k + 1 > maxLag ? 0 : ac[k] + (ac[k + 1] - ac[k]) * (lag - k); }
        double best = 0, coarse = 0;
        for (double bpm = 70; bpm < 185; bpm += 0.25)
        {
            double lag = EnvRate * 60 / bpm, score = 0.5 * Ac(lag) + Ac(2 * lag) + 1.5 * Ac(4 * lag);
            double l = Math.Log2(bpm / 138);
            score *= Math.Exp(-0.5 * l * l / 0.5);
            if (score > best) { best = score; coarse = bpm; }
        }
        if (coarse == 0) return null;

        // when the first guess does not hold up, a 3:2 or 4:3 relative may be the real tempo
        var grid = FitGrid(e, coarse);
        if (!grid.Confident)
            foreach (double r in new[] { 2 / 3.0, 0.75, 4 / 3.0, 1.5 })
                if (coarse * r is >= 70 and < 185)
                {
                    var g = FitGrid(e, coarse * r);
                    if (g.Confidence > grid.Confidence + 0.15) grid = g;
                }
        return grid;
    }

    static BeatGrid FitGrid(float[] e, double coarse)
    {
        // fine tempo: spectral peak of the envelope over the whole track, using four harmonics
        double Score(double bpm)
        {
            double total = 0;
            for (int h = 1; h <= 4; h++)
            {
                double w = 2 * Math.PI * h * bpm / 60 / EnvRate, cr = Math.Cos(w), ci = Math.Sin(w), x = 1, y = 0, sr = 0, si = 0;
                foreach (float v in e)
                {
                    sr += v * x; si += v * y;
                    (x, y) = (x * cr - y * ci, x * ci + y * cr);
                }
                total += Math.Sqrt(sr * sr + si * si);
            }
            return total;
        }
        double seconds = e.Length / EnvRate, fineStep = Math.Max(0.005, 60 / seconds / 12);
        double bestBpm = coarse, bestScore = 0;
        for (double b = coarse * 0.985; b <= coarse * 1.015; b += fineStep)
        {
            double sc = Score(b);
            if (sc > bestScore) { bestScore = sc; bestBpm = b; }
        }
        for (double st = fineStep / 2; st > 0.0005; st /= 2)
            foreach (double b in new[] { bestBpm - st, bestBpm + st })
            {
                double sc = Score(b);
                if (sc > bestScore) { bestScore = sc; bestBpm = b; }
            }
        double snapped = Math.Round(bestBpm); // most electronic music has a whole-number tempo
        if (Math.Abs(snapped - bestBpm) < 0.03 && Score(snapped) > bestScore * 0.985) bestBpm = snapped;

        // phase: fold the envelope over one beat
        double period = EnvRate * 60 / bestBpm;
        double phase = FoldPeak(e, 0, e.Length, period, out _);

        // confidence: energy-weighted share of 8 s sections that agree with the grid.
        // Folded on half beats, so a section led by off-beat hats still counts as on the grid.
        int sec = (int)(EnvRate * 8);
        double agree = 0, weight = 0;
        for (int i = 0; i + sec <= e.Length; i += sec)
        {
            double p = FoldPeak(e, i, sec, period / 2, out double contrast);
            double d = p - phase;
            d -= Math.Round(d / (period / 2)) * (period / 2);
            double energy = 0;
            for (int k = i; k < i + sec; k++) energy += e[k];
            weight += energy;
            // a section that fits a slightly different tempo better is drifting through the grid, not following it
            double own = Pull(e, i, sec, period / 2);
            bool steady = own * 1.08 >= Math.Max(Pull(e, i, sec, period / 2 * 1.012), Pull(e, i, sec, period / 2 / 1.012));
            if (Math.Abs(d) / EnvRate < 0.025 && contrast > 1.5 && steady) agree += energy;
        }
        double conf = weight > 0 ? agree / weight : 0;
        double offset = phase / EnvRate + N / 2.0 / 48000 + OnsetBias;

        // downbeat: the strongest of every four beats
        double off4 = offset, bs = -1;
        for (int b = 0; b < 4; b++)
        {
            double sum = 0;
            for (double p = phase + b * period; p < e.Length - 2; p += period * 4) sum += e[(int)p] + e[(int)p + 1];
            if (sum > bs * 1.05) { bs = sum; off4 = offset + b * 60 / bestBpm; }
        }
        double bar = 240 / bestBpm;
        off4 -= Math.Floor(off4 / bar) * bar;
        return new BeatGrid(Math.Round(bestBpm, 3), off4, conf);
    }

    /// <summary>Strength of one periodicity in a section.</summary>
    static double Pull(float[] e, int start, int len, double period)
    {
        double w = 2 * Math.PI / period, cr = Math.Cos(w), ci = Math.Sin(w), x = 1, y = 0, sr = 0, si = 0;
        for (int i = start; i < start + len; i++)
        {
            sr += e[i] * x; si += e[i] * y;
            (x, y) = (x * cr - y * ci, x * ci + y * cr);
        }
        return Math.Sqrt(sr * sr + si * si);
    }

    static double FoldPeak(float[] e, int start, int len, double period, out double contrast)
    {
        const int B = 128;
        Span<double> h = stackalloc double[B], sm = stackalloc double[B];
        h.Clear();
        for (int i = start; i < start + len; i++)
        {
            double f = i / period;
            double x = (f - Math.Floor(f)) * B;
            int k = (int)x;
            double fr = x - k;
            h[k % B] += e[i] * (1 - fr);
            h[(k + 1) % B] += e[i] * fr;
        }
        int best = 0;
        double mean = 0;
        for (int i = 0; i < B; i++)
        {
            sm[i] = h[(i + B - 2) % B] + 2 * h[(i + B - 1) % B] + 3 * h[i] + 2 * h[(i + 1) % B] + h[(i + 2) % B];
            mean += sm[i] / B;
            if (sm[i] > sm[best]) best = i;
        }
        contrast = mean > 0 ? sm[best] / mean : 0;
        double a = sm[(best + B - 1) % B], b = sm[best], c = sm[(best + 1) % B], den = a - 2 * b + c;
        double frac = Math.Abs(den) > 1e-12 ? 0.5 * (a - c) / den : 0;
        return (best + frac) / B * period;
    }
}

public sealed class Fft
{
    readonly int _n;
    readonly float[] _c, _s;
    readonly int[] _rev;
    public Fft(int n)
    {
        _n = n;
        _c = new float[n / 2]; _s = new float[n / 2]; _rev = new int[n];
        for (int i = 0; i < n / 2; i++) { _c[i] = MathF.Cos(2 * MathF.PI * i / n); _s[i] = -MathF.Sin(2 * MathF.PI * i / n); }
        int bits = System.Numerics.BitOperations.Log2((uint)n);
        for (int i = 0; i < n; i++) { int r = 0; for (int b = 0; b < bits; b++) if ((i >> b & 1) != 0) r |= 1 << (bits - 1 - b); _rev[i] = r; }
    }
    public void Forward(float[] re, float[] im)
    {
        for (int i = 0; i < _n; i++) { int j = _rev[i]; if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); } }
        for (int len = 2; len <= _n; len <<= 1)
        {
            int half = len / 2, st = _n / len;
            for (int i = 0; i < _n; i += len)
                for (int k = 0; k < half; k++)
                {
                    float c = _c[k * st], s = _s[k * st];
                    int a = i + k, b = a + half;
                    float tr = re[b] * c - im[b] * s, ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
    }
}
