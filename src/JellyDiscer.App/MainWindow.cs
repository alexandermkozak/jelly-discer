using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using JellyDiscer.Core.Analysis;
using JellyDiscer.Core.Audio;
using JellyDiscer.Jellyfin;
using Track = JellyDiscer.Jellyfin.Track;

namespace JellyDiscer.App;

public sealed class MainWindow : Window
{
    readonly Settings _s = Settings.Load();
    readonly Engine _engine = new();
    readonly Store _store = new(Path.Combine(Settings.DataDir, "library.db"));
    readonly TrackCache _cache = new(Path.Combine(Settings.CacheDir, "tracks"));
    readonly DeckController[] _d;
    readonly Assists _assists = new();
    readonly Drills _drills;
    readonly Dictionary<string, (Action down, Action? up)> _commands = [];
    Dictionary<Key, string> _keys;
    readonly HashSet<Key> _held = [];
    readonly List<Control> _live = [];
    readonly List<Action> _tick = [];
    Output? _out;
    string _masterName = "";
    Recorder? _rec;
    JellyfinClient? _client;
    List<Track> _tracks = [];
    Dictionary<string, BeatGrid> _grids = [];
    readonly ListBox _list = new() { Background = Ui.Well, SelectionMode = SelectionMode.Single };
    readonly TextBox _search = new() { PlaceholderText = "Search title, artist, album, genre, or bpm:120-130", MinWidth = 320 };
    readonly TextBlock _status = Ui.Label("", 12, Ui.Dim), _drillText = Ui.Label("", 13);
    readonly ComboBox _preset = new() { ItemsSource = new[] { "Guided", "Partial", "Ears only" }, SelectedIndex = 0, Focusable = false };
    CancellationTokenSource? _analyze;
    readonly Border _help = new() { IsVisible = false };
    readonly List<Action> _hints = [];

    public MainWindow()
    {
        Title = "jelly-discer"; Width = 1400; Height = 940; MinWidth = 1180; MinHeight = 760; Background = Ui.Bg;
        _d = [new(0, _engine, _store, _assists), new(1, _engine, _store, _assists)];
        _drills = new(_d[0], _d[1], _store, () => _tracks);
        _keys = _s.KeyMap();
        _engine.PhoneMode = _s.SplitMono ? PhoneMode.SplitMono : PhoneMode.Blend;
        _engine.CueOffsetMs = _s.CueOffsetMs;
        _engine.CueBlend = _s.CueBlend;
        BuildCommands();
        Content = BuildLayout();
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Deactivated += (_, _) => ReleaseAll();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            foreach (var c in _live) c.InvalidateVisual();
            foreach (var t in _tick) t();
        });
        timer.Start();
        Opened += async (_, _) =>
        {
            OpenAudio();
            await ConnectAsync();
            if (Environment.GetEnvironmentVariable("JELLY_SELFTEST") is { } png) await SelfTest(png);
        };
        Closing += (_, _) => { timer.Stop(); _analyze?.Cancel(); _rec?.Dispose(); _out?.Dispose(); _s.CueBlend = _engine.CueBlend; _s.Save(); _store.Dispose(); };
    }

    // ---- commands -------------------------------------------------------
    void BuildCommands()
    {
        foreach (var d in _d)
        {
            string p = d.Letter.ToLower() + ".";
            var other = _d[1 - d.Index];
            _commands[p + "play"] = (d.PlayPause, null);
            _commands[p + "cue"] = (() => d.Cue(true), () => d.Cue(false));
            _commands[p + "nudge-"] = (() => d.Deck.Nudge = -0.02f, () => d.Deck.Nudge = 0);
            _commands[p + "nudge+"] = (() => d.Deck.Nudge = 0.02f, () => d.Deck.Nudge = 0);
            _commands[p + "tempo-"] = (() => d.StepTempo(-1), null);
            _commands[p + "tempo+"] = (() => d.StepTempo(1), null);
            _commands[p + "pfl"] = (() => d.Channel.Pfl = !d.Channel.Pfl, null);
            for (int i = 0; i < 4; i++) { int n = i; _commands[p + "hot" + (i + 1)] = (() => d.HotCue(n), null); }
            _commands[p + "loop"] = (d.ToggleLoop, null);
            _commands[p + "sync"] = (() => { if (_assists.Sync) Say(d.Sync(other) ?? ""); }, null);
            _commands[p + "tap"] = (d.Tap, null);
        }
        _commands["xfade-"] = (() => _engine.Crossfader = Math.Max(0, _engine.Crossfader - 0.05f), null);
        _commands["xfade+"] = (() => _engine.Crossfader = Math.Min(1, _engine.Crossfader + 0.05f), null);
        _commands["drill.done"] = (FinishDrill, null);
    }
    static bool Repeats(string a) => a.Contains("tempo") || a.StartsWith("xfade");
    bool Typing => FocusManager?.GetFocusedElement() is TextBox;
    void OnKey(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.F1) { ToggleHelp(); e.Handled = true; return; }
        if (e.Key == Key.Escape && _help.IsVisible) { _help.IsVisible = false; e.Handled = true; return; }
        if (Typing) { if (e.Key == Key.Escape) _list.Focus(); return; }
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift) || !_keys.TryGetValue(e.Key, out var a)) return;
        if (a == "drill.done" && _drills.Active == null) return;
        if (_held.Add(e.Key) || Repeats(a)) _commands[a].down();
        e.Handled = true;
    }
    void OnKeyUp(object? s, KeyEventArgs e)
    {
        if (_held.Remove(e.Key) && _keys.TryGetValue(e.Key, out var a)) _commands[a].up?.Invoke();
    }
    void ReleaseAll()
    {
        foreach (var k in _held) if (_keys.TryGetValue(k, out var a)) _commands[a].up?.Invoke();
        _held.Clear();
    }
    void Say(string s) => _status.Text = s;

    // ---- key help --------------------------------------------------------
    string KeyFor(string action)
    {
        foreach (var (k, a) in _keys) if (a == action) return Settings.KeyName(k);
        return "";
    }
    /// <summary>Adds the action's current key in small print under the button's label.</summary>
    Button Hint(Button b, string action)
    {
        var label = new TextBlock { Text = b.Content as string, HorizontalAlignment = HorizontalAlignment.Center };
        var key = new TextBlock { FontSize = 9, Opacity = 0.9, HorizontalAlignment = HorizontalAlignment.Center };
        b.Content = new StackPanel { Children = { label, key } };
        _hints.Add(() => { key.Text = KeyFor(action); key.IsVisible = _s.KeyHints && key.Text.Length > 0; });
        return b;
    }
    TextBlock HintLabel(params string[] actions)
    {
        var t = Ui.Label("", 10, Ui.Dim);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        _hints.Add(() => { t.Text = string.Join(" / ", actions.Select(KeyFor)); t.IsVisible = _s.KeyHints; });
        return t;
    }
    void RefreshHints()
    {
        foreach (var h in _hints) h();
        var cols = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 36 };
        foreach (var group in Settings.DefaultKeys.Keys.GroupBy(Settings.Group))
        {
            var col = Col(Ui.Label(group.Key, 15, group.Key == "Deck A" ? Ui.A : group.Key == "Deck B" ? Ui.Bb : Ui.Text));
            foreach (var a in group)
            {
                var key = new Border { Background = Ui.Well, BorderBrush = Ui.Line, BorderThickness = new(1), CornerRadius = new(4), MinWidth = 52, Padding = new(6, 2),
                    Child = new TextBlock { Text = KeyFor(a), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center } };
                col.Children.Add(Row(key, Ui.Label(Settings.ActionName(a), 13)));
            }
            cols.Children.Add(col);
        }
        _help.Background = Ui.Panel; _help.BorderBrush = Ui.Dim; _help.BorderThickness = new(1); _help.CornerRadius = new(8); _help.Padding = new(24, 18);
        _help.HorizontalAlignment = HorizontalAlignment.Center; _help.VerticalAlignment = VerticalAlignment.Center;
        var content = Col(Ui.Label("Keyboard shortcuts", 18), cols,
            Ui.Label("Keys keep working while this is open. F1 or Esc closes it; change keys under Keys.", 12, Ui.Dim));
        content.Spacing = 14;
        _help.Child = content;
    }
    void ToggleHelp() => _help.IsVisible = !_help.IsVisible;

    // ---- layout ---------------------------------------------------------
    static StackPanel Row(params Control[] c) { var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; p.Children.AddRange(c); return p; }
    static StackPanel Col(params Control[] c) { var p = new StackPanel { Spacing = 6 }; p.Children.AddRange(c); return p; }
    static Border Panel(Control c) => new() { Background = Ui.Panel, CornerRadius = new(6), Padding = new(10), Margin = new(4), Child = c };
    static void Center(Button b) { b.HorizontalContentAlignment = HorizontalAlignment.Center; b.VerticalContentAlignment = VerticalAlignment.Center; }
    T Live<T>(T c) where T : Control { _live.Add(c); return c; }
    Button Hold(string label, Action down, Action up)
    {
        var b = new Button { Content = label, Focusable = false, Padding = new(8, 4), FontSize = 12 };
        b.AddHandler(PointerPressedEvent, (_, _) => down(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        b.AddHandler(PointerReleasedEvent, (_, _) => up(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        b.PointerCaptureLost += (_, _) => up();
        return b;
    }
    Button Toggle(string label, Func<bool> on, Action click, string? tip = null)
    {
        var b = Ui.Btn(label, click, tip);
        bool? last = null;
        _tick.Add(() => { bool v = on(); if (v == last) return; last = v; b.Background = v ? Ui.Green : Ui.Line; b.Foreground = v ? Brushes.Black : Ui.Text; });
        return b;
    }
    void Bind(TextBlock t, Func<string> f) => _tick.Add(() => { var v = f(); if (t.Text != v) t.Text = v; });

    Control BuildLayout()
    {
        var waves = new Grid { RowDefinitions = new("110,14,110"), Margin = new(8, 8, 8, 4) };
        var wa = Live(new WaveView(_d[0], _assists));
        var ph = Live(new PhaseMeter(_d[0], _d[1]));
        var wb = Live(new WaveView(_d[1], _assists));
        Grid.SetRow(ph, 1); Grid.SetRow(wb, 2);
        waves.Children.AddRange([wa, ph, wb]);
        _tick.Add(() => ph.Opacity = _assists.Phase ? 1 : 0);

        var mid = new Grid { ColumnDefinitions = new("*,Auto,*"), Margin = new(4, 0) };
        var l = DeckPanel(_d[0]); var m = MixerPanel(); var r = DeckPanel(_d[1]);
        Grid.SetColumn(m, 1); Grid.SetColumn(r, 2);
        mid.Children.AddRange([l, m, r]);

        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto") };
        var bar = TopBar(); var lib = Library();
        Grid.SetRow(waves, 1); Grid.SetRow(mid, 2); Grid.SetRow(lib, 3);
        var foot = new Border { Padding = new(10, 4), Child = _status };
        Grid.SetRow(foot, 4);
        Grid.SetRowSpan(_help, 5);
        root.Children.AddRange([bar, waves, mid, lib, foot, _help]);
        RefreshHints();
        return root;
    }

    Control TopBar()
    {
        var audio = Ui.Label("", 11, Ui.Dim);
        Bind(audio, () => _out == null ? "No audio output" : $"Dropouts {_out.MasterUnderruns}/{_out.CueUnderruns}" + (_out.HasCue ? $"   cue buffer {_out.CueFill:F0}/{_out.CueTarget}   drift {_out.DriftPpm:F0} ppm" : "   single output"));
        CheckBox Box(string n, Func<bool> g, Action<bool> set)
        {
            var c = new CheckBox { Content = n, Focusable = false, FontSize = 12, MinWidth = 0 };
            c.IsCheckedChanged += (_, _) => set(c.IsChecked == true);
            _tick.Add(() => { if (c.IsChecked != g()) c.IsChecked = g(); });
            return c;
        }
        _preset.SelectionChanged += (_, _) => { if (_preset.SelectedIndex >= 0) _assists.Preset(_preset.SelectedIndex); };
        var rec = Toggle("Record", () => _rec != null, ToggleRecord, "Record the master to ~/Music/jelly-discer");
        var row = Row(Ui.Label("Assists", 12, Ui.Dim), _preset,
            Box("BPM", () => _assists.Bpm, v => _assists.Bpm = v), Box("Grid", () => _assists.Grid, v => _assists.Grid = v),
            Box("Phase", () => _assists.Phase, v => _assists.Phase = v), Box("Sync", () => _assists.Sync, v => _assists.Sync = v), Box("Quantize", () => _assists.Quantize, v => _assists.Quantize = v),
            Ui.Btn("Beatmatch drill", () => _ = StartDrill("Beatmatch")), Ui.Btn("Level drill", () => _ = StartDrill("Level")), Ui.Btn("History", ShowHistory),
            rec, Ui.Btn("Settings", ShowSettings), Ui.Btn("Keys", ShowKeys, "Change keys"), Ui.Btn("Help F1", ToggleHelp, "Show keyboard shortcuts"), Box("Key hints", () => _s.KeyHints, v => { _s.KeyHints = v; RefreshHints(); }), audio);
        var done = Hint(Ui.Btn("Done", FinishDrill), "drill.done"); var cancel = Ui.Btn("Cancel", () => { _drills.Cancel(); _drillText.Text = ""; });
        var drill = Row(_drillText, done, cancel);
        _tick.Add(() => { drill.IsVisible = !string.IsNullOrEmpty(_drillText.Text); done.IsVisible = _drills.Active != null; cancel.Content = _drills.Active != null ? "Cancel" : "Close"; });
        return new Border { Background = Ui.Panel, Padding = new(10, 6), Child = Col(row, drill) };
    }

    Control DeckPanel(DeckController d)
    {
        var art = new Image { Width = 56, Height = 56, Stretch = Stretch.UniformToFill };
        byte[]? shown = null;
        var title = Ui.Label("", 16); title.TextTrimming = TextTrimming.CharacterEllipsis;
        var sub = Ui.Label("", 12, Ui.Dim); sub.TextTrimming = TextTrimming.CharacterEllipsis;
        d.Changed += () =>
        {
            if (shown == d.Art) return;
            shown = d.Art;
            try { art.Source = shown == null ? null : new Bitmap(new MemoryStream(shown)); } catch (Exception) { art.Source = null; }
        };
        Bind(title, () => d.Track?.Title ?? "Deck " + d.Letter);
        Bind(sub, () => d.Status.Length > 0 ? d.Status : d.Track?.Artist ?? "");
        var bpm = Ui.Label("", 24, Ui.Deck(d.Index)); var time = Ui.Label("", 14); var conf = Ui.Label("", 11, Ui.Yellow);
        Bind(bpm, () => !_assists.Bpm ? "" : d.Data.Grid is null ? "-- BPM" : $"{d.EffectiveBpm:F2}");
        Bind(conf, () => !_assists.Bpm || d.Track == null ? "" : d.Data.Grid is not { } g ? "No beat found: tap it in" : g.Confident ? "" : $"Unsure of this grid ({g.Confidence:P0})");
        Bind(time, () => d.Buffer is { } b ? $"{Ui.Time(d.Seconds)} / -{Ui.Time(Math.Max(0, b.Seconds - d.Seconds))}" : "");
        var names = Col(title, sub); names.Spacing = 2;
        var head = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10 };
        var nums = Col(bpm, time); nums.Spacing = 0; nums.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(names, 1); Grid.SetColumn(nums, 2);
        head.Children.AddRange([art, names, nums]);
        var over = Live(new Overview(d) { Height = 36 });

        string p = d.Letter.ToLower() + ".";
        Button Cmd(string label, string a) => Hint(Ui.Btn(label, () => _commands[p + a].down()), p + a);
        Button HoldCmd(string label, string a) => Hint(Hold(label, () => _commands[p + a].down(), () => _commands[p + a].up!()), p + a);
        var play = Hint(Toggle("Play", () => d.Deck.Playing, d.PlayPause), p + "play"); play.MinWidth = 70; play.MinHeight = 40; Center(play);
        var cue = HoldCmd("CUE", "cue"); cue.MinWidth = 60; cue.MinHeight = 40; Center(cue); cue.Foreground = Ui.Yellow;
        var sync = Cmd("Sync", "sync"); _tick.Add(() => sync.IsVisible = _assists.Sync);
        var transport = Row(cue, play, HoldCmd("- Nudge", "nudge-"), HoldCmd("Nudge +", "nudge+"), sync,
            Toggle("Key lock", () => d.Deck.KeyLock, () => d.Deck.KeyLock = !d.Deck.KeyLock, d.Deck.KeyLockAvailable ? "Keep pitch while changing tempo" : "Rubber Band library not found"));
        var hot = Row(Ui.Label("Hot cues", 11, Ui.Dim));
        for (int i = 0; i < 4; i++)
        {
            int n = i;
            var b = Hint(Toggle((i + 1).ToString(), () => d.Data.HotCues[n] >= 0, () => d.HotCue(n), "Set or jump; right-click clears"), p + "hot" + (i + 1));
            b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Right) d.ClearHotCue(n); };
            hot.Children.Add(b);
        }
        var beats = Ui.Label("", 12); Bind(beats, () => d.LoopBeats + " beats");
        hot.Children.AddRange([Ui.Label("  Loop", 11, Ui.Dim), Ui.Btn("-", () => d.LoopBeats = Math.Max(1, d.LoopBeats / 2)), beats, Ui.Btn("+", () => d.LoopBeats = Math.Min(32, d.LoopBeats * 2)), Hint(Toggle("On", () => d.Deck.Looping, d.ToggleLoop), p + "loop")]);
        var grid = Row(Ui.Label("Grid", 11, Ui.Dim), Hint(Ui.Btn("Tap", d.Tap, "Tap along at least five times"), p + "tap"), Ui.Btn("/2", () => d.ScaleBpm(0.5)), Ui.Btn("x2", () => d.ScaleBpm(2)),
            Ui.Btn("<", () => d.ShiftGrid(-5), "Grid 5 ms earlier"), Ui.Btn(">", () => d.ShiftGrid(5), "Grid 5 ms later"), Ui.Btn("Downbeat here", d.SetDownbeat), conf);
        _tick.Add(() => grid.IsVisible = _assists.Grid || _assists.Bpm);

        var tempo = Live(new Fader(true, -1, 1, 0, () => -d.Deck.Tempo / d.Range, v => d.Deck.Tempo = (float)(-v * d.Range)) { Width = 44, Height = 180 });
        ToolTip.SetTip(tempo, "Tempo: down is faster. Scroll for fine control, Shift+scroll for finer, double-click resets");
        var pct = Ui.Label("", 12); pct.HorizontalAlignment = HorizontalAlignment.Center;
        Bind(pct, () => _assists.Bpm ? $"{d.Deck.Tempo * 100:+0.00;-0.00;0.00}%" : "");
        var range = Ui.Btn("", () => { }); range.Click += (_, _) => { d.Range = d.Range < 0.1f ? 0.16f : d.Range < 0.2f ? 0.5f : 0.08f; d.Deck.Tempo = Math.Clamp(d.Deck.Tempo, -d.Range, d.Range); };
        _tick.Add(() => { var t = $"±{d.Range * 100:F0}%"; if ((string?)range.Content != t) range.Content = t; });
        var tcol = Col(range, tempo, pct, HintLabel(p + "tempo-", p + "tempo+")); tcol.Width = 64;
        tcol.Children[0].HorizontalAlignment = HorizontalAlignment.Center;
        var g2 = new Grid { ColumnDefinitions = new("*,Auto") };
        var left = Col(head, over, transport, hot, grid); left.Spacing = 10;
        Grid.SetColumn(tcol, 1);
        g2.Children.AddRange([left, tcol]);
        return Panel(g2);
    }

    Control MixerPanel()
    {
        static string Db(double v) => $"{v:+0.0;-0.0;0.0}";
        Control Strip(DeckController d)
        {
            var c = d.Channel;
            static string Eq(double v) => v < 0.01 ? "kill" : Db(Dsp.ToDb(v));
            var knobs = Col(
                Live(new Knob("Trim", -24, 12, 0, () => c.TrimDb, v => c.TrimDb = (float)v, v => _drills.Active == "Level" ? "" : Db(v))),
                Live(new Knob("High", 0, 2, 1, () => c.High, v => c.High = (float)v, Eq)),
                Live(new Knob("Mid", 0, 2, 1, () => c.Mid, v => c.Mid = (float)v, Eq)),
                Live(new Knob("Low", 0, 2, 1, () => c.Low, v => c.Low = (float)v, Eq)));
            knobs.Spacing = 0;
            var filt = Live(new Knob("Filter", -1, 1, 0, () => c.Filter, v => c.Filter = (float)v, v => Math.Abs(v) < 0.02 ? "off" : v < 0 ? "LP" : "HP"));
            var fader = Live(new Fader(true, 0, 1, 1, () => c.Fader, v => c.Fader = (float)v) { Width = 40, Height = 150 });
            var meter = Live(new MeterBar(() => c.Meter.Peak, () => c.Meter.Rms) { Width = 10, Height = 150 });
            _tick.Add(() => meter.Opacity = _drills.Active == "Level" ? 0 : 1);
            var pfl = Hint(Toggle("Phones", () => c.Pfl, () => c.Pfl = !c.Pfl, "Send this deck to the headphones"), d.Letter.ToLower() + ".pfl");
            var second = Col(filt, d.Index == 0 ? Row(fader, meter) : Row(meter, fader), pfl);
            return d.Index == 0 ? Row(knobs, second) : Row(second, knobs);
        }
        var master = Col(Live(new Knob("Master", 0, 1.5, 0.8, () => _engine.MasterGain, v => _engine.MasterGain = (float)v, v => Db(Dsp.ToDb(v)))),
            Live(new MeterBar(() => _engine.MasterMeter.Peak, () => _engine.MasterMeter.Rms, () => _engine.Limiter.Reduction < 0.98f) { Width = 16, Height = 90, HorizontalAlignment = HorizontalAlignment.Center }),
            Live(new Knob("Phones", 0, 1.5, 0.8, () => _engine.CueGain, v => _engine.CueGain = (float)v, v => Db(Dsp.ToDb(v)))),
            Live(new Knob("Cue/Mix", 0, 1, 0, () => _engine.CueBlend, v => _engine.CueBlend = (float)v, v => $"{v * 100:F0}")));
        master.Spacing = 2;
        var xf = Live(new Fader(false, 0, 1, 0.5, () => _engine.Crossfader, v => _engine.Crossfader = (float)v) { Width = 260, Height = 34 });
        var xrow = Row(Col(Ui.Label("A", 14, Ui.A), HintLabel("xfade-")), xf, Col(Ui.Label("B", 14, Ui.Bb), HintLabel("xfade+")), Live(new Knob("Curve", 0, 1, 0, () => _engine.Curve, v => _engine.Curve = (float)v, v => v < 0.5 ? "smooth" : "cut")));
        xrow.HorizontalAlignment = HorizontalAlignment.Center;
        var strips = Row(Strip(_d[0]), master, Strip(_d[1]));
        strips.HorizontalAlignment = HorizontalAlignment.Center;
        var col = Col(strips, xrow);
        col.Spacing = 2;
        return Panel(col);
    }

    Control Library()
    {
        _list.ItemTemplate = new FuncDataTemplate<Track>((t, ns) =>
        {
            var g = new Grid { ColumnDefinitions = new("70,60,3*,2*,2*,2*,60"), ColumnSpacing = 8 };
            if (t == null) return g;
            void Add(int c, string s, IBrush? b = null) { var x = Ui.Label(s, 12, b); x.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(x, c); g.Children.Add(x); }
            var row = Row(Ui.Btn("A", () => _ = LoadTrack(0, t)), Ui.Btn("B", () => _ = LoadTrack(1, t)));
            foreach (var b in row.Children.OfType<Button>()) b.Padding = new(7, 1);
            g.Children.Add(row);
            Add(1, _grids.TryGetValue(t.Id, out var gr) && _assists.Bpm ? $"{gr.Bpm:F1}{(gr.Confident ? "" : "?")}" : "", Ui.Dim);
            Add(2, t.Title); Add(3, t.Artist); Add(4, t.Album, Ui.Dim); Add(5, t.Genre, Ui.Dim); Add(6, Ui.Time(t.Seconds), Ui.Dim);
            return g;
        });
        _list.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x)) { Setters = { new Avalonia.Styling.Setter(PaddingProperty, new Thickness(10, 2)) } });
        _search.TextChanged += (_, _) => Filter();
        var analyze = Ui.Btn("Analyze shown", AnalyzeShown, "Download and find the beats of every track in the list");
        _tick.Add(() => { var s = _analyze == null ? "Analyze shown" : "Stop analyzing"; if ((string?)analyze.Content != s) analyze.Content = s; });
        var sort = new ComboBox { ItemsSource = new[] { "Title", "Artist", "Album", "BPM" }, SelectedIndex = 0, Focusable = false };
        sort.SelectionChanged += (_, _) => { _sort = sort.SelectedIndex; Filter(); };
        var count = Ui.Label("", 12, Ui.Dim); Bind(count, () => $"{_list.ItemCount} of {_tracks.Count}");
        var bar = Row(_search, Ui.Label("Sort", 12, Ui.Dim), sort, analyze, Ui.Btn("Sign in", () => _ = SignIn()), count);
        var g = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 6 };
        Grid.SetRow(_list, 1);
        g.Children.AddRange([bar, _list]);
        return Panel(g);
    }
    int _sort;
    void Filter()
    {
        IEnumerable<Track> q = _tracks;
        foreach (var w in (_search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.StartsWith("bpm:", StringComparison.OrdinalIgnoreCase))
            {
                var p = w[4..].Split('-');
                if (double.TryParse(p[0], out var lo)) { double hi = p.Length > 1 && double.TryParse(p[1], out var h) ? h : lo + 1; q = q.Where(t => _grids.TryGetValue(t.Id, out var g) && g.Bpm >= lo && g.Bpm <= hi); }
            }
            else q = q.Where(t => (t.Title + "\n" + t.Artist + "\n" + t.Album + "\n" + t.Genre).Contains(w, StringComparison.OrdinalIgnoreCase));
        }
        q = _sort switch { 1 => q.OrderBy(t => t.Artist), 2 => q.OrderBy(t => t.Album), 3 => q.OrderBy(t => _grids.TryGetValue(t.Id, out var g) ? g.Bpm : 999), _ => q.OrderBy(t => t.Title) };
        _list.ItemsSource = q.ToList();
    }

    // ---- actions --------------------------------------------------------
    async Task<bool> LoadTrack(int deck, Track t)
    {
        if (_client == null) return false;
        bool ok = await _d[deck].LoadAsync(_client, _cache, t);
        _grids = _store.Grids();
        return ok;
    }

    async void AnalyzeShown()
    {
        if (_analyze != null) { _analyze.Cancel(); return; }
        if (_client is not { } client) return;
        var cts = _analyze = new CancellationTokenSource();
        var todo = (_list.ItemsSource as IEnumerable<Track> ?? []).Where(t => _store.Get(t.Id) == null && t.Seconds is > 20 and < 1200).ToList();
        int done = 0, failed = 0;
        try
        {
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cts.Token }, async (t, ct) =>
            {
                try
                {
                    var buf = await TrackBuffer.DecodeAsync(await _cache.GetAsync(client, t.Id, null, ct), ct);
                    var data = new TrackData { Grid = Analyzer.DetectGrid(buf), Waveform = Analyzer.BuildWaveform(buf) };
                    ct.ThrowIfCancellationRequested();
                    if (_store.Get(t.Id) == null) _store.Save(t.Id, data); // keep hand edits made meanwhile
                }
                catch (Exception e) when (e is not OperationCanceledException) { Interlocked.Increment(ref failed); }
                int n = Interlocked.Increment(ref done);
                Dispatcher.UIThread.Post(() => Say($"Analyzed {n} of {todo.Count}" + (failed > 0 ? $", {failed} failed" : "")));
            });
        }
        catch (OperationCanceledException) { Say($"Stopped after {done} of {todo.Count}"); }
        catch (ObjectDisposedException) { return; }
        _analyze = null;
        _grids = _store.Grids();
        Filter();
    }

    async Task StartDrill(string kind)
    {
        _drillText.Text = "Loading drill tracks";
        _preset.SelectedIndex = 2;
        _assists.Preset(2);
        var msg = await _drills.StartAsync(kind, LoadTrack);
        _drillText.Text = msg;
        if (_drills.Active != null) { _engine.Crossfader = kind == "Level" ? 0.5f : 0f; _d[0].Channel.Pfl = false; _d[1].Channel.Pfl = true; }
    }
    void FinishDrill() { if (_drills.Active != null) _drillText.Text = _drills.Finish(); }

    void ToggleRecord()
    {
        if (_rec != null)
        {
            var path = _rec.Path;
            _rec.Dispose();
            _rec = null;
            Say("Saved " + path);
            return;
        }
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "jelly-discer");
            _rec = new Recorder(_engine, Path.Combine(dir, $"mix-{DateTime.Now:yyyyMMdd-HHmmss}.wav"));
        }
        catch (IOException e) { Say("Could not record: " + e.Message); }
    }

    void OpenAudio()
    {
        _out?.Dispose();
        _out = null;
        try
        {
            var devs = Output.Devices();
            var m = devs.FirstOrDefault(d => d.Name == _s.MasterDevice) ?? devs.FirstOrDefault(d => d.Name.Contains("Speaker")) ?? devs.FirstOrDefault();
            if (m == null) { Say("No audio output devices found"); return; }
            _masterName = m.Name;
            _out = new Output(_engine, m, devs.FirstOrDefault(d => d.Name == _s.CueDevice));
            Say($"Master: {m.Name}" + (_out.HasCue ? $", headphones: {_s.CueDevice}" : ". Pick a headphone device in Settings to cue separately."));
        }
        catch (Exception e) { Say(e.Message); }
    }

    async Task ConnectAsync()
    {
        var url = Environment.GetEnvironmentVariable("JELLY_URL");
        var tok = Environment.GetEnvironmentVariable("JELLY_TOKEN");
        if (url != null && tok != null) { await UseAsync(new JellyfinClient(url, _s.DeviceId) { Token = tok }); return; }
        if (await Secrets.LoadAsync() is { } s)
        {
            try { if (await UseAsync(new JellyfinClient(s.Server, _s.DeviceId) { Token = s.Token, UserId = s.UserId })) return; }
            catch (ArgumentException) { }
        }
        await SignIn();
    }
    async Task<bool> UseAsync(JellyfinClient c)
    {
        try
        {
            Say("Reading library");
            _tracks = await c.AllTracksAsync();
            _client = c;
            _grids = _store.Grids();
            Filter();
            Say($"Connected to {c.Server}: {_tracks.Count} tracks");
            return true;
        }
        catch (Exception e) { Say("Jellyfin: " + e.Message); return e is not UnauthorizedAccessException; }
    }

    void Shoot(string png)
    {
        using var bmp = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
        bmp.Render(this);
        bmp.Save(png);
    }
    async Task ShootHelp(string png)
    {
        RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.F1, Source = this });
        await Task.Delay(200);
        Console.WriteLine("help open: " + _help.IsVisible);
        Shoot(png + ".help.png");
        RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape, Source = this });
        Console.WriteLine("help closed: " + !_help.IsVisible);
    }

    /// <summary>Silent end-to-end check: load, play, sync, drill; writes a picture of the window and exits.</summary>
    async Task SelfTest(string png)
    {
        _engine.MasterGain = _engine.CueGain = 0;
        if (_tracks.Count < 2)
        {
            Console.WriteLine("no library: window pictures only. " + _status.Text);
            await Task.Delay(500);
            Shoot(png);
            await ShootHelp(png);
            Close();
            return;
        }
        var pick = _tracks.Where(t => t.Title is "Rave Bar" or "Nowhere To Go").Concat(_tracks).Where(t => t.Seconds > 100).Take(2).ToList();
        Console.WriteLine($"dropouts at start {_out?.MasterUnderruns}/{_out?.CueUnderruns}");
        await LoadTrack(0, pick[0]);
        await LoadTrack(1, pick[1]);
        foreach (var d in _d) Console.WriteLine($"{d.Letter}: {d.Track?.Title} {d.Data.Grid} '{d.Status}'");
        Console.WriteLine($"dropouts after load {_out?.MasterUnderruns}/{_out?.CueUnderruns}");
        foreach (var d in _d) { d.Seek(40 + d.Index * 0.17); d.PlayPause(); }
        await Task.Delay(1500);
        Console.WriteLine("sync: " + (_d[1].Sync(_d[0]) ?? "ok"));
        foreach (int w in new[] { 1500, 20000 })
        {
            await Task.Delay(w);
            Console.WriteLine($"phase {BeatGrid.PhaseDiff(_d[1].Data.Grid!, _d[1].Seconds, _d[0].Data.Grid!, _d[0].Seconds) * _d[0].Data.Grid!.Period * 1000:F2} ms, bpm {_d[0].EffectiveBpm:F3} / {_d[1].EffectiveBpm:F3}, LUFS {_d[0].Channel.Loudness.Lufs:F1} / {_d[1].Channel.Loudness.Lufs:F1}");
        }
        foreach (bool q in new[] { true, false })
        {
            double Phase() => BeatGrid.PhaseDiff(_d[1].Data.Grid!, _d[1].Seconds, _d[0].Data.Grid!, _d[0].Seconds) * _d[0].Data.Grid!.Period * 1000;
            _assists.Quantize = q;
            _d[0].Data.HotCues[1] = 45.1 * 48000; // deliberately off the beat
            double before = Phase();
            _d[0].HotCue(1);
            await Task.Delay(300);
            Console.WriteLine($"hot cue, quantize {q}: phase {before:F2} -> {Phase():F2} ms, landed {_d[0].Seconds - 45.1:F2} s past the cue after 0.3 s");
        }
        _d[0].ClearHotCue(1);
        _assists.Quantize = true;
        _d[1].Sync(_d[0]);
        _d[0].HotCue(0); _d[1].ToggleLoop(); _d[1].Channel.Pfl = true;
        await Task.Delay(1000);
        Shoot(png);
        Console.WriteLine($"dropouts before drill {_out?.MasterUnderruns}/{_out?.CueUnderruns}");
        await ShootHelp(png);
        foreach (var d in _d) d.PlayPause();
        await Task.Delay(300);
        await StartDrill("Beatmatch");
        _d[1].PlayPause();
        await Task.Delay(2000);
        FinishDrill();
        Console.WriteLine("drill: " + _drillText.Text);
        Console.WriteLine($"dropouts {_out?.MasterUnderruns}/{_out?.CueUnderruns}");
        Close();
    }

    static Window Dialog(string title, Control c, double w = 480) =>
        new() { Title = title, Width = w, SizeToContent = SizeToContent.Height, MaxHeight = 800, Background = Ui.Panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = new Border { Padding = new(16), Child = c } } };

    async Task SignIn()
    {
        var server = new TextBox { Text = _s.Server, PlaceholderText = "http://server:8096" };
        var user = new TextBox { PlaceholderText = "User name" };
        var pw = new TextBox { PlaceholderText = "Password", PasswordChar = '*' };
        var msg = Ui.Label("", 12, Ui.Yellow); msg.TextWrapping = TextWrapping.Wrap;
        var cts = new CancellationTokenSource();
        Window? w = null;
        async Task Done(JellyfinClient c, Session s)
        {
            _s.Server = c.Server; _s.Save();
            bool saved = await Secrets.SaveAsync(s);
            await UseAsync(c);
            if (!saved) Say("Signed in, but the keyring is unavailable, so you will need to sign in again next time.");
            w!.Close();
        }
        async void Login()
        {
            try { var c = new JellyfinClient(server.Text ?? "", _s.DeviceId); await Done(c, await c.LoginAsync(user.Text ?? "", pw.Text ?? "", cts.Token)); }
            catch (Exception e) { msg.Text = e.Message; }
        }
        async void Quick()
        {
            try
            {
                var c = new JellyfinClient(server.Text ?? "", _s.DeviceId);
                var (code, secret) = await c.QuickConnectStartAsync(cts.Token);
                msg.Text = $"Enter code {code} in Jellyfin under your profile, Quick Connect.";
                for (int i = 0; i < 100; i++)
                {
                    await Task.Delay(3000, cts.Token);
                    if (await c.QuickConnectPollAsync(secret, cts.Token) is { } s) { await Done(c, s); return; }
                }
                msg.Text = "The code expired.";
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { msg.Text = e.Message; }
        }
        var note = Ui.Label("Over plain http your password is sent unencrypted. Use https outside your own network.", 11, Ui.Dim); note.TextWrapping = TextWrapping.Wrap;
        w = Dialog("Sign in to Jellyfin", Col(server, user, pw, Row(Ui.Btn("Sign in", Login), Ui.Btn("Quick Connect", Quick)), msg, note));
        w.Closed += (_, _) => cts.Cancel();
        await w.ShowDialog(this);
    }

    void ShowSettings()
    {
        var devs = Output.Devices().Select(d => d.Name).ToList();
        var m = new ComboBox { ItemsSource = devs, SelectedItem = _masterName, MinWidth = 400 };
        var c = new ComboBox { ItemsSource = devs.Prepend("(none: mix headphones into one output)").ToList(), MinWidth = 400 };
        c.SelectedIndex = devs.IndexOf(_s.CueDevice) + 1;
        m.SelectionChanged += (_, _) => { _s.MasterDevice = m.SelectedItem as string ?? ""; _s.Save(); OpenAudio(); };
        c.SelectionChanged += (_, _) => { _s.CueDevice = c.SelectedIndex <= 0 ? "" : devs[c.SelectedIndex - 1]; _s.Save(); OpenAudio(); };
        var testMsg = Ui.Label("Test plays a quiet tone: left ear, then right.", 11, Ui.Dim); testMsg.TextWrapping = TextWrapping.Wrap;
        Button testM = null!, testC = null!;
        void Enable(bool idle) { testM.IsEnabled = idle && m.SelectedIndex >= 0; testC.IsEnabled = idle && c.SelectedIndex > 0; }
        async void Test(string? name)
        {
            Enable(false);
            try
            {
                var dev = Output.Devices().FirstOrDefault(x => x.Name == name) ?? throw new InvalidOperationException("That output is no longer available.");
                testMsg.Text = "Playing on " + name;
                await TestTone.PlayAsync(dev);
                testMsg.Text = "Played on " + name + ": left, then right.";
            }
            catch (Exception e) { testMsg.Text = e.Message; }
            Enable(true);
        }
        testM = Ui.Btn("Test", () => Test(m.SelectedItem as string));
        testC = Ui.Btn("Test", () => Test(c.SelectedItem as string));
        m.SelectionChanged += (_, _) => Enable(true);
        c.SelectionChanged += (_, _) => Enable(true);
        Enable(true);
        var split = new CheckBox { Content = "Split mono headphones: cue left, master right", IsChecked = _s.SplitMono };
        split.IsCheckedChanged += (_, _) => { _s.SplitMono = split.IsChecked == true; _engine.PhoneMode = _s.SplitMono ? PhoneMode.SplitMono : PhoneMode.Blend; _s.Save(); };
        var lab = Ui.Label("");
        var off = new Slider { Minimum = -200, Maximum = 200, Value = _s.CueOffsetMs, TickFrequency = 1, IsSnapToTickEnabled = true };
        void Show() => lab.Text = $"Headphone delay: {off.Value:F0} ms (positive delays the headphones)";
        off.PropertyChanged += (_, e) => { if (e.Property != RangeBase.ValueProperty) return; _s.CueOffsetMs = _engine.CueOffsetMs = (float)off.Value; Show(); };
        Show();
        var tip = Ui.Label("To align: play one deck, turn on Phones for it, listen to both outputs and move this until they coincide.", 11, Ui.Dim); tip.TextWrapping = TextWrapping.Wrap;
        var w = Dialog("Settings", Col(Ui.Label("Master output"), Row(m, testM), Ui.Label("Headphone output"), Row(c, testC), testMsg, split, lab, off, tip, Ui.Btn("Forget sign-in", async () => { await Secrets.ClearAsync(); Say("Sign-in removed from the keyring"); })), 540);
        w.Closed += (_, _) => _s.Save();
        _ = w.ShowDialog(this);
    }

    void ShowKeys()
    {
        var col = Col(Ui.Label("Click a key, then press the new one. Escape cancels.", 12, Ui.Dim));
        var buttons = new Dictionary<string, Button>();
        string? waiting = null;
        void Refresh() { var by = _keys.ToDictionary(k => k.Value, k => Settings.KeyName(k.Key)); foreach (var (a, b) in buttons) b.Content = a == waiting ? "press a key" : by.GetValueOrDefault(a, "(none)"); RefreshHints(); }
        string section = "";
        foreach (var a in Settings.DefaultKeys.Keys)
        {
            if (section != Settings.Group(a)) col.Children.Add(Ui.Label(section = Settings.Group(a), 14, Ui.Yellow));
            buttons[a] = Ui.Btn("", () => { waiting = a; Refresh(); });
            var l = Ui.Label(Settings.ActionName(a)); l.Width = 190;
            col.Children.Add(Row(l, buttons[a]));
        }
        col.Children.Add(Ui.Btn("Restore defaults", () => { _s.Keys.Clear(); _s.Save(); _keys = _s.KeyMap(); Refresh(); }));
        Refresh();
        var w = Dialog("Keyboard", col, 380);
        w.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (waiting == null) return;
            if (e.Key == Key.F1) { e.Handled = true; return; } // reserved for help
            if (e.Key != Key.Escape)
            {
                if (_keys.TryGetValue(e.Key, out var other) && other != waiting) // swap so no action loses its key
                    _s.Keys[other] = _keys.First(k => k.Value == waiting).Key.ToString();
                _s.Keys[waiting] = e.Key.ToString();
                _s.Save();
                _keys = _s.KeyMap();
            }
            waiting = null;
            Refresh();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _ = w.ShowDialog(this);
    }

    void ShowHistory()
    {
        var col = Col();
        foreach (var r in _store.Drills())
            col.Children.Add(Ui.Label($"{r.When.ToLocalTime():g}   {r.Kind}   score {r.Score}   " + (r.Kind == "Level" ? $"{r.LevelErrorDb:+0.0;-0.0} LU" : $"{r.TempoErrorPct:+0.00;-0.00} %, {r.PhaseErrorMs:+0;-0} ms") + $"   {r.Seconds:F0} s"));
        if (col.Children.Count == 0) col.Children.Add(Ui.Label("No drills yet."));
        _ = Dialog("Drill history", col, 520).ShowDialog(this);
    }
}
