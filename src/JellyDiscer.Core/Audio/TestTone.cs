using System.Runtime.InteropServices;

namespace JellyDiscer.Core.Audio;

/// <summary>A quiet tone on the left, then a higher one on the right, then silence.</summary>
public sealed class TestTone
{
    public const double Seconds = 1.4;
    const int Beep = 28800, Gap = 9600, Ramp = 960; // 0.6 s, 0.2 s, 20 ms
    const float Level = 0.12f;                      // about -18 dBFS
    int _n;
    public bool Started => Volatile.Read(ref _n) > 0;

    /// <summary>Plays on one device, independently of the engine. Returns when it has finished.</summary>
    public static async Task PlayAsync(OutputDevice device)
    {
        var tone = new TestTone();
        var handle = GCHandle.Alloc(tone);
        nint stream = 0;
        try
        {
            stream = Output.StartTone(device, handle);
            await Task.Delay(TimeSpan.FromSeconds(Seconds + 0.5)); // plus the device buffer and wake-up
            if (!tone.Started) throw new InvalidOperationException("Audio output: the device did not start playing.");
        }
        finally
        {
            if (stream != 0) Output.StopTone(stream);
            handle.Free();
        }
    }

    public void Render(Span<float> o)
    {
        for (int i = 0; i < o.Length; i += 2, _n++)
        {
            o[i] = Tone(_n, 440);
            o[i + 1] = Tone(_n - Beep - Gap, 660);
        }
    }
    static float Tone(int n, double hz)
    {
        if (n < 0 || n >= Beep) return 0;
        float fade = Math.Min(1, Math.Min(n, Beep - n) / (float)Ramp);
        return Level * fade * (float)Math.Sin(2 * Math.PI * hz * n / TrackBuffer.SampleRate);
    }
}
