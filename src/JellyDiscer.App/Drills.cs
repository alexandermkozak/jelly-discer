using JellyDiscer.Core.Analysis;
using JellyDiscer.Core.Learning;
using JellyDiscer.Jellyfin;

namespace JellyDiscer.App;

/// <summary>Beatmatch and level exercises. Deck A is the reference, the player adjusts deck B.</summary>
public sealed class Drills(DeckController a, DeckController b, Store store, Func<IReadOnlyList<Track>> tracks)
{
    public string? Active { get; private set; }
    DateTime _start;
    public double Elapsed => (DateTime.UtcNow - _start).TotalSeconds;

    /// <summary>Picks and loads two tracks; returns instructions or the reason it cannot start.</summary>
    public async Task<string> StartAsync(string kind, Func<int, Track, Task<bool>> load)
    {
        if (a.Deck.Playing || b.Deck.Playing) return "Pause both decks first.";
        var grids = store.Grids();
        var pool = tracks().Where(t => t.Seconds > 90 && grids.TryGetValue(t.Id, out var g) && g.Confident).ToList();
        var pairs = (from x in pool from y in pool
                     where x.Id != y.Id && (kind == "Level" || Math.Abs(grids[x.Id].Bpm / grids[y.Id].Bpm - 1) < 0.06)
                     select (x, y)).ToList();
        if (pairs.Count == 0) return "No suitable pair yet. Load or analyze more tracks with a clear beat at similar tempos.";
        var (ta, tb) = pairs[Random.Shared.Next(pairs.Count)];
        if (!await load(0, ta) || !await load(1, tb)) return "Could not load the drill tracks.";
        a.Deck.Tempo = 0;
        b.Range = a.Range = 0.08f;
        a.Channel.Fader = b.Channel.Fader = 1;
        double match = a.Data.Grid!.Bpm / b.Data.Grid!.Bpm - 1;
        if (kind == "Beatmatch")
        {
            double off = (0.015 + Random.Shared.NextDouble() * 0.03) * (Random.Shared.Next(2) * 2 - 1);
            if (Math.Abs(match + off) > 0.08) off = -off;
            b.Deck.Tempo = (float)Math.Clamp(match + off, -0.08, 0.08);
        }
        else
        {
            b.Deck.Tempo = 0;
            double off = (3 + Random.Shared.NextDouble() * 5) * (Random.Shared.Next(2) * 2 - 1);
            b.Channel.TrimDb = (float)Math.Clamp(b.Channel.TrimDb + off, -24, 12);
        }
        a.PlayPause();
        _start = DateTime.UtcNow;
        Active = kind;
        return kind == "Beatmatch"
            ? "Deck A is playing. Cue B in your headphones, match its tempo and beats by ear, then press Done with both playing."
            : "Deck A is playing. Start B and set its Trim so both sound equally loud, then press Done with both playing for a few seconds.";
    }

    public string Finish()
    {
        if (Active == null) return "";
        if (!a.Deck.Playing || !b.Deck.Playing) return "Both decks must be playing when you press Done.";
        if (a.Data.Grid is not { } ga || b.Data.Grid is not { } gb) return "A beatgrid is missing.";
        double secs = Elapsed;
        DrillResult r;
        string msg;
        if (Active == "Beatmatch")
        {
            double tempo = (b.EffectiveBpm / a.EffectiveBpm - 1) * 100;
            double phase = BeatGrid.PhaseDiff(gb, b.Seconds, ga, a.Seconds) * 60000 / b.EffectiveBpm;
            r = new(DateTime.UtcNow, Active, tempo, phase, 0, secs, Scoring.Beatmatch(tempo, phase, secs));
            msg = $"Score {r.Score}. B was {Math.Abs(tempo):F2} % {(tempo > 0 ? "fast" : "slow")} and {Math.Abs(phase):F0} ms {(phase > 0 ? "ahead" : "behind")}, in {secs:F0} s.";
        }
        else
        {
            double diff = b.Channel.Loudness.Lufs - a.Channel.Loudness.Lufs;
            r = new(DateTime.UtcNow, Active, 0, 0, diff, secs, Scoring.Level(diff));
            msg = $"Score {r.Score}. B was {Math.Abs(diff):F1} LU {(diff > 0 ? "louder" : "quieter")}, in {secs:F0} s.";
        }
        store.AddDrill(r);
        Active = null;
        return msg;
    }
    public void Cancel() => Active = null;
}
