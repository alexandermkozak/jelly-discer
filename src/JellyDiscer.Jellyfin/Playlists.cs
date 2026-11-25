using System.Net;
using System.Text.Json;

namespace JellyDiscer.Jellyfin;

public sealed record Playlist(string Id, string Name, int Count);
/// <summary>Index is the position on the server, counting entries of every kind.</summary>
public sealed record PlaylistEntry(string EntryId, int Index, Track Track);

public sealed partial class JellyfinClient
{
    string User => UserId ?? throw new InvalidOperationException("Playlists need a signed-in user.");
    static string G(string id) => Guid.Parse(id).ToString("N");

    public async Task<List<Playlist>> PlaylistsAsync(CancellationToken ct = default)
    {
        var j = await Json(HttpMethod.Get, $"/Items?IncludeItemTypes=Playlist&Recursive=true&Fields=ChildCount&SortBy=SortName&UserId={G(User)}", null, ct);
        return j.GetProperty("Items").EnumerateArray()
            .Where(i => !i.TryGetProperty("MediaType", out var m) || m.GetString() is "Audio" or "Unknown" or null)
            .Select(i => new Playlist(i.GetProperty("Id").GetString()!, i.GetProperty("Name").GetString() ?? "", i.TryGetProperty("ChildCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0))
            .ToList();
    }

    public async Task<List<PlaylistEntry>> PlaylistAsync(string id, CancellationToken ct = default)
    {
        var all = new List<PlaylistEntry>();
        for (int index = 0; ;)
        {
            var j = await Json(HttpMethod.Get, $"/Playlists/{G(id)}/Items?userId={G(User)}&startIndex={index}&limit=500&fields=Genres&enableImages=false", null, ct);
            int n = 0;
            foreach (var i in j.GetProperty("Items").EnumerateArray())
            {
                if (i.GetProperty("Type").GetString() == "Audio" && i.TryGetProperty("PlaylistItemId", out var e) && e.GetString() is { } entry)
                    all.Add(new(entry, index + n, Parse(i)));
                n++;
            }
            index += n;
            if (n == 0 || index >= j.GetProperty("TotalRecordCount").GetInt32()) return all;
        }
    }

    /// <summary>Creates a private playlist owned by the signed-in user and returns its id.</summary>
    public async Task<string> CreatePlaylistAsync(string name, IEnumerable<string> trackIds, CancellationToken ct = default)
    {
        var j = await Json(HttpMethod.Post, "/Playlists", new { Name = name, Ids = trackIds.Select(G).ToArray(), UserId = G(User), MediaType = "Audio", IsPublic = false }, ct);
        return j.GetProperty("Id").GetString()!;
    }
    public Task RenamePlaylistAsync(string id, string name, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"/Playlists/{G(id)}", new { Name = name }, ct);
    /// <summary>Adds at the end, or before the entry at a position.</summary>
    public Task AddToPlaylistAsync(string id, string trackId, int? position = null, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"/Playlists/{G(id)}/Items?ids={G(trackId)}&userId={G(User)}" + (position is { } p ? $"&position={p}" : ""), null, ct);
    public Task RemoveFromPlaylistAsync(string id, string entryId, CancellationToken ct = default) =>
        Send(HttpMethod.Delete, $"/Playlists/{G(id)}/Items?entryIds={Uri.EscapeDataString(entryId)}", null, ct);
    public Task MoveInPlaylistAsync(string id, string entryId, int newIndex, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"/Playlists/{G(id)}/Items/{Uri.EscapeDataString(entryId)}/Move/{newIndex}", null, ct);

    async Task Send(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        using var r = await _http.SendAsync(Req(m, path, body), ct);
        if (r.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("This account may not edit that playlist.");
        if (r.StatusCode == HttpStatusCode.NotFound) throw new HttpRequestException("The playlist or track is no longer on the server.");
        Ensure(r);
    }
}
