using Microsoft.Data.Sqlite;
using MovieWall.Models;

namespace MovieWall.Services;

/// <summary>
/// SQLite 库。所有路径、元数据、技术参数都在本地。
/// 表结构用「缺哪列补哪列」的方式演进，升级不会丢已有数据。
/// </summary>
public sealed class LibraryDatabase
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static string ConnStr => new SqliteConnectionStringBuilder
    {
        DataSource = Paths.DbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = true
    }.ToString();

    /// <summary>v2 新增的列（老的 v1 库里没有，要动态补上）。</summary>
    private static readonly (string name, string type)[] ExtraColumns =
    {
        ("VolumeLabel", "TEXT NOT NULL DEFAULT ''"),
        ("IsRemovable", "INTEGER NOT NULL DEFAULT 0"),
        ("RatingCount", "INTEGER NOT NULL DEFAULT 0"),
        ("Director", "TEXT"),
        ("Actors", "TEXT"),
        ("Countries", "TEXT"),
        ("ReleaseDate", "TEXT"),
        ("DoubanId", "TEXT"),
        ("ImdbId", "TEXT"),
        ("IsFavorite", "INTEGER NOT NULL DEFAULT 0"),
        ("ProbeJson", "TEXT"),
        ("ProbeDone", "INTEGER NOT NULL DEFAULT 0")
    };

    public async Task InitializeAsync()
    {
        Paths.EnsureAll();
        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync().ConfigureAwait(false);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;

                CREATE TABLE IF NOT EXISTS Movies (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    FilePath        TEXT    NOT NULL UNIQUE,
                    FileSize        INTEGER NOT NULL DEFAULT 0,
                    FileName        TEXT    NOT NULL DEFAULT '',
                    LastWriteTicks  INTEGER NOT NULL DEFAULT 0,
                    VolumeRoot      TEXT    NOT NULL DEFAULT '',
                    Title           TEXT    NOT NULL DEFAULT '',
                    OriginalTitle   TEXT    NOT NULL DEFAULT '',
                    Year            INTEGER NOT NULL DEFAULT 0,
                    Overview        TEXT,
                    Rating          REAL    NOT NULL DEFAULT 0,
                    Genres          TEXT,
                    TmdbId          TEXT,
                    RuntimeMinutes  INTEGER,
                    DurationMs      INTEGER,
                    VideoWidth      INTEGER,
                    VideoHeight     INTEGER,
                    BitrateBps      INTEGER,
                    VideoCodec      TEXT,
                    AudioCodec      TEXT,
                    PosterFile      TEXT,
                    BackdropFile    TEXT,
                    PosterOrigin    TEXT,
                    ScrapeState     INTEGER NOT NULL DEFAULT 0,
                    IsAvailable     INTEGER NOT NULL DEFAULT 1,
                    LastSeenTicks   INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS IX_Movies_Volume ON Movies(VolumeRoot);
                CREATE INDEX IF NOT EXISTS IX_Movies_Scrape ON Movies(ScrapeState);
                """;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // 老库补列
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Movies)";
            await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await r.ReadAsync().ConfigureAwait(false)) existing.Add(r.GetString(1));
        }

        foreach (var (name, type) in ExtraColumns)
        {
            if (existing.Contains(name)) continue;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE Movies ADD COLUMN {name} {type}";
            try
            {
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                Log.Write($"db: added column {name}");
            }
            catch (Exception ex) { Log.Write($"db: add column {name} failed: {ex.Message}"); }
        }
    }

    // ---------------- 读 ----------------

    public async Task<List<Movie>> LoadAllAsync()
    {
        var list = new List<Movie>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnStr);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Movies ORDER BY Title COLLATE NOCASE";
            await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await r.ReadAsync().ConfigureAwait(false))
            {
                var m = new Movie
                {
                    Id = r.GetInt64(r.GetOrdinal("Id")),
                    FilePath = S(r, "FilePath"),
                    FileSize = r.GetInt64(r.GetOrdinal("FileSize")),
                    FileName = S(r, "FileName"),
                    LastWriteTicks = r.GetInt64(r.GetOrdinal("LastWriteTicks")),
                    VolumeRoot = S(r, "VolumeRoot"),
                    Title = S(r, "Title"),
                    OriginalTitle = S(r, "OriginalTitle"),
                    Year = r.GetInt32(r.GetOrdinal("Year")),
                    Overview = SN(r, "Overview"),
                    Rating = r.GetDouble(r.GetOrdinal("Rating")),
                    Genres = SN(r, "Genres"),
                    TmdbId = SN(r, "TmdbId"),
                    RuntimeMinutes = IN(r, "RuntimeMinutes"),
                    DurationMs = LN(r, "DurationMs"),
                    VideoWidth = IN(r, "VideoWidth"),
                    VideoHeight = IN(r, "VideoHeight"),
                    BitrateBps = LN(r, "BitrateBps"),
                    VideoCodec = SN(r, "VideoCodec"),
                    AudioCodec = SN(r, "AudioCodec"),
                    PosterFile = SN(r, "PosterFile"),
                    BackdropFile = SN(r, "BackdropFile"),
                    PosterOrigin = SN(r, "PosterOrigin"),
                    ScrapeState = (ScrapeState)r.GetInt32(r.GetOrdinal("ScrapeState")),
                    IsAvailable = r.GetInt32(r.GetOrdinal("IsAvailable")) != 0,
                    LastSeenUtc = new DateTime(r.GetInt64(r.GetOrdinal("LastSeenTicks")), DateTimeKind.Utc),

                    // v2
                    VolumeLabel = S(r, "VolumeLabel"),
                    IsRemovable = r.GetInt32(r.GetOrdinal("IsRemovable")) != 0,
                    RatingCount = r.GetInt32(r.GetOrdinal("RatingCount")),
                    Director = SN(r, "Director"),
                    Actors = SN(r, "Actors"),
                    Countries = SN(r, "Countries"),
                    ReleaseDate = SN(r, "ReleaseDate"),
                    DoubanId = SN(r, "DoubanId"),
                    ImdbId = SN(r, "ImdbId"),
                    IsFavorite = r.GetInt32(r.GetOrdinal("IsFavorite")) != 0,
                    ProbeJson = SN(r, "ProbeJson"),
                    ProbeDone = r.GetInt32(r.GetOrdinal("ProbeDone")) != 0
                };

                list.Add(m);
            }
        }
        finally { _gate.Release(); }
        return list;
    }

    // ---------------- 写 ----------------

    public async Task<long> UpsertAsync(Movie m)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnStr);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Movies
                    (FilePath, FileSize, FileName, LastWriteTicks, VolumeRoot, VolumeLabel, IsRemovable,
                     Title, OriginalTitle, Year, Overview, Rating, RatingCount, Genres, TmdbId, DoubanId, ImdbId,
                     Director, Actors, Countries, ReleaseDate,
                     RuntimeMinutes, DurationMs, VideoWidth, VideoHeight, BitrateBps, VideoCodec, AudioCodec,
                     PosterFile, BackdropFile, PosterOrigin, ScrapeState, IsAvailable, IsFavorite,
                     LastSeenTicks, ProbeJson, ProbeDone)
                VALUES
                    ($fp, $fs, $fn, $lw, $vr, $vl, $rem,
                     $ti, $ot, $yr, $ov, $ra, $rc, $ge, $tm, $db, $im,
                     $di, $ac, $co, $rd,
                     $rm, $dm, $vw, $vh, $br, $vc, $auc,
                     $pf, $bf, $po, $ss, $av, $fav,
                     $ls, $pj, $pd)
                ON CONFLICT(FilePath) DO UPDATE SET
                    FileSize=$fs, FileName=$fn, LastWriteTicks=$lw, VolumeRoot=$vr,
                    VolumeLabel=$vl, IsRemovable=$rem,
                    Title=$ti, OriginalTitle=$ot, Year=$yr, Overview=$ov, Rating=$ra, RatingCount=$rc,
                    Genres=$ge, TmdbId=$tm, DoubanId=$db, ImdbId=$im,
                    Director=$di, Actors=$ac, Countries=$co, ReleaseDate=$rd,
                    RuntimeMinutes=$rm, DurationMs=$dm, VideoWidth=$vw, VideoHeight=$vh,
                    BitrateBps=$br, VideoCodec=$vc, AudioCodec=$auc,
                    PosterFile=CASE WHEN $pf IS NULL AND PosterFile IS NOT NULL THEN PosterFile ELSE $pf END,
                    BackdropFile=CASE WHEN $bf IS NULL AND BackdropFile IS NOT NULL THEN BackdropFile ELSE $bf END,
                    PosterOrigin=$po,
                    ScrapeState=$ss, IsAvailable=$av, IsFavorite=$fav,
                    LastSeenTicks=$ls, ProbeJson=$pj, ProbeDone=$pd
                RETURNING Id;
                """;

            void P(string n, object? v) => cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);

            P("$fp", m.FilePath);
            P("$fs", m.FileSize);
            P("$fn", m.FileName);
            P("$lw", m.LastWriteTicks);
            P("$vr", m.VolumeRoot);
            P("$vl", m.VolumeLabel ?? "");
            P("$rem", m.IsRemovable ? 1 : 0);
            P("$ti", m.Title ?? "");
            P("$ot", m.OriginalTitle ?? "");
            P("$yr", m.Year);
            P("$ov", m.Overview);
            P("$ra", m.Rating);
            P("$rc", m.RatingCount);
            P("$ge", m.Genres);
            P("$tm", m.TmdbId);
            P("$db", m.DoubanId);
            P("$im", m.ImdbId);
            P("$di", m.Director);
            P("$ac", m.Actors);
            P("$co", m.Countries);
            P("$rd", m.ReleaseDate);
            P("$rm", m.RuntimeMinutes);
            P("$dm", m.DurationMs);
            P("$vw", m.VideoWidth);
            P("$vh", m.VideoHeight);
            P("$br", m.BitrateBps);
            P("$vc", m.VideoCodec);
            P("$auc", m.AudioCodec);
            P("$pf", m.PosterFile);
            P("$bf", m.BackdropFile);
            P("$po", m.PosterOrigin);
            P("$ss", (int)m.ScrapeState);
            P("$av", m.IsAvailable ? 1 : 0);
            P("$fav", m.IsFavorite ? 1 : 0);
            P("$ls", m.LastSeenUtc.Ticks);
            P("$pj", m.ProbeJson);
            P("$pd", m.ProbeDone ? 1 : 0);

            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
            m.Id = id;
            return id;
        }
        finally { _gate.Release(); }
    }

    public async Task SetFavoriteAsync(long id, bool fav)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnStr);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Movies SET IsFavorite=$f WHERE Id=$id";
            cmd.Parameters.AddWithValue("$f", fav ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(long id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnStr);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Movies WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> SetAvailabilityAsync(string volumeRoot, bool available)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnStr);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Movies SET IsAvailable=$av WHERE VolumeRoot=$vr COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$av", available ? 1 : 0);
            cmd.Parameters.AddWithValue("$vr", volumeRoot);
            return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ResetScrapeAsync(IEnumerable<long> ids)
    {
        var arr = ids.ToArray();
        if (arr.Length == 0) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnStr);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Movies SET ScrapeState=0 WHERE Id=$id";
            var p = cmd.CreateParameter();
            p.ParameterName = "$id";
            cmd.Parameters.Add(p);

            foreach (var id in arr)
            {
                p.Value = id;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await tx.CommitAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 用户手动删掉了 posters 目录里的图片（或点了「清空海报缓存」）之后，
    /// 把库里指向已不存在文件的引用抹掉，并把状态退回「待刮削」。
    ///
    /// 不做这一步的话库里会留下「有海报」的假象：卡片永远显示占位符，
    /// 队列又因为状态是 Success 不再重试，等于这张海报永久丢了。
    /// 只动 PosterFile / BackdropFile / ScrapeState 三列，绝不碰用户的视频文件。
    /// </summary>
    public async Task ClearPosterRefsAsync(IEnumerable<long> ids)
    {
        var arr = ids.Where(id => id > 0).ToArray();
        if (arr.Length == 0) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnStr);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE Movies SET PosterFile=NULL, BackdropFile=NULL, PosterOrigin=NULL, ScrapeState=0 WHERE Id=$id";
            var p = cmd.CreateParameter();
            p.ParameterName = "$id";
            cmd.Parameters.Add(p);

            foreach (var id in arr)
            {
                p.Value = id;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> CountAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnStr);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Movies";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }
        finally { _gate.Release(); }
    }

    // ---------------- 列读取辅助 ----------------

    private static string S(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? "" : r.GetString(i);
    }

    private static string? SN(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static int? IN(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    private static long? LN(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }
}
