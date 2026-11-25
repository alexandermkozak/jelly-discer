namespace JellyDiscer.Core.Analysis;

public static class TempoStep
{
    /// <summary>Tempo change in percent needed to play <paramref name="to"/> at the speed of <paramref name="from"/>; half and double time count as the same.</summary>
    public static double Percent(double from, double to)
    {
        double r = from / to;
        while (r > Math.Sqrt(2)) r /= 2;
        while (r < Math.Sqrt(0.5)) r *= 2;
        return (r - 1) * 100;
    }
}
