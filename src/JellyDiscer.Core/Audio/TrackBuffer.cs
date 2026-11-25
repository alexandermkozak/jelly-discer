using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JellyDiscer.Core.Audio;

/// <summary>Interleaved stereo 48 kHz float audio in native memory. Freed by the finalizer, so the
/// audio thread can never see memory released under it.</summary>
public sealed class TrackBuffer
{
    public const int SampleRate = 48000;
    readonly unsafe float* _data;
    readonly long _bytes;
    public int Frames { get; }
    public double Seconds => Frames / (double)SampleRate;

    public unsafe TrackBuffer(int frames)
    {
        Frames = frames;
        _bytes = Math.Max(8, frames * 8L);
        _data = (float*)NativeMemory.AllocZeroed((nuint)_bytes);
        GC.AddMemoryPressure(_bytes);
    }

    unsafe ~TrackBuffer()
    {
        NativeMemory.Free(_data);
        GC.RemoveMemoryPressure(_bytes);
    }

    /// <summary>The caller must keep this object alive while using the span.</summary>
    public unsafe Span<float> Samples => new(_data, Frames * 2);

    public static TrackBuffer FromSamples(ReadOnlySpan<float> interleaved)
    {
        var t = new TrackBuffer(interleaved.Length / 2);
        interleaved[..(t.Frames * 2)].CopyTo(t.Samples);
        GC.KeepAlive(t);
        return t;
    }

    /// <summary>Decodes any file ffmpeg understands.</summary>
    public static async Task<TrackBuffer> DecodeAsync(string path, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-nostdin", "-v", "error", "-i", "file:" + path, "-map", "0:a:0", "-f", "f32le", "-ac", "2", "-ar", "48000", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg");
        try
        {
            var err = p.StandardError.ReadToEndAsync(ct);
            var chunks = new List<byte[]>();
            long total = 0;
            while (true)
            {
                var b = new byte[1 << 20];
                int n = await p.StandardOutput.BaseStream.ReadAtLeastAsync(b, b.Length, false, ct);
                if (n == 0) break;
                if (n < b.Length) Array.Resize(ref b, n);
                chunks.Add(b);
                total += n;
                if (total > 2_000_000_000) throw new InvalidDataException("Track is too long to load (limit about 85 minutes).");
            }
            await p.WaitForExitAsync(ct);
            var msg = await err;
            if (p.ExitCode != 0 || total < 8) throw new InvalidDataException("ffmpeg could not decode the track: " + msg.Trim());
            var t = new TrackBuffer((int)(total / 8));
            Copy(t, chunks);
            return t;
        }
        finally { if (!p.HasExited) p.Kill(); }
    }

    static void Copy(TrackBuffer t, List<byte[]> chunks)
    {
        var dst = MemoryMarshal.AsBytes(t.Samples);
        int o = 0;
        foreach (var c in chunks)
        {
            int n = Math.Min(c.Length, dst.Length - o);
            c.AsSpan(0, n).CopyTo(dst[o..]);
            o += n;
        }
        GC.KeepAlive(t);
    }
}
