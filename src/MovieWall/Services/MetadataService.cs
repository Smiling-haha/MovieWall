using System.IO;
using MovieWall.Models;

namespace MovieWall.Services;

/// <summary>
/// 刮削编排：TMDB（配了 Key 才有）优先 → 豆瓣免 Key 兜底 → 下载海报落盘 → 回填模型。
/// 全局并发上限 2，既不把源站打爆（豆瓣对频率敏感），也不至于几百部片子刮到天亮。
/// </summary>
public sealed class MetadataService
{
    private readonly TmdbClient _tmdb = new();
    private readonly DoubanClient _douban = new();

    // 原来这里是全局 1 并发（SemaphoreSlim(1,1)），所有条目不管走哪个源都排成一队。
    // 问题是：豆瓣确实必须串行（节流状态是全局的，IP 一被封全挂），
    // 但 TMDB 走代理、配额按请求数算，完全可以并行。
    // 拆成三个闸门后，配了 Key 的机器从「每条 2 秒排队」变成「3 条同时查」。
    private readonly SemaphoreSlim _tmdbGate = new(3, 3);
    private readonly SemaphoreSlim _doubanGate = new(1, 1);
    private readonly SemaphoreSlim _posterGate = new(2, 2);

    /// <summary>本次运行里每个文件「搜不到」的次数。到 2 次就判 Failed，不再反复打扰豆瓣。</summary>
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public TmdbClient Tmdb => _tmdb;
    public DoubanClient Douban => _douban;

    /// <summary>成功拿到元数据（可能没海报）。失败的置为 Failed，避免下次启动反复重试。</summary>
    public async Task<bool> EnrichAsync(Movie movie, CancellationToken ct)
    {
        if (movie.ScrapeState is ScrapeState.Success or ScrapeState.Ignored or ScrapeState.Skipped) return true;
        if (string.IsNullOrWhiteSpace(movie.DisplayTitle)) return false;

        if (MovieClassifier.LooksLikeNonFeature(movie))
        {
            movie.ScrapeState = ScrapeState.Skipped;
            Log.Write($"scrape «{movie.DisplayTitle}» -> SKIP（不是影片，不占配额）");
            return false;
        }

        try
        {
            var s = SettingsStore.Instance.Current;

            MovieMeta? meta = null;

            if (!s.PreferDoubanOnly && _tmdb.IsConfigured)
            {
                await _tmdbGate.WaitAsync(ct).ConfigureAwait(false);
                try { meta = await _tmdb.SearchAsync(movie.DisplayTitle, movie.Year, ct).ConfigureAwait(false); }
                finally { _tmdbGate.Release(); }
            }

            // 豆瓣是「全机共用一个 IP 配额」的资源：冷却期内再去问，只会把冷却往上推一级。
            // 所以这里直接不问，条目留在 Pending 交给队列在冷却结束后重排；
            // 而 TMDB 命中的条目完全不受影响——这正是配了 Key 之后该有的行为。
            bool doubanSkipped = false;
            if (meta is null)
            {
                if (DoubanClient.IsThrottled)
                {
                    doubanSkipped = true;
                }
                else
                {
                    await _doubanGate.WaitAsync(ct).ConfigureAwait(false);
                    try { meta = await _douban.SearchAsync(movie.DisplayTitle, movie.Year, ct).ConfigureAwait(false); }
                    finally { _doubanGate.Release(); }
                }
            }

            if (meta is null)
            {
                // 这一轮压根没问豆瓣（正在冷却），所以「搜不到」的次数不能记——
                // 否则冷却期间排队进来的条目会凭白被判成 Failed，永远不再重试。
                if (doubanSkipped)
                {
                    movie.ScrapeState = ScrapeState.Pending;
                    Log.Write($"scrape «{movie.DisplayTitle}» -> 跳过（豆瓣冷却中，TMDB 也没有），稍后重排");
                    return false;
                }

                // 被限流就保留 Pending，下次启动/下次扫描还能再试；
                // 真的是「搜不到」才记 Failed，避免每次启动都拿同一批空结果空跑。
                //
                // 但有一类不能再给机会：名字一看就是录像/访谈/成人片的东西。
                // 它们本来就搜不到，如果因为「恰好赶上限流」被留在 Pending，
                // 每轮都会再问一次豆瓣，把配额全烧在注定没有结果的条目上——
                // 日志里那串「throttled, will retry」循环就是这么来的。
                // 同一个文件本次运行里已经空了 2 次，也就别再问了。
                var key = movie.FilePath;
                var tries = _attempts.TryGetValue(key, out var n) ? n + 1 : 1;
                _attempts[key] = tries;

                var junk = MovieClassifier.LooksLikeNonFeature(movie) || tries >= 2;
                movie.ScrapeState = DoubanClient.IsThrottled && !junk
                    ? ScrapeState.Pending
                    : ScrapeState.Failed;
                if (movie.ScrapeState == ScrapeState.Failed) _attempts.Remove(key);

                Log.Write($"scrape «{movie.DisplayTitle}»({movie.Year}) -> NO MATCH" +
                          (movie.ScrapeState == ScrapeState.Pending ? " (throttled, will retry)" : ""));
                return false;
            }

            _attempts.Remove(movie.FilePath);

            Log.Write($"scrape «{movie.DisplayTitle}» -> {meta.Source}: {meta.Title} ({meta.Year})");

            // ---- 下载图片（失败不影响元数据）----
            //
            // 用户自己放在片子旁边的 poster.jpg 优先：那是他挑的图，比网上的准，
            // 而且省一次下载。这种情况 PosterFile 是绝对路径。
            var hasLocalPoster = !string.IsNullOrWhiteSpace(movie.PosterFile)
                                 && Path.IsPathRooted(movie.PosterFile);

            if (!hasLocalPoster && !string.IsNullOrWhiteSpace(meta.PosterUrl))
            {
                await _posterGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var f = await FetchImageAsync(meta.PosterUrl!, meta.ImageReferer, meta.Source, ct)
                        .ConfigureAwait(false);
                    if (f is not null) movie.PosterFile = f;
                }
                finally { _posterGate.Release(); }
            }

            if (!string.IsNullOrWhiteSpace(meta.BackdropUrl))
            {
                await _posterGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var f = await FetchImageAsync(meta.BackdropUrl!, meta.ImageReferer, meta.Source, ct)
                        .ConfigureAwait(false);
                    if (f is not null) movie.BackdropFile = f;
                }
                finally { _posterGate.Release(); }
            }

            // ---- 回填元数据（绝不覆盖本地探测出的技术参数）----
            if (!string.IsNullOrWhiteSpace(meta.Title)) movie.Title = meta.Title;

            if (!string.IsNullOrWhiteSpace(meta.OriginalTitle)
                && !string.Equals(meta.OriginalTitle, movie.Title, StringComparison.OrdinalIgnoreCase))
                movie.OriginalTitle = meta.OriginalTitle!;

            if (meta.Year > 0) movie.Year = meta.Year;
            if (!string.IsNullOrWhiteSpace(meta.Overview)) movie.Overview = meta.Overview!.Trim();
            if (meta.Rating > 0) movie.Rating = Math.Round(meta.Rating, 1);
            if (meta.RatingCount is > 0) movie.RatingCount = meta.RatingCount.Value;
            if (!string.IsNullOrWhiteSpace(meta.Genres)) movie.Genres = meta.Genres;

            if (!string.IsNullOrWhiteSpace(meta.Director)) movie.Director = meta.Director;
            if (!string.IsNullOrWhiteSpace(meta.Actors)) movie.Actors = meta.Actors;
            if (!string.IsNullOrWhiteSpace(meta.Countries)) movie.Countries = meta.Countries;
            if (!string.IsNullOrWhiteSpace(meta.ReleaseDate)) movie.ReleaseDate = meta.ReleaseDate;
            if (!string.IsNullOrWhiteSpace(meta.ImdbId)) movie.ImdbId = meta.ImdbId;
            if (!string.IsNullOrWhiteSpace(meta.DoubanId)) movie.DoubanId = meta.DoubanId;

            if (meta.RuntimeMinutes is > 0 && movie.DurationMs is null) movie.RuntimeMinutes = meta.RuntimeMinutes;
            if (!string.IsNullOrWhiteSpace(meta.TmdbId)) movie.TmdbId = meta.TmdbId;

            movie.PosterOrigin = meta.Source;
            movie.ScrapeState = ScrapeState.Success;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"scrape «{movie.DisplayTitle}» EXCEPTION {ex.GetType().Name}: {ex.Message}");
            movie.ScrapeState = ScrapeState.Failed;
            return false;
        }
    }

    /// <summary>
    /// 按图床选通道：TMDB / 境外图床先走代理，豆瓣图先走直连。
    ///
    /// 原来是「直连 → 系统代理 → 代理」三连试，对 TMDB 的图来说前两步注定失败，
    /// 每条白白多耗几百毫秒到几秒（连接被重置/超时都算时间），
    /// 用户看到的就是「TMDB 加上了，海报反而下得慢」。
    /// </summary>
    private static async Task<string?> FetchImageAsync(
        string url, string? referer, string source, CancellationToken ct)
    {
        bool foreign = string.Equals(source, "tmdb", StringComparison.OrdinalIgnoreCase)
                       || url.Contains("tmdb.org", StringComparison.OrdinalIgnoreCase)
                       || url.Contains("fanart.tv", StringComparison.OrdinalIgnoreCase)
                       || url.Contains("bangumi", StringComparison.OrdinalIgnoreCase);

        var order = foreign
            ? new[] { Http.GetForeignClient(), Http.Client, Http.DomesticClient }
            : new[] { Http.DomesticClient, Http.Client, Http.GetForeignClient() };

        foreach (var client in order)
        {
            var f = await PosterCache.SaveFromUrlAsync(client, url, ct, referer).ConfigureAwait(false);
            if (f is not null) return f;
        }

        return null;
    }

    public async Task<(bool tmdbOk, string tmdbMsg, bool doubanOk, string doubanMsg)> TestAllAsync(CancellationToken ct)
    {
        var t = await _tmdb.TestAsync(ct).ConfigureAwait(false);
        var d = await _douban.TestAsync(ct).ConfigureAwait(false);
        return (t.ok, t.message, d.ok, d.message);
    }
}
