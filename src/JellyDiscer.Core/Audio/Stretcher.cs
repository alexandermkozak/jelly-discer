using System.Runtime.InteropServices;

namespace JellyDiscer.Core.Audio;

/// <summary>Rubber Band used as a real-time pitch shifter (time ratio fixed at 1).</summary>
public sealed unsafe class Stretcher
{
    const string Lib = "librubberband.so.3";
    [DllImport(Lib)] static extern nint rubberband_new(uint rate, uint channels, int options, double timeRatio, double pitch);
    [DllImport(Lib)] static extern void rubberband_delete(nint s);
    [DllImport(Lib)] static extern void rubberband_reset(nint s);
    [DllImport(Lib)] static extern void rubberband_set_pitch_scale(nint s, double p);
    [DllImport(Lib)] static extern uint rubberband_get_preferred_start_pad(nint s);
    [DllImport(Lib)] static extern uint rubberband_get_start_delay(nint s);
    [DllImport(Lib)] static extern void rubberband_set_max_process_size(nint s, uint n);
    [DllImport(Lib)] static extern void rubberband_process(nint s, float** input, uint n, int final);
    [DllImport(Lib)] static extern int rubberband_available(nint s);
    [DllImport(Lib)] static extern uint rubberband_retrieve(nint s, float** output, uint n);

    public const int Block = 256;
    readonly nint _s;
    readonly float* _mem;
    float* InL => _mem;
    float* InR => _mem + Block;
    float* OutL => _mem + 2 * Block;
    float* OutR => _mem + 3 * Block;
    double _pitch = 1;
    int _discard;

    /// <summary>Source frames consumed but not heard yet.</summary>
    public int Ahead { get; private set; }

    Stretcher()
    {
        _s = rubberband_new(48000, 2, 0x1 | 0x04000000, 1, 1);
        rubberband_set_max_process_size(_s, Block);
        _mem = (float*)NativeMemory.AllocZeroed(4 * Block * sizeof(float));
    }
    ~Stretcher() { rubberband_delete(_s); NativeMemory.Free(_mem); }

    public static Stretcher? TryCreate()
    {
        try { return new Stretcher(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public void Reset(double pitch)
    {
        rubberband_reset(_s);
        rubberband_set_pitch_scale(_s, _pitch = pitch);
        Ahead = 0;
        int pad = (int)rubberband_get_preferred_start_pad(_s);
        _discard = (int)rubberband_get_start_delay(_s);
        new Span<float>(_mem, 2 * Block).Clear();
        float** p = stackalloc float*[2] { InL, InR };
        for (; pad > 0; pad -= Block) rubberband_process(_s, p, (uint)Math.Min(pad, Block), 0);
    }

    public void SetPitch(double p)
    {
        if (Math.Abs(p - _pitch) > 1e-5) rubberband_set_pitch_scale(_s, _pitch = p);
    }

    /// <summary>Fills o (interleaved, at most Block frames), pulling source audio from the deck as needed.</summary>
    public void Process(Deck source, Span<float> o)
    {
        int n = o.Length / 2;
        float** ip = stackalloc float*[2] { InL, InR };
        float** op = stackalloc float*[2] { OutL, OutR };
        Span<float> tmp = stackalloc float[2 * Block];
        for (int guard = 0; guard < 100; guard++)
        {
            int av = rubberband_available(_s);
            if (_discard > 0 && av > 0)
            {
                _discard -= (int)rubberband_retrieve(_s, op, (uint)Math.Min(Math.Min(av, _discard), Block));
                continue;
            }
            if (av >= n) break;
            source.ReadSource(tmp);
            for (int i = 0; i < Block; i++) { InL[i] = tmp[2 * i]; InR[i] = tmp[2 * i + 1]; }
            rubberband_process(_s, ip, Block, 0);
            Ahead += Block;
        }
        int got = (int)rubberband_retrieve(_s, op, (uint)n);
        Ahead = Math.Max(0, Ahead - got);
        for (int i = 0; i < n; i++)
        {
            o[2 * i] = i < got ? OutL[i] : 0;
            o[2 * i + 1] = i < got ? OutR[i] : 0;
        }
    }
}
