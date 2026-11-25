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
