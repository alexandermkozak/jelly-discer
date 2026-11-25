namespace JellyDiscer.Jellyfin;

/// <summary>One library row. Entry is set when the row belongs to the open playlist; Previous is the track before it there.</summary>
public sealed record LibRow(Track T, PlaylistEntry? Entry = null, Track? Previous = null);

/// <summary>The open playlist. The server's copy is the truth: every edit is sent, then the list is read back. One edit runs at a time.</summary>
public sealed class SetList(Func<JellyfinClient?> client)
{
    public List<Playlist> All { get; private set; } = [];
    public Playlist? Current { get; private set; }
    public List<PlaylistEntry> Entries { get; private set; } = [];
    public bool Busy { get; private set; }
    public event Action? Changed;
    (string list, string track, string title, int index)? _removed;
    public string? UndoTitle => _removed?.title;

    JellyfinClient C => client() ?? throw new InvalidOperationException("Sign in first.");

    async Task<string> Run(Func<Task<string>> work)
    {
        if (Busy) return "Still saving the previous change";
        Busy = true;
        try { return await work(); }
        catch (Exception e) { return "Playlist: " + e.Message; }
        finally { Busy = false; Changed?.Invoke(); }
    }

    async Task Reload()
    {
        if (Current is { } c) Entries = await C.PlaylistAsync(c.Id);
    }
    async Task Refresh()
    {
        All = await C.PlaylistsAsync();
        if (Current is { } c) Current = All.FirstOrDefault(p => p.Id == c.Id);
        if (Current == null) Entries = [];
    }
    /// <summary>Reads back after an edit, so a failure half way still shows what the server has.</summary>
    async Task Edit(Func<Task> edit)
    {
        try { await edit(); }
        finally
        {
            await Refresh();
            await Reload();
        }
    }

    public Task<string> RefreshAsync() => Run(async () => { await Refresh(); await Reload(); return ""; });
    public Task<string> OpenAsync(string? id) => Run(async () =>
    {
        Current = null;
        Entries = [];
        if (id == null) return "";
        Current = All.FirstOrDefault(p => p.Id == id);
        await Reload();
        return "";
    });
    public Task<string> CreateAsync(string name, Track? first) => Run(async () =>
    {
        string id = await C.CreatePlaylistAsync(name, first == null ? [] : [first.Id]);
        All = await C.PlaylistsAsync();
        Current = All.FirstOrDefault(p => p.Id == id);
        Entries = [];
        await Reload();
        return $"Created {name}";
    });
    public Task<string> RenameAsync(string name) => Run(async () =>
    {
        if (Current is not { } c) return "Open a playlist first";
        await Edit(() => C.RenamePlaylistAsync(c.Id, name));
        return $"Renamed to {name}";
    });
    public Task<string> AddAsync(Playlist p, Track t) => Run(async () =>
    {
        // Jellyfin silently ignores duplicates, so look first
        if ((await C.PlaylistAsync(p.Id)).Any(e => e.Track.Id == t.Id)) return $"{t.Title} is already in {p.Name}";
        await Edit(() => C.AddToPlaylistAsync(p.Id, t.Id));
        return $"Added {t.Title} to {p.Name}";
    });
    public Task<string> RemoveAsync(PlaylistEntry e) => Run(async () =>
    {
        if (Current is not { } c) return "Open a playlist first";
        await Edit(() => C.RemoveFromPlaylistAsync(c.Id, e.EntryId));
        _removed = (c.Id, e.Track.Id, e.Track.Title, e.Index);
        return $"Removed {e.Track.Title} from {c.Name}";
    });
    public Task<string> UndoAsync() => Run(async () =>
    {
        if (_removed is not { } r) return "Nothing to undo";
        var now = await C.PlaylistAsync(r.list);
        if (now.Any(e => e.Track.Id == r.track)) { _removed = null; return $"{r.title} is already back"; }
        // a position past the end means append
        await Edit(() => C.AddToPlaylistAsync(r.list, r.track, now.Count == 0 || r.index > now[^1].Index ? null : r.index));
        _removed = null;
        return $"Put {r.title} back";
    });
    public Task<string> MoveAsync(PlaylistEntry e, int step) => Run(async () =>
    {
        if (Current is not { } c) return "Open a playlist first";
        int at = Entries.FindIndex(x => x.EntryId == e.EntryId), to = at + step;
        if (at < 0 || to < 0 || to >= Entries.Count) return "";
        await Edit(() => C.MoveInPlaylistAsync(c.Id, e.EntryId, Entries[to].Index));
        return $"Moved {e.Track.Title} to {Entries.FindIndex(x => x.EntryId == e.EntryId) + 1}";
    });

    public PlaylistEntry? Find(Track? t) => t == null ? null : Entries.FirstOrDefault(e => e.Track.Id == t.Id);
    /// <summary>The track to load next: after the one on the other deck, else after this deck's, else the first.</summary>
    public Track? NextFor(Track? mine, Track? other)
    {
        if (Entries.Count == 0) return null;
        var from = Find(other) ?? Find(mine);
        if (from == null) return Entries[0].Track;
        int i = Entries.IndexOf(from) + 1;
        return i < Entries.Count ? Entries[i].Track : null;
    }
}
