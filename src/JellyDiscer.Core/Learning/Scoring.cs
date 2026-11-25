namespace JellyDiscer.Core.Learning;

public static class Scoring
{
    /// <summary>0..100. Half for tempo (0 at 1 % off), half for phase (0 at 60 ms off), minus up to 20 for taking over a minute.</summary>
    public static int Beatmatch(double tempoErrorPct, double phaseErrorMs, double seconds)
    {
        double t = 50 * Math.Max(0, 1 - Math.Max(0, Math.Abs(tempoErrorPct) - 0.02) / 0.98);
        double p = 50 * Math.Max(0, 1 - Math.Max(0, Math.Abs(phaseErrorMs) - 5) / 55);
        return (int)Math.Round(Math.Max(0, t + p - Math.Clamp((seconds - 60) / 6, 0, 20)));
    }
    /// <summary>0..100: full marks within 0.5 LU, none at 6 LU or more.</summary>
    public static int Level(double errorLu) => (int)Math.Round(100 * Math.Max(0, 1 - Math.Max(0, Math.Abs(errorLu) - 0.5) / 5.5));
}
