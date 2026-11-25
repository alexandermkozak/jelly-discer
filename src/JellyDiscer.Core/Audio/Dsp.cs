namespace JellyDiscer.Core.Audio;

public static class Dsp
{
    public const double SR = TrackBuffer.SampleRate;
    public static float Hermite(float a, float b, float c, float d, float t)
    {
        float c1 = 0.5f * (c - a), c2 = a - 2.5f * b + 2 * c - 0.5f * d, c3 = 0.5f * (d - a) + 1.5f * (b - c);
        return ((c3 * t + c2) * t + c1) * t + b;
    }
    public static float Db(double db) => (float)Math.Pow(10, db / 20);
    public static double ToDb(double g) => 20 * Math.Log10(Math.Max(g, 1e-6));

    /// <summary>x in 0..1 (0 = full A). curve 0 = constant power, 1 = both decks full except at the ends.</summary>
    public static (float a, float b) Crossfade(float x, float curve)
    {
        x = Math.Clamp(x, 0, 1);
        float pa = MathF.Cos(x * MathF.PI / 2), pb = MathF.Sin(x * MathF.PI / 2);
        float ca = Math.Clamp((1 - x) * 20, 0, 1), cb = Math.Clamp(x * 20, 0, 1);
        return (pa + (ca - pa) * curve, pb + (cb - pb) * curve);
    }
}

/// <summary>Stereo biquad, transposed direct form II.</summary>
public struct Biquad
{
    double b0, b1, b2, a1, a2, l1, l2, r1, r2;
    public enum Kind { Low, High, AllPass }
    public Biquad(Kind kind, double hz, double q = 0.70710678)
    {
        this = default;
        double w = 2 * Math.PI * hz / Dsp.SR, c = Math.Cos(w), al = Math.Sin(w) / (2 * q), a0 = 1 + al;
        (b0, b1, b2) = kind switch
        {
            Kind.Low => ((1 - c) / 2, 1 - c, (1 - c) / 2),
            Kind.High => ((1 + c) / 2, -(1 + c), (1 + c) / 2),
            _ => (1 - al, -2 * c, 1 + al)
        };
        b0 /= a0; b1 /= a0; b2 /= a0;
        a1 = -2 * c / a0;
        a2 = (1 - al) / a0;
    }
    public Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        this = default;
        this.b0 = b0; this.b1 = b1; this.b2 = b2; this.a1 = a1; this.a2 = a2;
    }
    public void Process(ref float l, ref float r)
    {
        double x = l + 1e-20, y = b0 * x + l1;
        l1 = b1 * x - a1 * y + l2; l2 = b2 * x - a2 * y; l = (float)y;
        x = r + 1e-20; y = b0 * x + r1;
        r1 = b1 * x - a1 * y + r2; r2 = b2 * x - a2 * y; r = (float)y;
    }
}

/// <summary>Linkwitz-Riley three-band isolator. Flat when all gains are 1; a gain of 0 removes the band.</summary>
public sealed class Eq3
{
    Biquad _l1 = new(Biquad.Kind.Low, 250), _l2 = new(Biquad.Kind.Low, 250), _ap = new(Biquad.Kind.AllPass, 2500);
    Biquad _h1 = new(Biquad.Kind.High, 250), _h2 = new(Biquad.Kind.High, 250);
    Biquad _m1 = new(Biquad.Kind.Low, 2500), _m2 = new(Biquad.Kind.Low, 2500), _t1 = new(Biquad.Kind.High, 2500), _t2 = new(Biquad.Kind.High, 2500);
    float _low = 1, _mid = 1, _high = 1;

    public void Process(Span<float> s, float low, float mid, float high)
    {
        for (int i = 0; i < s.Length; i += 2)
        {
            _low += (low - _low) * 0.002f; _mid += (mid - _mid) * 0.002f; _high += (high - _high) * 0.002f;
            float ll = s[i], lr = s[i + 1], ml = ll, mr = lr;
            _l1.Process(ref ll, ref lr); _l2.Process(ref ll, ref lr); _ap.Process(ref ll, ref lr);
            _h1.Process(ref ml, ref mr); _h2.Process(ref ml, ref mr);
            float hl = ml, hr = mr;
            _m1.Process(ref ml, ref mr); _m2.Process(ref ml, ref mr);
            _t1.Process(ref hl, ref hr); _t2.Process(ref hl, ref hr);
            s[i] = ll * _low + ml * _mid + hl * _high;
            s[i + 1] = lr * _low + mr * _mid + hr * _high;
        }
    }
}

/// <summary>One-knob filter: negative = low-pass, positive = high-pass, 0 = off.</summary>
public sealed class KnobFilter
{
    float _k, _l1, _l2, _r1, _r2;
    public void Process(Span<float> s, float knob)
    {
        for (int i = 0; i < s.Length; i += 64)
        {
            var c = s.Slice(i, Math.Min(64, s.Length - i));
            _k += (knob - _k) * 0.06f;
            float mag = Math.Abs(_k), wet = Math.Clamp((mag - 0.01f) * 25, 0, 1);
            float hz = _k < 0 ? 18000 * MathF.Pow(100f / 18000, mag) : 20 * MathF.Pow(8000f / 20, mag);
            float g = MathF.Tan(MathF.PI * Math.Min(hz, 20000) / 48000), k = 1.1f, a1 = 1 / (1 + g * (g + k)), a2 = g * a1, a3 = g * a2;
            for (int j = 0; j < c.Length; j += 2)
            {
                c[j] = One(c[j], ref _l1, ref _l2, a1, a2, a3, k, wet);
                c[j + 1] = One(c[j + 1], ref _r1, ref _r2, a1, a2, a3, k, wet);
            }
        }
    }
    float One(float x, ref float s1, ref float s2, float a1, float a2, float a3, float k, float wet)
    {
        float v3 = x - s2 + 1e-20f, v1 = a1 * s1 + a2 * v3, v2 = s2 + a2 * s1 + a3 * v3;
        s1 = 2 * v1 - s1;
        s2 = 2 * v2 - s2;
        float y = _k < 0 ? v2 : x - k * v1 - v2;
        return x + (y - x) * wet;
    }
}

/// <summary>Short-term loudness with K-weighting (BS.1770) and an exponential window.</summary>
public sealed class LoudnessMeter
{
    Biquad _a = new(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);
    Biquad _b = new(1, -2, 1, -1.99004745483398, 0.99007225036621);
    double _ms;
    const double Alpha = 1 / (1.5 * 48000);
    public double Lufs => -0.691 + 10 * Math.Log10(Math.Max(Volatile.Read(ref _ms), 1e-12));
    public void Process(ReadOnlySpan<float> s)
    {
        double m = _ms;
        for (int i = 0; i < s.Length; i += 2)
        {
            float l = s[i], r = s[i + 1];
            _a.Process(ref l, ref r);
            _b.Process(ref l, ref r);
            m += ((double)l * l + (double)r * r - m) * Alpha;
        }
        Volatile.Write(ref _ms, m);
    }
}

public sealed class Meter
{
    float _peak, _rms;
    public float Peak => Volatile.Read(ref _peak);
    public float Rms => Volatile.Read(ref _rms);
    public void Process(ReadOnlySpan<float> s)
    {
        if (s.Length == 0) return;
        float p = 0;
        double q = 0;
        foreach (float v in s) { p = Math.Max(p, Math.Abs(v)); q += v * v; }
        float fall = MathF.Exp(-s.Length / 2 / 48000f / 0.3f);
        Volatile.Write(ref _peak, Math.Max(p, _peak * fall));
        Volatile.Write(ref _rms, MathF.Sqrt((float)(_rms * _rms * fall + q / s.Length * (1 - fall))));
    }
}

/// <summary>Instant-attack peak limiter: output never exceeds the ceiling.</summary>
public sealed class Limiter
{
    const float Ceiling = 0.97f;
    float _g = 1, _min = 1;
    /// <summary>Lowest recent gain; below 1 means the limiter is working.</summary>
    public float Reduction => Volatile.Read(ref _min);
    public void Process(Span<float> s)
    {
        float min = 1;
        for (int i = 0; i < s.Length; i += 2)
        {
            float p = Math.Max(Math.Abs(s[i]), Math.Abs(s[i + 1]));
            _g += (1 - _g) * 0.0002f;
            if (p * _g > Ceiling) _g = Ceiling / p;
            s[i] *= _g;
            s[i + 1] *= _g;
            min = Math.Min(min, _g);
        }
        Volatile.Write(ref _min, Math.Min(min, _min + (1 - _min) * 0.05f));
    }
}

public sealed class DelayLine
{
    readonly float[] _d = new float[2 * 65536];
    int _w;
    public void Process(Span<float> s, int frames)
    {
        frames = Math.Clamp(frames, 0, 65535);
        for (int i = 0; i < s.Length; i += 2)
        {
            _d[_w * 2] = s[i];
            _d[_w * 2 + 1] = s[i + 1];
            int r = (_w - frames) & 65535;
            s[i] = _d[r * 2];
            s[i + 1] = _d[r * 2 + 1];
            _w = (_w + 1) & 65535;
        }
    }
}
