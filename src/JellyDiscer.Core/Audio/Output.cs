using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace JellyDiscer.Core.Audio;

public sealed record OutputDevice(int Index, string Name);

/// <summary>PortAudio output. The master stream is the clock; the cue stream follows it through a drift-correcting ring.</summary>
public sealed unsafe class Output : IDisposable
{
    const string Lib = "libportaudio.so.2";
    [StructLayout(LayoutKind.Sequential)]
    struct StreamParams { public int Device, Channels; public CULong Format; public double Latency; public nint Specific; }
    [StructLayout(LayoutKind.Sequential)]
    struct DeviceInfo { public int Version; public nint Name; public int HostApi, MaxIn, MaxOut; public double LowIn, LowOut, HighIn, HighOut, Rate; }
    [StructLayout(LayoutKind.Sequential)]
    struct HostApiInfo { public int Version, Type; public nint Name; public int DeviceCount, DefaultIn, DefaultOut; }
    [DllImport(Lib)] static extern int Pa_Initialize();
    [DllImport(Lib)] static extern int Pa_Terminate();
    [DllImport(Lib)] static extern int Pa_GetDeviceCount();
    [DllImport(Lib)] static extern DeviceInfo* Pa_GetDeviceInfo(int i);
    [DllImport(Lib)] static extern HostApiInfo* Pa_GetHostApiInfo(int i);
    [DllImport(Lib)] static extern int Pa_OpenStream(out nint s, nint input, StreamParams* output, double rate, CULong frames, CULong flags,
        delegate* unmanaged[Cdecl]<nint, float*, CULong, nint, CULong, nint, int> cb, nint user);
    [DllImport(Lib)] static extern int Pa_StartStream(nint s);
    [DllImport(Lib)] static extern int Pa_AbortStream(nint s);
    [DllImport(Lib)] static extern int Pa_CloseStream(nint s);
    [DllImport(Lib)] static extern nint Pa_GetErrorText(int e);
    static readonly int InitError = Pa_Initialize();

    readonly Engine _engine;
    readonly StereoRing _ring = new(1 << 15);
    readonly DriftReader _drift;
    readonly float[] _master = new float[Engine.MaxBlock * 2], _cue = new float[Engine.MaxBlock * 2];
    GCHandle _self;
    nint _m, _c;
    int _masterUnderruns, _cueUnderruns;
    public int MasterUnderruns => Volatile.Read(ref _masterUnderruns);
    public int CueUnderruns => Volatile.Read(ref _cueUnderruns) + _drift.Underruns;
    public double CueFill => _drift.AverageFill;
    public int CueTarget => _drift.Target;
    public double DriftPpm => (_drift.Ratio - 1) * 1e6;
    public bool HasCue => _c != 0;

    public static IReadOnlyList<OutputDevice> Devices()
    {
        Check(InitError);
        var all = new List<(OutputDevice d, string api)>();
        for (int i = 0; i < Pa_GetDeviceCount(); i++)
        {
            var d = Pa_GetDeviceInfo(i);
            if (d == null || d->MaxOut < 2) continue;
            all.Add((new(i, Marshal.PtrToStringUTF8(d->Name) ?? "?"), Marshal.PtrToStringUTF8(Pa_GetHostApiInfo(d->HostApi)->Name) ?? ""));
        }
        // raw ALSA entries bypass PipeWire and need exclusive access, so offer the sound server's devices when there are any
        var pulse = all.Where(x => !x.api.Contains("ALSA") && !x.api.Contains("JACK") && !x.api.Contains("OSS")).ToList();
        return (pulse.Count > 0 ? pulse : all).Select(x => x.d).ToList();
    }

    public Output(Engine engine, OutputDevice master, OutputDevice? cue)
    {
        Check(InitError);
        _engine = engine;
        _drift = new DriftReader(_ring);
        _self = GCHandle.Alloc(this);
        Warm();
        if (cue?.Index == master.Index) cue = null;
        engine.SingleDevice = cue == null;
        engine.BaseOffsetMs = -_drift.Target * 1000f / TrackBuffer.SampleRate; // the cue path waits in the ring
        try
        {
            if (cue != null) _c = Open(cue, &CueCallback);
            _m = Open(master, &MasterCallback);
            Check(Pa_StartStream(_m));
            if (_c != 0) Check(Pa_StartStream(_c));
        }
        catch { Dispose(); throw; }
    }

    static bool _warmed;
    /// <summary>Runs every render path on a scratch engine so nothing is compiled for the first time inside a callback.</summary>
    static void Warm()
    {
        if (_warmed) return;
        _warmed = true;
        var e = new Engine { Recording = true };
        var t = new TrackBuffer(48000 * 4);
        var m = new float[1024];
        var c = new float[1024];
        var drift = new DriftReader(e.RecordRing);
        for (int d = 0; d < 2; d++)
        {
            e.Decks[d].KeyLock = d == 1;
            e.Channels[d].Pfl = true;
            e.Channels[d].Filter = d - 0.5f;
            e.Send(d, DeckOp.Load, 0, 0, t);
            e.Send(d, DeckOp.LoopOn, 4800, 48000);
            e.Send(d, DeckOp.HotPlay, 6000);
        }
        for (int i = 0; i < 100; i++)
        {
            if (i == 50) for (int d = 0; d < 2; d++) { e.Send(d, DeckOp.JumpBy, 100); e.Decks[d].KeyLock = d == 0; }
            e.Render(m, c);
            drift.Read(c);
        }
    }

    nint Open(OutputDevice d, delegate* unmanaged[Cdecl]<nint, float*, CULong, nint, CULong, nint, int> cb)
    {
        var p = new StreamParams { Device = d.Index, Channels = 2, Format = new CULong(1), Latency = 0.04 };
        Check(Pa_OpenStream(out var s, 0, &p, TrackBuffer.SampleRate, new CULong(512), new CULong(0), cb, GCHandle.ToIntPtr(_self)));
        return s;
    }

    static void Check(int e)
    {
        if (e < 0) throw new InvalidOperationException("Audio output: " + Marshal.PtrToStringUTF8(Pa_GetErrorText(e)));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int MasterCallback(nint input, float* o, CULong n, nint time, CULong flags, nint user)
    {
        var self = Unsafe.As<Output>(GCHandle.FromIntPtr(user).Target!);
        if ((flags.Value & 4) != 0) self._masterUnderruns++;
        var dst = new Span<float>(o, (int)n.Value * 2);
        for (int i = 0; i < dst.Length; i += Engine.MaxBlock * 2)
        {
            int len = Math.Min(Engine.MaxBlock * 2, dst.Length - i);
            var m = self._master.AsSpan(0, len);
            var c = self._cue.AsSpan(0, len);
            self._engine.Render(m, c);
            if (self._c == 0) c.CopyTo(dst[i..]);
            else { m.CopyTo(dst[i..]); self._ring.Write(c); }
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int CueCallback(nint input, float* o, CULong n, nint time, CULong flags, nint user)
    {
        var self = Unsafe.As<Output>(GCHandle.FromIntPtr(user).Target!);
        if ((flags.Value & 4) != 0) self._cueUnderruns++;
        self._drift.Read(new Span<float>(o, (int)n.Value * 2));
        return 0;
    }

    public void Dispose()
    {
        foreach (var s in new[] { _m, _c })
            if (s != 0) { Pa_AbortStream(s); Pa_CloseStream(s); }
        _m = _c = 0;
        if (_self.IsAllocated) _self.Free();
    }
}
