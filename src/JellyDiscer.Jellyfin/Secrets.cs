using System.Diagnostics;
using System.Text.Json;

namespace JellyDiscer.Jellyfin;

/// <summary>Keeps the session in the desktop keyring through secret-tool (libsecret). The token goes over stdin, never the command line.</summary>
public static class Secrets
{
    static async Task<(int code, string text)> Run(string? input, params string[] args)
    {
        var psi = new ProcessStartInfo("secret-tool") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (input != null) await p.StandardInput.WriteAsync(input);
        p.StandardInput.Close();
        await p.WaitForExitAsync();
        await e;
        return (p.ExitCode, await o);
    }
    public static async Task<bool> SaveAsync(Session s)
    {
        try { return (await Run(JsonSerializer.Serialize(s), "store", "--label=jelly-discer", "app", "jelly-discer", "kind", "session")).code == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    public static async Task<Session?> LoadAsync()
    {
        try
        {
            var (c, t) = await Run(null, "lookup", "app", "jelly-discer", "kind", "session");
            return c == 0 && t.Length > 0 ? JsonSerializer.Deserialize<Session>(t) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or JsonException) { return null; }
    }
    public static async Task ClearAsync()
    {
        try { await Run(null, "clear", "app", "jelly-discer", "kind", "session"); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
