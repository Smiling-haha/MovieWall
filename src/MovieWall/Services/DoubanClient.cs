using System.IO;
using System.Net.Http;
using System.Text.Json;
using MovieWall.Common;

namespace MovieWall.Services;

/// <summary>
/// 豆瓣数据源。TMDB 的免 Key 兜底，中文片名匹配最准。
///
/// 两个接口：
///  1) /j/subject_suggest?q=xxx  —— 搜索，返回片名/原名/年份/海报/条目 id
///  2) m.douban.com/rexxar/api/v2/movie/{id} —— 详情，拿简介、评分、类型、片长、导演、主演、国家
///
/// 有防盗链：必须带浏览器 UA，图片还要带 movie.douban.com 的 Referer。
/// </summary>
public sealed class DoubanClient
{
    private const string SuggestReferer = "https://movie.douban.com/";
    private const string DetailReferer = "https://m.douban.com/";

    /// <summary>
    /// 豆瓣对搜索接口有频率限制：短时间连发不会报 403，而是静默返回空数组，
    /// 表现就是「前几部刮得到、后面全空」。所以强制串行 + 最小间隔。
    /// </summary>
    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static long _lastCallTicks;

    // 请求间隔按命中情况自适应：一直命中说明状态好，可以降下来提速；
    // 出现空结果就抬回去保守一点。
    //
    // 实测豆瓣的风控对「节奏」比对上量更敏感：固定 1 秒一发，十几发就被封；
    // 拉到 3 秒左右再叠一点随机抖动，能连着刮几十条不出事。
    private static int _gapMs = 2600;
    private const int GapFast = 2600;
    private const int GapSlow = 4500;

    private static int _consecutiveEmpty;
    private static long _coolDownUntilTicks;

    /// <summary>一批最多连刮多少条，到量主动休息。</summary>
    private const int BatchSize = 12;
    private static int _successInBatch;

    // 豆瓣对被判定异常的出口 IP 容忍度极低：实测连续 6~7 次请求就会静默回空。
    // 固定 60 秒冷却会让程序在「恢复→又被封」之间来回打转，所以改成指数退避。
    private static int _coolDownLevel;

    // 403 和「静默空结果」是两回事：空结果可能只是片名不对，403 则是这个出口 IP
    // 已经被明确拦住了。这种情况再等 60 秒毫无意义，直接长冷却，别拿探针去撞墙。
    //
    // 关键：这个时间必须跨进程有效。以前存在内存里，用户重启一次程序就等于
    // 「忘掉自己刚被封过」，立刻又去打，只会让封禁越来越重。现在落盘成绝对时间。
    private static long _bannedUntilUtc = LoadBannedUntilUtc();

    public static bool IsBanned => DateTime.UtcNow.Ticks < Interlocked.Read(ref _bannedUntilUtc);

    private static readonly string CooldownFile = Path.Combine(Paths.Root, "douban-cooldown.txt");

    private static long LoadBannedUntilUtc()
    {
        try
        {
            if (!File.Exists(CooldownFile)) return 0;
            return long.TryParse(File.ReadAllText(CooldownFile).Trim(), out var t) ? t : 0;
        }
        catch { return 0; }
    }

    private static void SaveBannedUntilUtc(long ticks)
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            File.WriteAllText(CooldownFile, ticks.ToString());
        }
        catch { }
    }

    public static bool IsThrottled =>
        Environment.TickCount64 < Interlocked.Read(ref _coolDownUntilTicks) || IsBanned;

    private static void NoteForbidden()
    {
        if (IsBanned) return;

        var until = DateTime.UtcNow.AddMinutes(20).Ticks;
        Interlocked.Exchange(ref _bannedUntilUtc, until);
        SaveBannedUntilUtc(until);
        Log.Write("douban: 搜索接口返回 403，出口 IP 被拦，暂停 20 分钟再试（重启也不会忘）");
    }

    // 豆瓣对匿名 IP 的判定还有一种更阴的形态：HTTP 200，但页面/接口里只有一句
    // 「检测到有异常请求从您的IP发出，请登录再试!」。这时所有入口（搜索页、j/ 接口、
    // subject_suggest）会同时变空，跟「这部片子它没有」长得一模一样。
    //
    // 关键是：这种状态下再怎么放慢节奏也没用，只能整段停手。以前把它当普通空结果，
    // 于是「空 → 冷却 3 分钟 → 再空 → 冷却 6 分钟」循环几十轮，越试封得越久。
    private static int _ipBlockLevel;

    /// <summary>确认本机 IP 被豆瓣拉黑：整段停手，而且逐次加长。</summary>
    private static void NoteIpBlocked(string where)
    {
        var level = Interlocked.Increment(ref _ipBlockLevel);

        // 阶梯别再设那么狠。实测「异常请求」页经常只是几秒钟的偶发限流：
        // 之前第一档就 45 分钟、第三档 180 分钟，结果豆瓣早就恢复了，
        // 程序还自己闷着头停了三个小时，用户看到的就是「一直不加载海报」。
        // 现在 8 → 16 → 32 → 60（封顶），每档到期后随手发一个请求就能知道到底解没解。
        var minutes = level switch
        {
            1 => 8,
            2 => 16,
            3 => 32,
            _ => 60
        };

        var until = DateTime.UtcNow.AddMinutes(minutes).Ticks;
        var current = Interlocked.Read(ref _bannedUntilUtc);
        if (until <= current) return;      // 已经在更长的封禁里，别缩短

        Interlocked.Exchange(ref _bannedUntilUtc, until);
        SaveBannedUntilUtc(until);
        Log.Write($"douban: {where} 判定本机 IP 异常，暂停 {minutes} 分钟（第 {level} 次）。这段时间不再发任何请求");
    }

    /// <summary>还要等多少毫秒才解除冷却。</summary>
    public static int ThrottleRemainingMs
    {
        get
        {
            // 两个时钟不同源：站内冷却用 TickCount64（进程内），封禁用 UTC 绝对时间（跨进程）
            var cool = Interlocked.Read(ref _coolDownUntilTicks) - Environment.TickCount64;
            var ban = (Interlocked.Read(ref _bannedUntilUtc) - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
            var left = Math.Max(cool, ban);
            return left > 0 ? (int)Math.Min(left, int.MaxValue) : 0;
        }
    }

    private static readonly string[] LeadingArticles = { "The ", "A ", "An " };

    // ---------------- 节流 ----------------

    private static async Task<T> ThrottledAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await Throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = Interlocked.Read(ref _coolDownUntilTicks) - Environment.TickCount64;
            while (wait > 0)
            {
                await Task.Delay((int)Math.Min(wait, 5000), ct).ConfigureAwait(false);
                wait = Interlocked.Read(ref _coolDownUntilTicks) - Environment.TickCount64;
            }

            var gap = Volatile.Read(ref _gapMs) + Random.Shared.Next(0, 900);
            var since = Environment.TickCount64 - Interlocked.Read(ref _lastCallTicks);
            if (since < gap)
                await Task.Delay((int)(gap - since), ct).ConfigureAwait(false);

            return await action().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _lastCallTicks, Environment.TickCount64);
            Throttle.Release();
        }
    }

    private static void NoteResult(int count)
    {
        if (count > 0)
        {
            Interlocked.Exchange(ref _consecutiveEmpty, 0);
            Interlocked.Exchange(ref _coolDownLevel, 0);
            Interlocked.Exchange(ref _ipBlockLevel, 0);   // 能搜到就说明 IP 已经解封
            Volatile.Write(ref _gapMs, GapFast);

            // 豆瓣除了「每秒几次」还管「一次连续打多少」。实测不管间隔拉到 1 秒还是
            // 2.6 秒，连打 10 次上下就开始返空，跟你多慢没关系——总量到了。
            // 与其被它硬封 20 分钟，不如自己数着：连中 8 条就主动歇 4 分钟。
            // 对用户来说，墙是「一批一批长出海报」，而不是刮了十几张然后整晚卡死。
            var streak = Interlocked.Increment(ref _successInBatch);
            if (streak >= BatchSize)
            {
                Interlocked.Exchange(ref _successInBatch, 0);
                Interlocked.Exchange(ref _coolDownUntilTicks, Environment.TickCount64 + 3 * 60_000);
                Log.Write($"douban: 已连刮 {BatchSize} 条，主动休息 3 分钟再继续（比被硬封 20 分钟划算）");
            }
            return;
        }

        Interlocked.Increment(ref _consecutiveEmpty);
        Volatile.Write(ref _gapMs, GapSlow);
    }

    /// <summary>
    /// 真正的限流判定。
    ///
    /// 豆瓣限流时不是报 403，而是静默返回空数组，和「这个名字它本来就没有」长得一模一样。
    /// 而普通人的硬盘里大半是宣传片、录制、剧集编号、花絮，这些东西必然搜不到——
    /// 早期版本「连续 4 次空就冷却 60 秒」，结果整库 200 多部被那堆片花反复打进冷却，
    /// 真正的大片永远排不上队，最后一张海报都没刮到。
    ///
    /// 现在改成探针：连空 4 次时拿一部一定存在的片名试一次。
    /// 探针命中 = 网络没问题，只是这批文件名豆瓣没有，继续往下走；
    /// 探针也空 = 确认限流，才冷却。
    /// </summary>
    private async Task EnsureNotThrottledAsync(CancellationToken ct)
    {
        if (IsThrottled) return;
        if (Volatile.Read(ref _consecutiveEmpty) < 3) return;

        Interlocked.Exchange(ref _consecutiveEmpty, 0);

        bool probeHit;
        try
        {
            var probe = await ThrottledAsync(
                () => SuggestCoreAsync("阿甘正传", ct), ct).ConfigureAwait(false);
            probeHit = probe.Count > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 探针自己出错时不要误伤，按「正常」处理，让业务逻辑继续判断
            return;
        }

        if (!probeHit)
        {
            int level = Interlocked.Increment(ref _coolDownLevel);
            // 3 → 6 → 12 → 20 分钟。原来写的是 1/2/4/10 分钟，
            // 但实测 1 分钟根本不够——豆瓣这个档位一般要 5 分钟以上才松，
            // 等 1 分钟回来只是再空一轮，白白把队列刷成 Failed。
            int waitMs = level switch
            {
                1 => 3 * 60_000,
                2 => 6 * 60_000,
                3 => 12 * 60_000,
                _ => 20 * 60_000
            };

            Interlocked.Exchange(ref _coolDownUntilTicks, Environment.TickCount64 + waitMs);
            Log.Write($"douban: 探针也搜不到，确认限流，冷却 {waitMs / 1000} 秒（第 {level} 级）");

            // 连探针（阿甘正传，一定有）都空，基本就是 IP 被拉黑了。
            // 这时候再每 20 分钟回来撞一次只会延长封禁，直接升级成小时级停手。
            if (level >= 2) NoteIpBlocked("探针");
        }
        else
        {
            Log.Write("douban: 连续空但探针正常 —— 这些文件名豆瓣里没有，继续");
        }
    }

    // ---------------- 搜索 ----------------

    public async Task<MovieMeta?> SearchAsync(string title, int year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        // 中文资源常见「中文名.英文名.年份」的命名，解析出来是一串混合标题，
        // 直接拿去搜两边的库都匹配不上。所以拆成候选逐个试，中文优先（豆瓣中文最准）。
        var queries = new List<string>();
        void Add(string? q)
        {
            if (string.IsNullOrWhiteSpace(q)) return;
            q = q.Trim();
            if (q.Length == 0) return;
            if (queries.Any(x => string.Equals(x, q, StringComparison.OrdinalIgnoreCase))) return;
            queries.Add(q);
        }

        var cjk = ExtractCjk(title);
        var latin = ExtractLatin(title);

        // 纯数字不是「英文名」。ExtractLatin 会把「雷神3」的 3 也留下，
        // 于是 cjk=「雷神」、latin=「3」，被判成中英混合标题随手拆开搜 ——
        // 序号一丢，「雷神3」「钢铁侠2」「美国队长3」全部搜成系列第一部，
        // 海报挂的是别的片子。这里要求拉丁串里至少有一个字母。
        if (!latin.Any(char.IsLetter)) latin = "";

        // 中英混排的原串几乎不可能在任一库里命中，别浪费请求，也别让它污染限流判定
        bool mixed = !string.IsNullOrEmpty(cjk) && !string.IsNullOrEmpty(latin)
                     && !string.Equals(cjk, title, StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(latin, title, StringComparison.OrdinalIgnoreCase);

        if (!mixed) Add(title);
        Add(cjk);
        Add(latin);

        foreach (var src in new[] { title, latin })
        {
            foreach (var a in LeadingArticles)
            {
                if (src.StartsWith(a, StringComparison.OrdinalIgnoreCase) && src.Length > a.Length)
                {
                    Add(src[a.Length..].Trim());
                    break;
                }
            }
        }

        foreach (var q in queries)
        {
            if (IsBanned) break;
            if (IsThrottled) break;
            var meta = await TryQueryAsync(q, year, ct).ConfigureAwait(false);
            if (meta is not null) return meta;
        }

        return null;
    }

    /// <summary>抽出连续的中文段，其它字符当分隔。</summary>
    private static string ExtractCjk(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (IsCjk(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>抽出拉丁字母单词，中文整段当分隔。</summary>
    private static string ExtractLatin(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c < 128 ? c : ' ');
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static bool IsCjk(char c)
        => (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF);

    private async Task<MovieMeta?> TryQueryAsync(string title, int year, CancellationToken ct)
    {
        var candidates = await SuggestAsync(title, ct).ConfigureAwait(false);
        NoteResult(candidates.Count);

        if (candidates.Count == 0)
        {
            // 不在这里原地重试：真限流时重试同样为空，白白多花一次配额；
            // 是不是真限流交给探针判断。
            await EnsureNotThrottledAsync(ct).ConfigureAwait(false);
            Log.Write($"    douban: no suggest for «{title}»");
            return null;
        }

        SearchHit? best = null;
        double bestScore = 0.42;

        foreach (var c in candidates)
        {
            var score = Math.Max(
                TextSimilarity.Score(c.Title, c.Year, title, year),
                TextSimilarity.Score(c.Sub, c.Year, title, year));

            if (score <= bestScore) continue;
            bestScore = score;
            best = c;
        }

        if (candidates.Count > 0)
        {
            Log.Write($"    douban: {candidates.Count} 条候选 «{title}» -> " + string.Join("  |  ",
                candidates.Take(8).Select(c =>
                    $"{c.Title}({c.Year})={Math.Max(TextSimilarity.Score(c.Title, c.Year, title, year), TextSimilarity.Score(c.Sub, c.Year, title, year)):F2}")));
        }

        if (best is null)
        {
            Log.Write($"    douban: {candidates.Count} candidates, none matched «{title}»");
            return null;
        }

        // 详情接口只为了拿简介和片长。它挂了也不影响海报——搜索接口已经给过大图了。
        var detail = await GetDetailAsync(best.Id, ct).ConfigureAwait(false);

        return new MovieMeta(
            Title: NonEmpty(detail?.title) ?? best.Title,
            OriginalTitle: NonEmpty(detail?.originalTitle) ?? NonEmpty(best.Sub),
            Year: detail is { year: > 0 } ? detail.year : best.Year,
            Overview: NonEmpty(detail?.intro),
            Rating: detail is { rating: > 0 } ? detail.rating : best.Rating,
            RatingCount: detail is { ratingCount: > 0 }
                ? detail.ratingCount
                : (best.RatingCount > 0 ? best.RatingCount : null),
            RuntimeMinutes: detail?.runtime ?? best.Runtime,
            Genres: NonEmpty(detail?.genres) ?? best.Genres,
            Director: NonEmpty(detail?.directors) ?? best.Director,
            Actors: NonEmpty(detail?.actors) ?? best.Actors,
            Countries: NonEmpty(detail?.countries) ?? best.Countries,
            ReleaseDate: detail?.pubdate,
            PosterUrl: UpgradePoster(detail?.poster ?? best.Img),
            BackdropUrl: detail?.cover,
            TmdbId: null,
            DoubanId: best.Id,
            ImdbId: detail?.imdb,
            Source: "douban",
            ImageReferer: "https://movie.douban.com/");
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>s_ratio_poster / m_ratio_poster → l_ratio_poster，拿最大尺寸。</summary>
    private static string? UpgradePoster(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return url
            .Replace("/s_ratio_poster/", "/l_ratio_poster/")
            .Replace("/m_ratio_poster/", "/l_ratio_poster/");
    }

    // ---------------- suggest ----------------

    private Task<List<SearchHit>> SuggestAsync(
        string title, CancellationToken ct)
        => ThrottledAsync(() => SuggestCoreAsync(title, ct), ct);

    /// <summary>一条搜索结果。一次搜索就能给齐标题/年份/评分/海报/国家/类型/导演/主演。</summary>
    private sealed record SearchHit(
        string Id, string Title, string? Sub, int Year, string? Img,
        double Rating, int RatingCount,
        string? Genres, string? Countries, string? Director, string? Actors,
        int? Runtime);

    private async Task<List<SearchHit>> SuggestCoreAsync(string title, CancellationToken ct)
    {
        // 通道顺序按「实测可用性」排，不是按功能强弱排：
        //
        //   1) search.douban.com/movie/subject_search —— 完整搜索，结果以
        //      window.__DATA__ = {...} 内联在 HTML 里，一次请求就带海报大图。
        //      这是当前唯一既稳定、覆盖又全的入口（rexxar 接口对本机出口 IP 已 403）。
        //   2) movie.douban.com/j/subject_suggest —— 联想接口，只认精确前缀，
        //      但对「黑豹」「流浪地球2」这种正规中文名命中率很高，作为补刀。
        //   3) m.douban.com/rexxar/api/v2/search/subjects —— 曾经的主力，
        //      现在对部分出口 IP 返回 403，留着等它恢复。
        foreach (var client in PickClients())
        {
            var list = await FetchViaHtmlAsync(client, title, ct).ConfigureAwait(false);
            if (list.Count == 0)
                list = await FetchViaSuggestAsync(client, title, ct).ConfigureAwait(false);
            if (list.Count == 0)
                list = await FetchViaApiAsync(client, title, ct).ConfigureAwait(false);

            if (list.Count > 0)
            {
                RememberClient(client);
                return list;
            }
        }

        return new List<SearchHit>();
    }

    // ---------------- 通道 2：j/subject_suggest 联想 ----------------
    //
    // 返回一个纯 JSON 数组，每条形如：
    //   {"episode":"","img":"https://img3.doubanio.com/view/photo/s_ratio_poster/public/p451926968.jpg",
    //    "title":"黑客帝国","sub_title":"The Matrix","type":"movie","year":"1999","id":"1291843",
    //    "url":"https://movie.douban.com/subject/1291843/?suggest=matrix"}
    //
    // 它只做前缀联想，所以「雷神3」这种豆瓣正式名为「雷神3：诸神黄昏」的会搜不到；
    // 但对「黑豹」「流浪地球2」「哪吒之魔童闹海」这种正规中文名一击必中，
    // 而且这条线路对出口 IP 的要求比 rexxar 松得多，适合当补刀。
    private async Task<List<SearchHit>> FetchViaSuggestAsync(
        HttpClient client, string query, CancellationToken ct)
    {
        var list = new List<SearchHit>();
        var url = "https://movie.douban.com/j/subject_suggest?q=" + Uri.EscapeDataString(query);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(SuggestReferer);

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"    douban suggest http {(int)resp.StatusCode} for «{query}»");
                return list;
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!body.TrimStart().StartsWith('[')) return list;

            using var doc = JsonDocument.Parse(body);
            foreach (var it in doc.RootElement.EnumerateArray())
            {
                var type = Str(it, "type");
                // 只收电影；剧集/音乐/书籍在这里没有意义
                if (!string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase)) continue;

                var id = StrOrNum(it, "id");
                var name = Str(it, "title");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

                int year = 0;
                var ys = StrOrNum(it, "year");
                if (!string.IsNullOrWhiteSpace(ys)) int.TryParse(ys, out year);

                list.Add(new SearchHit(id!, name!, Str(it, "sub_title"), year, Str(it, "img"),
                    0, 0, null, null, null, null, null));

                if (list.Count >= 8) break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"    douban suggest ERROR {ex.GetType().Name}: {ex.Message}");
        }

        return list;
    }

    // ---------------- 通道 3：rexxar 搜索接口 ----------------

    private async Task<List<SearchHit>> FetchViaApiAsync(
        HttpClient client, string query, CancellationToken ct)
    {
        var list = new List<SearchHit>();

        // 这个接口对部分出口 IP 是长期 403（不是限流，是直接拒绝），
        // 每次还去打两遍纯属白送请求，只会让风控把整个 IP 记得更牢。
        // 连续两次 403 就本次运行彻底跳过。
        if (Volatile.Read(ref _apiDead) >= 2) return list;

        var url = "https://m.douban.com/rexxar/api/v2/search/subjects"
                  + $"?q={Uri.EscapeDataString(query)}&type=movie&count=6";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(DetailReferer);

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 这一路只是备用通道，403 不再升级成全局封禁——
                // 之前只要它 403 就把整个数据源停 20 分钟，结果主通道还能用也被一起停掉了。
                if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.TooManyRequests)
                    Interlocked.Increment(ref _apiDead);
                Log.Write($"    douban api search http {(int)resp.StatusCode} for «{query}»");
                return list;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("subjects", out var subjects)) return list;
            if (!subjects.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("target", out var t)) continue;

                var id = Str(t, "id");
                var name = Str(t, "title");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

                int y = 0;
                if (t.TryGetProperty("year", out var yv) && yv.ValueKind == JsonValueKind.Number)
                    y = yv.GetInt32();

                double rating = 0;
                int ratingCount = 0;
                if (t.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Object)
                {
                    if (r.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.Number)
                        rating = rv.GetDouble();
                    if (r.TryGetProperty("count", out var rc) && rc.ValueKind == JsonValueKind.Number)
                        ratingCount = rc.GetInt32();
                }

                // card_subtitle 形如：
                //   「美国 / 剧情 爱情 / 罗伯特·泽米吉斯 / 汤姆·汉克斯 罗宾·怀特」
                //   「国家 / 类型 / 导演 / 主演」，缺项时后面几段会缺席
                string? genres = null, countries = null, director = null, actors = null;
                var card = Str(t, "card_subtitle");
                if (!string.IsNullOrWhiteSpace(card))
                {
                    var parts = card!.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1) countries = parts[0];
                    if (parts.Length >= 2) genres = parts[1].Replace(' ', '/');
                    if (parts.Length >= 3) director = parts[2];
                    if (parts.Length >= 4) actors = parts[3].Replace(' ', '、');
                }

                list.Add(new SearchHit(id!, name!, null, y, Str(t, "cover_url"),
                    rating, ratingCount, genres, countries, director, actors, null));

                if (list.Count >= 6) break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"    douban api search ERROR {ex.GetType().Name}: {ex.Message}");
        }

        return list;
    }

    /// <summary>直连 / 系统代理两条通道。翻墙状态和出口 IP 都会影响豆瓣的判定，两条都留着。</summary>
    private static int _preferDomestic = 1;

    /// <summary>rexxar 搜索接口连续 403 的次数。到 2 就本次运行不再尝试。</summary>
    private static int _apiDead;

    private static HttpClient[] PickClients()
        => Volatile.Read(ref _preferDomestic) == 1
            ? new[] { Http.DomesticClient, Http.Client }
            : new[] { Http.Client, Http.DomesticClient };

    private static void RememberClient(HttpClient c)
        => Volatile.Write(ref _preferDomestic, ReferenceEquals(c, Http.DomesticClient) ? 1 : 0);

    // search.douban.com 把整页结果塞在一行 JS 里：
    //   window.__DATA__ = {"count":15,"error_info":"","items":[{...},{...}]}
    // items 每条（HTML 源码里中文是 \uXXXX 转义，JsonDocument 会自己还原）：
    //   abstract   "美国 / 动作 / 奇幻 / 冒险 / 雷神奇侠3：诸神黄昏(港) / 130分钟"
    //   abstract_2 "塔伊加·维迪提 / 克里斯·海姆斯沃斯 / 汤姆·希德勒斯顿 / ..."   ← 导演在第一位
    //   cover_url  https://img3.doubanio.com/view/photo/s_ratio_poster/public/p2493777430.jpg
    //   id         25821634
    //   rating     {"count":443727,"star_count":3.5,"value":7.4}
    //   title      "雷神3：诸神黄昏 Thor: Ragnarok‎ (2017)"
    //   url        https://movie.douban.com/subject/25821634/
    //
    // 一次搜索就同时拿到海报大图 + 类型/国家/导演/主演/片长，不用再打详情接口——
    // 请求量砍半是「不被豆瓣限流」的关键。
    //
    // 早期版本用正则在这段 HTML 里捞 `"cover_url"…"title"…"url"`，因为中间夹着
    // more_url / labels 这些长字段，1500 字符的窗口经常不够，于是「明明搜到了却解析出 0 条」，
    // 表现出来就是整面墙一张海报都没有。改成括号配平取完整 JSON 后解析，这个问题消失。
    private const string DataMarker = "window.__DATA__";

    private static async Task<List<SearchHit>> FetchViaHtmlAsync(
        HttpClient client, string query, CancellationToken ct)
    {
        var list = new List<SearchHit>();
        var url = $"https://search.douban.com/movie/subject_search?search_text={Uri.EscapeDataString(query)}&cat=1002";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(SuggestReferer);
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.TooManyRequests)
                    NoteForbidden();
                Log.Write($"    douban search http {(int)resp.StatusCode} for «{query}»");
                return list;
            }

            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 豆瓣的「IP 异常」页：HTTP 200、十几 KB、没有 __DATA__，只有一句提示。
            // 必须在这里认出来，否则会被当成「这部片搜不到」，然后带着整个队列空转。
            if (html.Contains("异常请求", StringComparison.Ordinal)
                || html.Contains("sec.douban.com", StringComparison.Ordinal))
            {
                NoteIpBlocked("搜索页");
                return list;
            }

            if (html.Length < 2000)
            {
                Log.Write($"    douban search 页面异常短（{html.Length}），可能被风控 «{query}»");
                return list;
            }

            var json = ExtractBalancedJson(html, DataMarker);
            if (json is null)
            {
                Log.Write($"    douban search 页面里没有 __DATA__ «{query}»");
                return list;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var it in items.EnumerateArray())
            {
                var id = StrOrNum(it, "id");
                var raw = Str(it, "title");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(raw)) continue;

                var (name, sub, year) = SplitSearchTitle(raw!);
                if (string.IsNullOrWhiteSpace(name)) continue;

                double rating = 0;
                int ratingCount = 0;
                if (it.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Object)
                {
                    if (r.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.Number)
                        rating = rv.GetDouble();
                    if (r.TryGetProperty("count", out var rc) && rc.ValueKind == JsonValueKind.Number)
                        ratingCount = rc.GetInt32();
                }

                // abstract  -> 国家 / 类型 / 类型 / 港台别名 / 130分钟
                // abstract_2 -> 导演 / 主演 / 主演 / …
                string? genres = null, countries = null;
                int? runtime = null;
                var abstractLine = Str(it, "abstract");
                if (!string.IsNullOrWhiteSpace(abstractLine))
                {
                    var parts = abstractLine!.Split('/',
                        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length >= 1) countries = parts[0];

                    var genreBits = new List<string>();
                    foreach (var p in parts.Skip(1))
                    {
                        // 「130分钟」「1小时50分钟」这种是片长，不是类型
                        var mins = ParseMinutes(p);
                        if (mins is > 0) { runtime = mins; continue; }

                        // 「雷神奇侠3：诸神黄昏(港)」这类港台译名带括号，跳过
                        if (p.Contains('(') || p.Contains('（')) continue;
                        if (p.Length is > 0 and <= 6) genreBits.Add(p);
                    }
                    if (genreBits.Count > 0) genres = string.Join(" / ", genreBits.Distinct());
                }

                string? director = null, actors = null;
                var castLine = Str(it, "abstract_2");
                if (!string.IsNullOrWhiteSpace(castLine))
                {
                    var people = castLine!.Split('/',
                        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (people.Length >= 1) director = people[0];
                    if (people.Length >= 2) actors = string.Join(" / ", people.Skip(1).Take(6));
                }

                list.Add(new SearchHit(id!, name, sub, year, Str(it, "cover_url"),
                    rating, ratingCount, genres, countries, director, actors, runtime));

                if (list.Count >= 8) break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"    douban search ERROR {ex.GetType().Name}: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// 从 HTML 里把 <c>marker = {...}</c> 的那段 JSON 原样切出来。
    /// 必须按括号配平 + 跳过字符串内的括号和转义，否则遇到
    /// 「片名里带括号」或 more_url 里的 } 就会切歪。
    /// </summary>
    private static string? ExtractBalancedJson(string html, string marker)
    {
        int at = html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;

        int eq = html.IndexOf('=', at + marker.Length);
        if (eq < 0) return null;

        int start = html.IndexOf('{', eq);
        if (start < 0) return null;

        int depth = 0;
        bool inString = false, escaped = false;

        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return html[start..(i + 1)];
            }
        }

        return null;
    }

    /// <summary>豆瓣 JSON 里 id 有时是数字有时是字符串，统一取成字符串。</summary>
    private static string? StrOrNum(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    /// <summary>豆瓣把「中文名 原名 (年份)」塞在一个字符串里，拆开才好算相似度。</summary>
    private static (string title, string? sub, int year) SplitSearchTitle(string raw)
    {
        var s = raw.Replace("\u200e", "").Replace("\u200f", "").Replace("\u00a0", " ").Trim();

        int year = 0;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"[(（]\s*(\d{4})\s*[)）]\s*$");
        if (m.Success)
        {
            int.TryParse(m.Groups[1].Value, out year);
            s = s[..m.Index].Trim();
        }

        // 第一个拉丁字母出现的位置就是中英文分界
        int firstLatin = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) { firstLatin = i; break; }
        }

        if (firstLatin > 0)
            return (s[..firstLatin].Trim(), s[firstLatin..].Trim(), year);

        return (s, null, year);
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;

        try { return JsonSerializer.Deserialize<string>("\"" + s + "\"") ?? s; }
        catch { return s; }
    }

    // ---------------- 详情 ----------------

    private sealed record DetailResult(
        string? title, string? originalTitle, int year, string? intro,
        double rating, int ratingCount, int? runtime, string? genres,
        string? directors, string? actors, string? countries, string? pubdate,
        string? poster, string? cover, string? imdb);

    private Task<DetailResult?> GetDetailAsync(string id, CancellationToken ct)
        => ThrottledAsync(() => GetDetailCoreAsync(id, ct), ct);

    // 详情接口连续失败多半是又被限流了。这时候继续硬打只会把配额烧光，
    // 连海报都存不下来——所以熔断掉详情，退回只用搜索页已经拿到的信息。
    private static int _detailFails;

    private async Task<DetailResult?> GetDetailCoreAsync(string id, CancellationToken ct)
    {
        if (Volatile.Read(ref _detailFails) >= 3) return null;

        var url = $"https://m.douban.com/rexxar/api/v2/movie/{id}";

        foreach (var client in PickClients())
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri(DetailReferer);

                using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var root = doc.RootElement;

                var parsed = ParseDetail(root);
                if (parsed is not null)
                {
                    Volatile.Write(ref _detailFails, 0);
                    RememberClient(client);
                }
                return parsed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Write($"    douban detail ERROR {ex.GetType().Name} for id={id}");
            }
        }

        var fails = Interlocked.Increment(ref _detailFails);
        if (fails == 3) Log.Write("douban: 详情接口连续失败，本次运行先只用搜索页数据（海报仍正常）");

        return null;
    }

    private static DetailResult? ParseDetail(JsonElement root)
    {
        {
            var genres = JoinArray(root, "genres", " / ");
            var countries = JoinArray(root, "countries", " / ");
            var directors = JoinNames(root, "directors", 6);
            var actors = JoinNames(root, "actors", 8);

            double rating = 0;
            int ratingCount = 0;
            if (root.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                if (r.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.Number) rating = rv.GetDouble();
                if (r.TryGetProperty("count", out var rc) && rc.ValueKind == JsonValueKind.Number) ratingCount = rc.GetInt32();
            }

            int? runtime = null;
            if (root.TryGetProperty("durations", out var d) && d.ValueKind == JsonValueKind.Array)
            {
                var first = d.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.String) runtime = ParseMinutes(first.GetString());
            }

            string? poster = null, cover = null;
            if (root.TryGetProperty("pic", out var pic) && pic.ValueKind == JsonValueKind.Object)
                poster = Str(pic, "large") ?? Str(pic, "normal");
            if (root.TryGetProperty("cover", out var c) && c.ValueKind == JsonValueKind.Object)
                cover = Str(c, "url");

            int year = 0;
            if (root.TryGetProperty("year", out var yv))
            {
                if (yv.ValueKind == JsonValueKind.Number) year = yv.GetInt32();
                else if (yv.ValueKind == JsonValueKind.String && int.TryParse(yv.GetString(), out var py)) year = py;
            }

            // 上映日期在不同条目上字段名不一致，多试几个
            string? pubdate = Str(root, "pubdate") ?? Str(root, "release_date");
            if (pubdate is null && root.TryGetProperty("pubdate", out var pd) && pd.ValueKind == JsonValueKind.Array)
            {
                var first = pd.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.String) pubdate = first.GetString();
            }

            string? imdb = Str(root, "imdb") ?? Str(root, "imdb_id");

            return new DetailResult(
                Str(root, "title"), Str(root, "original_title"), year, Str(root, "intro"),
                rating, ratingCount, runtime, genres,
                directors, actors, countries, pubdate,
                poster, cover, imdb);
        }
    }

    private static string? JoinArray(JsonElement root, string name, string sep)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var vals = arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        return vals.Count == 0 ? null : string.Join(sep, vals!);
    }

    private static string? JoinNames(JsonElement root, string name, int take)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var vals = arr.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.Object ? Str(x, "name") : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(take)
            .ToList();
        return vals.Count == 0 ? null : string.Join(" / ", vals!);
    }

    /// <summary>豆瓣片长是「136分钟」「2小时16分钟」这种中文串。</summary>
    private static int? ParseMinutes(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration)) return null;

        int total = 0;
        var m = System.Text.RegularExpressions.Regex.Match(duration, @"(\d+)\s*小时");
        if (m.Success) total += int.Parse(m.Groups[1].Value) * 60;

        m = System.Text.RegularExpressions.Regex.Match(duration, @"(\d+)\s*分钟");
        if (m.Success) total += int.Parse(m.Groups[1].Value);

        if (total == 0)
        {
            m = System.Text.RegularExpressions.Regex.Match(duration, @"^\s*(\d+)\s*$");
            if (m.Success) total = int.Parse(m.Groups[1].Value);
        }

        return total is > 0 and < 600 ? total : null;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async Task<(bool ok, string message)> TestAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://movie.douban.com/j/subject_suggest?q=" + Uri.EscapeDataString("matrix"));
            req.Headers.Referrer = new Uri(SuggestReferer);

            using var resp = await Http.Client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, $"豆瓣返回 {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var ok = body.Contains("\"type\":\"movie\"") || body.Contains("\"type\": \"movie\"");
            return ok ? (true, "豆瓣连接正常（免 Key 源可用）") : (false, "豆瓣有响应但没返回影片数据（可能正在限流）");
        }
        catch (Exception ex)
        {
            return (false, "连不上：" + ex.Message.Split('\n')[0]);
        }
    }
}
