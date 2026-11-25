using System.Text.Json;
using Avalonia.Input;

namespace JellyDiscer.App;

/// <summary>Preferences in ~/.config/jelly-discer. Never holds credentials.</summary>
public sealed class Settings
{
    public string Server { get; set; } = "";
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string MasterDevice { get; set; } = "";
    public string CueDevice { get; set; } = "";
    public float CueOffsetMs { get; set; }
    public bool SplitMono { get; set; }
    public float CueBlend { get; set; }
    public bool KeyHints { get; set; } = true;
    public Dictionary<string, string> Keys { get; set; } = [];

    public static string ConfigDir => Dir("XDG_CONFIG_HOME", ".config");
    public static string CacheDir => Dir("XDG_CACHE_HOME", ".cache");
    public static string DataDir => Dir("XDG_DATA_HOME", ".local/share");
    static string Dir(string env, string fallback)
    {
        var b = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrEmpty(b)) b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
        return Path.Combine(b, "jelly-discer");
    }
    static string File => Path.Combine(ConfigDir, "settings.json");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static readonly Dictionary<string, Key> DefaultKeys = new()
    {
        ["a.play"] = Key.Q, ["a.cue"] = Key.W, ["a.nudge-"] = Key.A, ["a.nudge+"] = Key.S, ["a.tempo-"] = Key.Z, ["a.tempo+"] = Key.X, ["a.pfl"] = Key.E,
        ["a.hot1"] = Key.D1, ["a.hot2"] = Key.D2, ["a.hot3"] = Key.D3, ["a.hot4"] = Key.D4, ["a.loop"] = Key.R, ["a.sync"] = Key.T, ["a.tap"] = Key.G,
        ["b.play"] = Key.P, ["b.cue"] = Key.O, ["b.nudge-"] = Key.K, ["b.nudge+"] = Key.L, ["b.tempo-"] = Key.N, ["b.tempo+"] = Key.M, ["b.pfl"] = Key.I,
        ["b.hot1"] = Key.D7, ["b.hot2"] = Key.D8, ["b.hot3"] = Key.D9, ["b.hot4"] = Key.D0, ["b.loop"] = Key.U, ["b.sync"] = Key.Y, ["b.tap"] = Key.H,
        ["xfade-"] = Key.Left, ["xfade+"] = Key.Right, ["drill.done"] = Key.Enter
    };
    static readonly Dictionary<string, string> Names = new()
    {
        ["play"] = "Play / pause", ["cue"] = "Cue (hold to preview)", ["nudge-"] = "Nudge slower (hold)", ["nudge+"] = "Nudge faster (hold)",
        ["tempo-"] = "Tempo down", ["tempo+"] = "Tempo up", ["pfl"] = "Phones on / off", ["hot1"] = "Hot cue 1", ["hot2"] = "Hot cue 2",
        ["hot3"] = "Hot cue 3", ["hot4"] = "Hot cue 4", ["loop"] = "Loop on / off", ["sync"] = "Sync", ["tap"] = "Tap the beat",
        ["xfade-"] = "Crossfader toward A", ["xfade+"] = "Crossfader toward B", ["drill.done"] = "Finish drill"
    };
    public static string Group(string action) => action.StartsWith("a.") ? "Deck A" : action.StartsWith("b.") ? "Deck B" : "Mixer and drills";
    public static string ActionName(string action) => Names[action is ['a' or 'b', '.', ..] ? action[2..] : action];
    public static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        Key.Left => "\u2190", Key.Right => "\u2192", Key.Up => "\u2191", Key.Down => "\u2193",
        Key.Enter => "Enter", Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemPlus => "=",
        Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.OemQuestion => "/", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
        _ => k.ToString()
    };
    public Dictionary<Key, string> KeyMap()
    {
        var m = new Dictionary<Key, string>();
        foreach (var (a, d) in DefaultKeys)
            m[Keys.TryGetValue(a, out var s) && Enum.TryParse<Key>(s, out var k) ? k : d] = a;
        return m;
    }
}
