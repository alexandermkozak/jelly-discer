using System.Runtime;
using Avalonia;
using JellyDiscer.Core.Audio;

namespace JellyDiscer.App;

static partial class Program
{
    /// <summary>jelly-discer --soak MASTER CUE SECONDS: silent two-device run, for checking dropouts and drift.</summary>
    static int Soak(string[] a)
    {
        var devs = Output.Devices();
        foreach (var d in devs) Console.WriteLine($"{d.Index}: {d.Name}");
        if (a.Length < 4) return 0;
        var m = devs.First(d => d.Name.Contains(a[1]));
        var c = devs.First(d => d.Name.Contains(a[2]));
        var e = new Engine { MasterGain = 0, CueGain = 0 };
        var noise = new float[48000 * 2 * 60];
        for (int i = 0; i < noise.Length; i++) noise[i] = Random.Shared.NextSingle() * 0.1f;
        for (int d = 0; d < 2; d++)
        {
            e.Send(d, DeckOp.Load, track: TrackBuffer.FromSamples(noise));
            e.Send(d, DeckOp.LoopOn, 0, 48000 * 50);
            e.Decks[d].KeyLock = true;
            e.Decks[d].Tempo = 0.03f;
            e.Send(d, DeckOp.Play);
        }
        using var o = new Output(e, m, c);
        for (int s = 0; s < int.Parse(a[3]); s++)
        {
            Thread.Sleep(1000);
            if (s % 10 == 9 || s < 3) Console.WriteLine($"{s + 1}s underruns master {o.MasterUnderruns} cue {o.CueUnderruns} fill {o.CueFill:F0}/{o.CueTarget} drift {o.DriftPpm:F0} ppm pos {e.Decks[0].Position / 48000:F1}");
        }
        return 0;
    }

    [STAThread]
    static int Main(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        if (args.Length > 0 && args[0] == "--soak") return Soak(args);
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
    }
}
