using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace JellyDiscer.Core.Analysis;

public sealed class TrackData
{
    public BeatGrid? Grid { get; set; }
    public Waveform? Waveform { get; set; }
    public double Cue { get; set; }
    public double[] HotCues { get; set; } = [-1, -1, -1, -1];
}

public sealed record DrillResult(DateTime When, string Kind, double TempoErrorPct, double PhaseErrorMs, double LevelErrorDb, double Seconds, int Score);

public sealed class Store : IDisposable
{
    readonly SqliteConnection _db;
    readonly Lock _lock = new();
    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("create table if not exists track(id text primary key, bpm real, off real, conf real, cue real, hot text, wave blob)");
        Exec("create table if not exists drill(at text, kind text, tempo real, phase real, level real, secs real, score integer)");
    }
    void Exec(string sql, params (string, object?)[] ps)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in ps) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        c.ExecuteNonQuery();
    }
    public TrackData? Get(string id)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "select bpm,off,conf,cue,hot,wave from track where id=$id";
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return new TrackData
            {
                Grid = r.IsDBNull(0) ? null : new BeatGrid(r.GetDouble(0), r.GetDouble(1), r.GetDouble(2)),
                Cue = r.GetDouble(3),
                HotCues = JsonSerializer.Deserialize<double[]>(r.GetString(4))!,
                Waveform = new Waveform((byte[])r[5])
            };
        }
    }
    public void Save(string id, TrackData d)
    {
        lock (_lock)
            Exec("insert or replace into track values($id,$b,$o,$c,$cue,$h,$w)", ("$id", id), ("$b", d.Grid?.Bpm), ("$o", d.Grid?.Offset), ("$c", d.Grid?.Confidence),
                ("$cue", d.Cue), ("$h", JsonSerializer.Serialize(d.HotCues)), ("$w", d.Waveform?.Bins ?? []));
    }
    public Dictionary<string, BeatGrid> Grids()
    {
        lock (_lock)
        {
            var o = new Dictionary<string, BeatGrid>();
            using var c = _db.CreateCommand();
            c.CommandText = "select id,bpm,off,conf from track where bpm is not null";
            using var r = c.ExecuteReader();
            while (r.Read()) o[r.GetString(0)] = new(r.GetDouble(1), r.GetDouble(2), r.GetDouble(3));
            return o;
        }
    }
    public void AddDrill(DrillResult d)
    {
        lock (_lock)
            Exec("insert into drill values($a,$k,$t,$p,$l,$s,$sc)", ("$a", d.When.ToString("o")), ("$k", d.Kind), ("$t", d.TempoErrorPct), ("$p", d.PhaseErrorMs),
                ("$l", d.LevelErrorDb), ("$s", d.Seconds), ("$sc", d.Score));
    }
    public List<DrillResult> Drills(int max = 50)
    {
        lock (_lock)
        {
            var o = new List<DrillResult>();
            using var c = _db.CreateCommand();
            c.CommandText = "select * from drill order by at desc limit " + max;
            using var r = c.ExecuteReader();
            while (r.Read())
                o.Add(new(DateTime.Parse(r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetInt32(6)));
            return o;
        }
    }
    public void Dispose() => _db.Dispose();
}
