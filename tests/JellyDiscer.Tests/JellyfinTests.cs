using System.Net;
using JellyDiscer.Core.Analysis;
using JellyDiscer.Core.Audio;
using JellyDiscer.Jellyfin;
using Xunit.Abstractions;
namespace JellyDiscer.Tests;

public class JellyfinTests(ITestOutputHelper log)
{
    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(f(r));
    }
    static HttpResponseMessage J(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, null, "application/json") };
    [Fact]
    public async Task LoginKeepsToken()
    {
        var c = new JellyfinClient("http://x:8096/", "d", new Fake(r => J("""{"User":{"Id":"u","Name":"n"},"AccessToken":"tok"}""")));
        Assert.Equal("tok", (await c.LoginAsync("n", "p")).Token); Assert.Equal("u", c.UserId);
    }
    [Fact]
    public async Task PagesThroughItems()
    {
        int calls = 0;
        var c = new JellyfinClient("x:8096", "d", new Fake(r =>
        {
            calls++;
            Assert.Contains("Token=\"t\"", r.Headers.Authorization!.ToString()); Assert.DoesNotContain("=t&", r.RequestUri!.Query);
            return J(calls == 1 ? """{"TotalRecordCount":2,"Items":[{"Id":"1","Name":"A","Artists":["Z"],"RunTimeTicks":10000000,"NormalizationGain":-3.5,"Genres":["g"]}]}""" : """{"TotalRecordCount":2,"Items":[{"Id":"2","Name":"B"}]}""");
        })) { Token = "t" };
        var l = await c.AllTracksAsync();
        Assert.Equal(2, l.Count); Assert.Equal(-3.5, l[0].GainDb); Assert.Equal(1, l[0].Seconds); Assert.Null(l[1].GainDb);
    }
    [Fact]
    public async Task RejectsBadIdsRedirectsAndAuthFailure()
    {
        var c = new JellyfinClient("x", "d", new Fake(_ => new(HttpStatusCode.Redirect)));
        await Assert.ThrowsAsync<FormatException>(() => c.DownloadAsync("../../x", "/tmp/never"));
        await Assert.ThrowsAsync<HttpRequestException>(() => c.AllTracksAsync());
        var u = new JellyfinClient("x", "d", new Fake(_ => new(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => u.AllTracksAsync());
        Assert.Throws<ArgumentException>(() => new JellyfinClient("ftp://x", "d"));
    }
    const string L = "00000000000000000000000000000001", T1 = "0000000000000000000000000000000a", U = "0000000000000000000000000000000f";
    [Fact]
    public async Task PlaylistRequests()
    {
        var log = new List<string>();
        var c = new JellyfinClient("http://x", "d", new Fake(r =>
        {
            log.Add($"{r.Method} {r.RequestUri!.PathAndQuery} {r.Content?.ReadAsStringAsync().Result}");
            if (r.Method == HttpMethod.Get && r.RequestUri.AbsolutePath.StartsWith("/Playlists"))
                return J($$"""{"TotalRecordCount":3,"Items":[{"Type":"Audio","Id":"a","PlaylistItemId":"e1","Name":"A"},{"Type":"Movie","Id":"m","PlaylistItemId":"e2","Name":"M"},{"Type":"Audio","Id":"b","PlaylistItemId":"e3","Name":"B"}]}""");
            if (r.Method == HttpMethod.Get) return J("""{"Items":[{"Id":"1","Name":"P","MediaType":"Audio","ChildCount":4},{"Id":"2","Name":"V","MediaType":"Video"}]}""");
            return r.RequestUri.AbsolutePath == "/Playlists" ? J("""{"Id":"new"}""") : new(HttpStatusCode.NoContent);
        })) { Token = "t", UserId = U };
        Assert.Equal(new Playlist("1", "P", 4), Assert.Single(await c.PlaylistsAsync()));
        var e = await c.PlaylistAsync(L);
        Assert.Equal([("e1", 0), ("e3", 2)], e.Select(x => (x.EntryId, x.Index)));
        Assert.Equal("new", await c.CreatePlaylistAsync("Set", [T1]));
        Assert.Contains("\"Name\":\"Set\"", log[^1]); Assert.Contains("\"IsPublic\":false", log[^1]); Assert.Contains(T1, log[^1]);
        await c.AddToPlaylistAsync(L, T1, 3); Assert.Equal($"POST /Playlists/{L}/Items?ids={T1}&userId={U}&position=3 ", log[^1]);
        await c.RemoveFromPlaylistAsync(L, "e1"); Assert.Equal($"DELETE /Playlists/{L}/Items?entryIds=e1 ", log[^1]);
        await c.MoveInPlaylistAsync(L, "e1", 2); Assert.Equal($"POST /Playlists/{L}/Items/e1/Move/2 ", log[^1]);
        await c.RenamePlaylistAsync(L, "N"); Assert.Equal($"POST /Playlists/{L} {{\"Name\":\"N\"}}", log[^1]);
        await Assert.ThrowsAsync<FormatException>(() => c.RemoveFromPlaylistAsync("../x", "e"));
    }
    [Fact]
    public async Task PlaylistPermissionErrors()
    {
        var c = new JellyfinClient("x", "d", new Fake(_ => new(HttpStatusCode.Forbidden))) { UserId = U };
        Assert.Contains("may not edit", (await Assert.ThrowsAsync<UnauthorizedAccessException>(() => c.MoveInPlaylistAsync(L, "e", 0))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JellyfinClient("x", "d").PlaylistsAsync());
    }
    [Theory] [InlineData(174, 170, 2.35)] [InlineData(87, 174, 0)] [InlineData(128, 130, -1.54)] [InlineData(174, 90, -3.33)]
    public void TempoSteps(double from, double to, double pct) => Assert.Equal(pct, TempoStep.Percent(from, to), 2);

    /// <summary>Opt-in, writes to the server: needs JELLY_WRITE=1 and JELLY_USER too. Works only in a playlist it creates, then deletes that one.</summary>
    [Fact]
    public async Task LivePlaylistEditing()
    {
        if (Environment.GetEnvironmentVariable("JELLY_WRITE") != "1") return;
        const string name = "jelly-discer test";
        string url = Environment.GetEnvironmentVariable("JELLY_URL")!, token = Environment.GetEnvironmentVariable("JELLY_TOKEN")!;
        var c = new JellyfinClient(url, "test") { Token = token, UserId = Environment.GetEnvironmentVariable("JELLY_USER") };
        var before = await c.PlaylistsAsync();
        Assert.DoesNotContain(before, p => p.Name.StartsWith(name));
        var t = (await c.AllTracksAsync()).Where(x => x.Seconds > 120).Take(4).ToList();
        var set = new SetList(() => c);
        string? id = null;
        void Is(string step, params int[] order)
        {
            log.WriteLine($"{step}: {string.Join(", ", set.Entries.Select(e => e.Track.Title))}");
            Assert.Equal(order.Select(i => t[i].Id), set.Entries.Select(e => e.Track.Id));
        }
        try
        {
            log.WriteLine(await set.CreateAsync(name, t[0]));
            Assert.NotNull(set.Current);
            id = set.Current!.Id;
            Assert.Equal(name, set.Current.Name);
            Is("created", 0);
            for (int i = 1; i < 4; i++) log.WriteLine(await set.AddAsync(set.Current!, t[i]));
            Is("added", 0, 1, 2, 3);
            Assert.Contains("already", await set.AddAsync(set.Current!, t[2]));
            // the move endpoint identifies the user from the login alone, so it can refuse an API key
            var moved = await set.MoveAsync(set.Entries[3], -1);
            log.WriteLine("move: " + moved);
            int[] o = [0, 1, 2, 3];
            if (!moved.StartsWith("Playlist:"))
            {
                Is("up", 0, 1, 3, 2);
                log.WriteLine(await set.MoveAsync(set.Entries[0], 1)); Is("down", 1, 0, 3, 2);
                Assert.Equal("", await set.MoveAsync(set.Entries[0], -1));
                o = [1, 0, 3, 2];
            }
            else
            {
                Assert.Null(Environment.GetEnvironmentVariable("JELLY_USER_LOGIN")); // set when the token is a real login: then it must work
                using var h = new HttpClient();
                var r = new HttpRequestMessage(HttpMethod.Post, $"{url}/Playlists/{id}/Items/{set.Entries[3].EntryId}/Move/2");
                r.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{token}\"");
                log.WriteLine("SKIPPED reordering; server says: " + await (await h.SendAsync(r)).Content.ReadAsStringAsync());
            }
            foreach (int at in new[] { 1, 0, 3 })
            {
                log.WriteLine(await set.RemoveAsync(set.Entries[at])); Is("removed", o.Where((_, i) => i != at).ToArray());
                log.WriteLine(await set.UndoAsync()); Is("undone", o);
            }
            var renamed = await set.RenameAsync(name + " renamed"); // also identifies the user from the login alone
            log.WriteLine("rename: " + renamed);
            if (renamed.StartsWith("Playlist:")) Assert.Null(Environment.GetEnvironmentVariable("JELLY_USER_LOGIN"));
            else Assert.Equal(name + " renamed", set.Current!.Name);
            Is("renamed keeps tracks", o);
        }
        finally
        {
            id ??= (await c.PlaylistsAsync()).SingleOrDefault(p => p.Name == name && before.All(b => b.Id != p.Id))?.Id;
            if (id != null && before.All(b => b.Id != id) && (await c.PlaylistsAsync()).Single(p => p.Id == id).Name.StartsWith(name))
            {
                using var h = new HttpClient();
                var r = new HttpRequestMessage(HttpMethod.Delete, $"{url}/Items/{Guid.Parse(id):N}");
                r.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{token}\"");
                log.WriteLine($"delete {id}: {(int)(await h.SendAsync(r)).StatusCode}");
            }
        }
        Assert.Equal(before.Select(p => p.Id), (await c.PlaylistsAsync()).Select(p => p.Id));
    }

    /// <summary>Opt-in: set JELLY_URL and JELLY_TOKEN.</summary>
    [Fact]
    public async Task Live()
    {
        var url = Environment.GetEnvironmentVariable("JELLY_URL");
        if (url == null) return;
        var c = new JellyfinClient(url, "test") { Token = Environment.GetEnvironmentVariable("JELLY_TOKEN") };
        var all = await c.AllTracksAsync();
        log.WriteLine($"{all.Count} tracks");
        Assert.NotEmpty(all);
        var dir = Directory.CreateTempSubdirectory("jelly").FullName;
        try
        {
            var cache = new TrackCache(dir);
            var rnd = new Random(3);
            foreach (var t in all.Where(x => x.Seconds is > 120 and < 500).OrderBy(_ => rnd.Next()).Take(14))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var b = await TrackBuffer.DecodeAsync(await cache.GetAsync(c, t.Id));
                var dec = sw.ElapsedMilliseconds; sw.Restart();
                var g = Analyzer.DetectGrid(b);
                var an = sw.ElapsedMilliseconds; sw.Restart();
                Analyzer.BuildWaveform(b);
                log.WriteLine($"{g?.Bpm,8:F2} conf {g?.Confidence:F2} load {dec} ms, grid {an} ms, wave {sw.ElapsedMilliseconds} ms  [{t.Genre}] {t.Artist} - {t.Title}");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
