using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace JellyDiscer.Jellyfin;

public sealed record Track(string Id, string Title, string Artist, string Album, string Genre, double Seconds, double? GainDb);
public sealed record Session(string Server, string UserId, string UserName, string Token);

public sealed partial class JellyfinClient
{
    readonly HttpClient _http;
    readonly string _base, _deviceId;
    public string? Token { get; set; }
    public string? UserId { get; set; }

    public JellyfinClient(string server, string deviceId, HttpMessageHandler? handler = null)
    {
        if (!server.Contains("://")) server = "http://" + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new ArgumentException("The server address must be an http or https URL.");
        _base = u.ToString().TrimEnd('/');
        _deviceId = deviceId;
        // no redirects: the token must never be forwarded to another host
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromMinutes(5) };
    }
    public string Server => _base;

    HttpRequestMessage Req(HttpMethod m, string path, object? body = null)
    {
        var r = new HttpRequestMessage(m, _base + path);
        string h = $"Client=\"jelly-discer\", Device=\"Linux\", DeviceId=\"{_deviceId}\", Version=\"0.1\"";
        if (Token != null) h += $", Token=\"{Token}\"";
        r.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", h);
        if (body != null) r.Content = JsonContent.Create(body, options: new JsonSerializerOptions());
        return r;
    }

    async Task<JsonElement> Json(HttpMethod m, string path, object? body = null, CancellationToken ct = default)
    {
        using var r = await _http.SendAsync(Req(m, path, body), ct);
        Ensure(r);
        return await r.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    static void Ensure(HttpResponseMessage r)
    {
        if (r.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Jellyfin rejected the sign-in.");
        if ((int)r.StatusCode is >= 300 and < 400) throw new HttpRequestException("The server redirected the request; enter its final address.");
        r.EnsureSuccessStatusCode();
    }

    public async Task<Session> LoginAsync(string user, string password, CancellationToken ct = default) =>
        Accept(await Json(HttpMethod.Post, "/Users/AuthenticateByName", new { Username = user, Pw = password }, ct));

    public async Task<(string code, string secret)> QuickConnectStartAsync(CancellationToken ct = default)
    {
        var j = await Json(HttpMethod.Post, "/QuickConnect/Initiate", null, ct);
        return (j.GetProperty("Code").GetString()!, j.GetProperty("Secret").GetString()!);
    }
    public async Task<Session?> QuickConnectPollAsync(string secret, CancellationToken ct = default)
    {
        var j = await Json(HttpMethod.Get, "/QuickConnect/Connect?secret=" + Uri.EscapeDataString(secret), null, ct);
        if (!j.GetProperty("Authenticated").GetBoolean()) return null;
        return Accept(await Json(HttpMethod.Post, "/Users/AuthenticateWithQuickConnect", new { Secret = secret }, ct));
    }
    Session Accept(JsonElement j)
    {
        var u = j.GetProperty("User");
        Token = j.GetProperty("AccessToken").GetString()!;
        UserId = u.GetProperty("Id").GetString()!;
        return new(_base, UserId, u.GetProperty("Name").GetString()!, Token);
    }

    public async Task<List<Track>> AllTracksAsync(CancellationToken ct = default)
    {
        var all = new List<Track>();
        const int page = 500;
        while (true)
        {
            string q = $"/Items?IncludeItemTypes=Audio&Recursive=true&Fields=Genres&SortBy=SortName&EnableImages=false&StartIndex={all.Count}&Limit={page}";
            if (UserId != null) q += "&UserId=" + UserId;
            var j = await Json(HttpMethod.Get, q, null, ct);
            int n = 0;
            foreach (var i in j.GetProperty("Items").EnumerateArray()) { all.Add(Parse(i)); n++; }
            if (n == 0 || all.Count >= j.GetProperty("TotalRecordCount").GetInt32()) return all;
        }
    }

    static Track Parse(JsonElement i)
    {
        string S(string n) => i.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        string L(string n) => i.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? string.Join(", ", v.EnumerateArray().Select(x => x.GetString())) : "";
        double? D(string n) => i.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var artist = L("Artists");
        return new(S("Id"), S("Name"), artist.Length > 0 ? artist : S("AlbumArtist"), S("Album"), L("Genres"), (D("RunTimeTicks") ?? 0) / 1e7, D("NormalizationGain"));
    }

    public async Task DownloadAsync(string id, string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var r = await _http.SendAsync(Req(HttpMethod.Get, $"/Audio/{Guid.Parse(id):N}/stream?static=true"), HttpCompletionOption.ResponseHeadersRead, ct);
        Ensure(r);
        long? len = r.Content.Headers.ContentLength;
        string part = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var f = File.Create(part))
            await using (var s = await r.Content.ReadAsStreamAsync(ct))
            {
                var b = new byte[1 << 17];
                long done = 0;
                int n;
                while ((n = await s.ReadAsync(b, ct)) > 0)
                {
                    await f.WriteAsync(b.AsMemory(0, n), ct);
                    done += n;
                    if (len > 0) progress?.Report(done / (double)len);
                }
                if (len != null && done != len) throw new IOException("The download was cut short.");
            }
            File.Move(part, path, true);
        }
        finally { File.Delete(part); }
    }

    public async Task<byte[]?> ImageAsync(string id, CancellationToken ct = default)
    {
        using var r = await _http.SendAsync(Req(HttpMethod.Get, $"/Items/{Guid.Parse(id):N}/Images/Primary?maxWidth=160"), ct);
        return r.IsSuccessStatusCode ? await r.Content.ReadAsByteArrayAsync(ct) : null;
    }
}

/// <summary>Original files on disk, oldest-used removed first.</summary>
public sealed class TrackCache(string dir, long maxBytes = 8L << 30)
{
    public async Task<string> GetAsync(JellyfinClient c, string id, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.Parse(id).ToString("N") + ".audio");
        if (!File.Exists(path)) await c.DownloadAsync(id, path, progress, ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        Trim(path);
        return path;
    }
    void Trim(string keep)
    {
        var files = new DirectoryInfo(dir).GetFiles("*.audio").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long sum = 0;
        foreach (var f in files)
        {
            sum += f.Length;
            if (sum > maxBytes && f.FullName != keep) f.Delete();
        }
    }
}
