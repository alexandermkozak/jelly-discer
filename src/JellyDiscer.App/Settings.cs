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
    public Dictionary<Key, string> KeyMap()
    {
        var m = new Dictionary<Key, string>();
        foreach (var (a, d) in DefaultKeys)
            m[Keys.TryGetValue(a, out var s) && Enum.TryParse<Key>(s, out var k) ? k : d] = a;
        return m;
    }
}
