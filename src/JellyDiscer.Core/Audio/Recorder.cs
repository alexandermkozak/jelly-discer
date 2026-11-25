namespace JellyDiscer.Core.Audio;

/// <summary>Writes the master mix to a 16-bit WAV file from a background thread.</summary>
public sealed class Recorder : IDisposable
{
    readonly Engine _e;
    readonly FileStream _f;
    readonly Thread _t;
    volatile bool _stop;
    long _bytes;
    public string Path { get; }
    public Recorder(Engine e, string path)
    {
        _e = e;
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _f = new FileStream(path, FileMode.CreateNew);
        Header();
        e.RecordRing.Skip(e.RecordRing.Fill);
        e.Recording = true;
        _t = new Thread(Run) { IsBackground = true, Name = "recorder" };
        _t.Start();
    }
    void Header()
    {
        uint n = (uint)Math.Min(_bytes, uint.MaxValue - 36);
        using var w = new BinaryWriter(_f, System.Text.Encoding.ASCII, true);
        w.Write("RIFF"u8); w.Write(n + 36); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(48000); w.Write(192000); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(n);
    }
    void Run()
    {
        var b = new float[8192];
        var o = new byte[b.Length * 2];
        while (true)
        {
            bool last = _stop;
            int n;
            while ((n = _e.RecordRing.Read(b)) > 0)
            {
                for (int i = 0; i < n * 2; i++)
                    BitConverter.TryWriteBytes(o.AsSpan(i * 2), (short)Math.Clamp(b[i] * 32767, -32768, 32767));
                _f.Write(o, 0, n * 4);
                _bytes += n * 4;
            }
            if (last) return;
            Thread.Sleep(50);
        }
    }
    public void Dispose()
    {
        if (_stop) return;
        _e.Recording = false;
        Thread.Sleep(30);
        _stop = true;
        _t.Join();
        _f.Position = 0;
        Header();
        _f.Dispose();
    }
}
