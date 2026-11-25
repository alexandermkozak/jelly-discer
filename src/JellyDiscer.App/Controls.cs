using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using JellyDiscer.Core.Analysis;
using JellyDiscer.Core.Audio;

namespace JellyDiscer.App;

static class Ui
{
    static IBrush B(string s) => new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(s));
    public static readonly IBrush Bg = B("#15171c"), Panel = B("#1d2027"), Well = B("#0f1115"), Line = B("#343944"), Text = B("#e4e6eb"), Dim = B("#8d94a3"),
        A = B("#4cc2ff"), Bb = B("#ffa24c"), Low = B("#3b6fd1"), Mid = B("#5fb58a"), High = B("#e9edf5"), Red = B("#ff5a5a"), Green = B("#52d38a"), Yellow = B("#f2cc4d"), Grid = B("#66ffffff"), Bar = B("#ccff7070"), LoopFill = B("#3352d38a");
    public static IBrush Deck(int i) => i == 0 ? A : Bb;
    public static FormattedText Fmt(string s, double size, IBrush b) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, b);
    public static TextBlock Label(string s, double size = 12, IBrush? b = null) => new() { Text = s, FontSize = size, Foreground = b ?? Text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    public static Button Btn(string s, Action a, string? tip = null)
    {
        var b = new Button { Content = s, Focusable = false, Padding = new(8, 4), FontSize = 12 };
        b.Click += (_, _) => a();
        if (tip != null) ToolTip.SetTip(b, tip);
        return b;
    }
    public static string Time(double s) => $"{(int)s / 60}:{(int)s % 60:00}";
}

/// <summary>Drag vertically or scroll; double-click resets.</summary>
sealed class Knob : Control
{
    readonly string _name;
    readonly double _min, _max, _def;
    readonly Func<double> _get;
    readonly Action<double> _set;
    readonly Func<double, string> _fmt;
    Point? _last;
    public Knob(string name, double min, double max, double def, Func<double> get, Action<double> set, Func<double, string> fmt)
    {
        (_name, _min, _max, _def, _get, _set, _fmt) = (name, min, max, def, get, set, fmt);
        Width = 58; Height = 62;
    }
    void Put(double v) { _set(Math.Clamp(v, _min, _max)); InvalidateVisual(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2) { Put(_def); return; }
        _last = e.GetPosition(this);
        e.Pointer.Capture(this);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_last is not { } l) return;
        var p = e.GetPosition(this);
        Put(_get() + (l.Y - p.Y) / 150 * (_max - _min) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1));
        _last = p;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _last = null; e.Pointer.Capture(null); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { Put(_get() + e.Delta.Y * (_max - _min) / 60); e.Handled = true; }
    public override void Render(DrawingContext c)
    {
        c.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        double v = _get(), f = (v - _min) / (_max - _min), ang = (-135 + 270 * f) * Math.PI / 180;
        var o = new Point(Bounds.Width / 2, 24);
        c.DrawEllipse(Ui.Well, new Pen(Ui.Line, 2), o, 17, 17);
        c.DrawLine(new Pen(Math.Abs(v - _def) < 1e-3 ? Ui.Dim : Ui.Yellow, 3), o, new Point(o.X + 16 * Math.Sin(ang), o.Y - 16 * Math.Cos(ang)));
        var t = Ui.Fmt(_name + " " + _fmt(v), 10, Ui.Dim);
        c.DrawText(t, new Point((Bounds.Width - t.Width) / 2, 46));
    }
}

/// <summary>Straight fader. Vertical has its maximum at the top.</summary>
sealed class Fader : Control
{
    readonly bool _vert;
    readonly double _min, _max, _def;
    readonly Func<double> _get;
    readonly Action<double> _set;
    bool _drag;
    public Fader(bool vertical, double min, double max, double def, Func<double> get, Action<double> set) => (_vert, _min, _max, _def, _get, _set) = (vertical, min, max, def, get, set);
    double Scale => _max - _min;
    void Put(double v) { _set(Math.Clamp(v, _min, _max)); InvalidateVisual(); }
    void At(Point p) => Put(_min + Scale * (_vert ? 1 - (p.Y - 8) / (Bounds.Height - 16) : (p.X - 8) / (Bounds.Width - 16)));
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2) { Put(_def); return; }
        _drag = true; e.Pointer.Capture(this); At(e.GetPosition(this));
    }
    protected override void OnPointerMoved(PointerEventArgs e) { if (_drag) At(e.GetPosition(this)); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _drag = false; e.Pointer.Capture(null); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Put(_get() + e.Delta.Y * Scale / (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1600 : 160));
        e.Handled = true;
    }
    public override void Render(DrawingContext c)
    {
        double w = Bounds.Width, h = Bounds.Height, f = (_get() - _min) / Scale, d = (_def - _min) / Scale;
        c.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        if (_vert)
        {
            c.FillRectangle(Ui.Well, new Rect(w / 2 - 3, 4, 6, h - 8), 3);
            double y0 = 8 + (1 - d) * (h - 16), y = 8 + (1 - f) * (h - 16);
            c.FillRectangle(Ui.Dim, new Rect(2, y0, w - 4, 1));
            c.FillRectangle(Ui.Text, new Rect(w / 2 - 14, y - 6, 28, 12), 2);
            c.FillRectangle(Ui.Bg, new Rect(w / 2 - 14, y - 0.5, 28, 1));
        }
        else
        {
            c.FillRectangle(Ui.Well, new Rect(4, h / 2 - 3, w - 8, 6), 3);
            double x = 8 + f * (w - 16);
            c.FillRectangle(Ui.Dim, new Rect(8 + d * (w - 16), 2, 1, h - 4));
            c.FillRectangle(Ui.Text, new Rect(x - 6, h / 2 - 13, 12, 26), 2);
            c.FillRectangle(Ui.Bg, new Rect(x - 0.5, h / 2 - 13, 1, 26));
        }
    }
}

sealed class MeterBar(Func<float> peak, Func<float> rms, Func<bool>? clip = null) : Control
{
    public override void Render(DrawingContext c)
    {
        double w = Bounds.Width, h = Bounds.Height;
        c.FillRectangle(Ui.Well, new Rect(0, 0, w, h));
        double F(float v) => Math.Clamp((Dsp.ToDb(v) + 48) / 48, 0, 1) * h;
        double p = F(peak()), r = F(rms());
        c.FillRectangle(peak() > 0.95 ? Ui.Red : peak() > 0.7 ? Ui.Yellow : Ui.Green, new Rect(0, h - p, w, p));
        c.FillRectangle(new SolidColorBrush(Colors.Black, 0.35), new Rect(0, h - p, w, Math.Max(0, p - r)));
        if (clip?.Invoke() == true) c.FillRectangle(Ui.Red, new Rect(0, 0, w, 8));
    }
}

/// <summary>Scrolling waveform. The time axis is scaled by playback rate, so matched decks show equal beat spacing.</summary>
sealed class WaveView(DeckController d, Assists assists) : Control
{
    public static double SecondsShown = 8;
    Point? _drag;
    double _dragPos;
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { SecondsShown = Math.Clamp(SecondsShown * (e.Delta.Y > 0 ? 0.85 : 1.18), 2, 30); e.Handled = true; }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { if (!d.Deck.Playing) { _drag = e.GetPosition(this); _dragPos = d.Seconds; e.Pointer.Capture(this); } }
    protected override void OnPointerMoved(PointerEventArgs e) { if (_drag is { } p && !d.Deck.Playing) d.Seek(_dragPos - (e.GetPosition(this).X - p.X) / Bounds.Width * SecondsShown); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _drag = null; e.Pointer.Capture(null); }
    public override void Render(DrawingContext c)
    {
        double w = Bounds.Width, h = Bounds.Height;
        using var _ = c.PushClip(new Rect(0, 0, w, h));
        c.FillRectangle(Ui.Well, new Rect(0, 0, w, h));
        if (d.Data.Waveform is { Count: > 0 } wf && d.Buffer != null)
        {
            double pos = d.Seconds, span = SecondsShown * (1 + d.Deck.Tempo), t0 = pos - span / 2, pps = w / span;
            if (d.Deck.Looping)
                c.FillRectangle(Ui.LoopFill, new Rect((d.Deck.LoopStart / 48000 - t0) * pps, 0, (d.Deck.LoopEnd - d.Deck.LoopStart) / 48000 * pps, h));
            var b = wf.Bins;
            // step in whole groups of bins so the picture does not shimmer while scrolling
            int step = Math.Max(1, (int)Math.Round(2 / pps * 150));
            int first = (int)Math.Floor(t0 * 150 / step) * step, last = (int)((t0 + span) * 150) + step;
            double bw = step / 150.0 * pps;
            for (int k = Math.Max(0, first); k < Math.Min(wf.Count - step, last); k += step)
            {
                int lo = 0, mi = 0, hi = 0;
                for (int j = k; j < k + step; j++) { lo = Math.Max(lo, b[3 * j]); mi = Math.Max(mi, b[3 * j + 1]); hi = Math.Max(hi, b[3 * j + 2]); }
                double x = (k / 150.0 - t0) * pps;
                void Col(int v, IBrush br) { double y = v / 255.0 * h * 0.48; c.FillRectangle(br, new Rect(x, h / 2 - y, bw + 0.3, 2 * y)); }
                Col(lo, Ui.Low); Col(mi * 3 / 4, Ui.Mid); Col(hi / 2, Ui.High);
            }
            if (assists.Grid && d.Data.Grid is { } g)
            {
                int bi = (int)Math.Ceiling(g.BeatAt(t0));
                for (double x; (x = (g.TimeOf(bi) - t0) * pps) < w; bi++)
                    c.FillRectangle(bi % 4 == 0 ? Ui.Bar : Ui.Grid, new Rect(x, 0, bi % 4 == 0 ? 2 : 1, h));
            }
            void Mark(double frames, IBrush br, string s)
            {
                double x = (frames / 48000 - t0) * pps;
                if (x < -20 || x > w) return;
                c.FillRectangle(br, new Rect(x, 0, 2, h));
                c.DrawText(Ui.Fmt(s, 11, br), new Point(x + 4, 0));
            }
            Mark(d.Deck.CuePoint, Ui.Yellow, "CUE");
            for (int i = 0; i < 4; i++) if (d.Data.HotCues[i] >= 0) Mark(d.Data.HotCues[i], Ui.Green, (i + 1).ToString());
        }
        c.FillRectangle(Ui.Deck(d.Index), new Rect(w / 2 - 1, 0, 2, h));
        c.DrawText(Ui.Fmt(d.Letter, 16, Ui.Deck(d.Index)), new Point(6, 2));
    }
}

sealed class Overview(DeckController d) : Control
{
    protected override void OnPointerPressed(PointerPressedEventArgs e) { if (d.Buffer is { } b) d.Seek(e.GetPosition(this).X / Bounds.Width * b.Seconds); }
    public override void Render(DrawingContext c)
    {
        double w = Bounds.Width, h = Bounds.Height;
        c.FillRectangle(Ui.Well, new Rect(0, 0, w, h));
        if (d.Data.Waveform is not { Count: > 0 } wf || d.Buffer is not { } buf) return;
        int cols = (int)(w / 2);
        for (int i = 0; i < cols; i++)
        {
            int a = (int)((long)i * wf.Count / cols), z = Math.Min(wf.Count, (int)((long)(i + 1) * wf.Count / cols)), m = 0, l = 0;
            for (int k = a; k < z; k++) { m = Math.Max(m, wf.Bins[3 * k + 1]); l = Math.Max(l, wf.Bins[3 * k]); }
            double yl = l / 255.0 * h, ym = m / 255.0 * h * 0.7;
            c.FillRectangle(Ui.Low, new Rect(i * 2, h - yl, 2, yl));
            c.FillRectangle(Ui.Mid, new Rect(i * 2, h - ym, 2, ym));
        }
        double X(double sec) => sec / buf.Seconds * w;
        c.FillRectangle(new SolidColorBrush(Colors.Black, 0.5), new Rect(0, 0, X(d.Seconds), h));
        c.FillRectangle(Ui.Yellow, new Rect(X(d.Deck.CuePoint / 48000), 0, 2, h));
        foreach (var q in d.Data.HotCues) if (q >= 0) c.FillRectangle(Ui.Green, new Rect(X(q / 48000), 0, 2, h));
        c.FillRectangle(Ui.Red, new Rect(X(d.Seconds) - 1, 0, 2, h));
    }
}

/// <summary>Marker right of centre: deck B is ahead.</summary>
sealed class PhaseMeter(DeckController a, DeckController b) : Control
{
    public override void Render(DrawingContext c)
    {
        double w = Bounds.Width, h = Bounds.Height;
        c.FillRectangle(Ui.Well, new Rect(0, 0, w, h));
        c.FillRectangle(Ui.Dim, new Rect(w / 2 - 1, 0, 2, h));
        if (a.Data.Grid is not { } ga || b.Data.Grid is not { } gb) return;
        double p = BeatGrid.PhaseDiff(gb, b.Seconds, ga, a.Seconds);
        c.FillRectangle(Math.Abs(p) < 0.04 ? Ui.Green : Math.Abs(p) < 0.12 ? Ui.Yellow : Ui.Red, new Rect(w / 2 + p * (w - 12) - 6, 1, 12, h - 2), 2);
    }
}

public sealed class Assists
{
    public bool Bpm = true, Grid = true, Phase = true, Sync = true;
    public void Preset(int i) { Bpm = i < 2; Grid = i < 2; Phase = i == 0; Sync = i == 0; }
}
