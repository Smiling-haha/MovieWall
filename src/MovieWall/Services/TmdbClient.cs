using System.Text.Json;
using MovieWall.Common;

namespace MovieWall.Services;

/// <summary>
/// TMDB 客户端。用 v3 API Key（注册后在 设置 → API 页拿到）。
/// 只把「片名 + 年份」发出去，不带任何路径信息。
/// </summary>
public sealed class TmdbClient
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SettingsStore.Instance.Current.TmdbApiKey);

    // ---------- 代理可用性探测 ----------
    // 填了代理但 Clash 没开的时候，每个请求都要等满超时（连 10s + 读 20s），
    // 几百部片子刮下来能拖到几个小时，用户看到的就是「配了 Key 反而卡死」。
    // 这里每 60 秒探一次代理端口，不通就在本轮直接放弃 TMDB，交给豆瓣，速度不受影响。
    private static DateTime _proxyProbeAt = DateTime.MinValue;
    private static bool _proxyAlive = true;

    private static async Task<bool> ProxyUsableAsync(CancellationToken ct, bool force = false)
    {
        var url = SettingsStore.Instance.Current.ProxyUrl?.Trim() ?? "";
        if (url.Length == 0) return true;   // 跟随系统代理，预判不了，直接试

        if (!force && DateTime.UtcNow - _proxyProbeAt < TimeSpan.FromSeconds(60)) return _proxyAlive;
        _proxyProbeAt = DateTime.UtcNow;

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Host.Length == 0)
            {
                // 格式写错就交给 HttpClient 自己去报错，不在这里装聪明
                _proxyAlive = true;
                return true;
            }

            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(u.Host, u.Port, cts.Token).ConfigureAwait(false);
            _proxyAlive = true;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;   // 是外部取消，不是代理挂了
            _proxyAlive = false;
        }
        catch
        {
            _proxyAlive = false;
        }

        if (!_proxyAlive) Log.Write($"tmdb: 代理 {url} 连不上，本轮跳过 TMDB（豆瓣照常）");
        return _proxyAlive;
    }

    public async Task<MovieMeta?> SearchAsync(string title, int year, CancellationToken ct)
    {
        var s = SettingsStore.Instance.Current;
        if (string.IsNullOrWhiteSpace(s.TmdbApiKey) || string.IsNullOrWhiteSpace(title)) return null;
        if (!await ProxyUsableAsync(ct).ConfigureAwait(false)) return null;

        var hit = await SearchOnceAsync(s, title, year, ct).ConfigureAwait(false);
        if (hit is null && year > 0)
            hit = await SearchOnceAsync(s, title, 0, ct).ConfigureAwait(false);

        if (hit is null) return null;

        var detail = await GetDetailAsync(s, hit.Value.id, ct).ConfigureAwait(false);

        var posterPath = detail?.posterPath ?? hit.Value.posterPath;
        var backdropPath = detail?.backdropPath ?? hit.Value.backdropPath;

        return new MovieMeta(
            Title: NonEmpty(detail?.title) ?? hit.Value.title ?? title,
            OriginalTitle: NonEmpty(hit.Value.originalTitle),
            Year: detail is { year: > 0 } ? detail.year : hit.Value.year,
            Overview: NonEmpty(detail?.overview) ?? NonEmpty(hit.Value.overview),
            Rating: detail is { rating: > 0 } ? detail.rating : hit.Value.rating,
            RatingCount: detail is { ratingCount: > 0 } ? detail.ratingCount : (int?)null,
            RuntimeMinutes: detail?.runtime,
            Genres: detail?.genres,
            Director: detail?.director,
            Actors: detail?.actors,
            Countries: detail?.countries,
            ReleaseDate: detail?.releaseDate,
            PosterUrl: BuildImageUrl(s.TmdbImageBase, "w500", posterPath),
            BackdropUrl: BuildImageUrl(s.TmdbImageBase, "w1280", backdropPath),
            TmdbId: hit.Value.id.ToString(),
            DoubanId: null,
            ImdbId: detail?.imdbId,
            Source: "tmdb");
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<(int id, string? title, string? originalTitle, int year, string? overview, double rating,
        string? posterPath, string? backdropPath)?> SearchOnceAsync(
        Models.AppSettings s, string title, int year, CancellationToken ct)
    {
        var url = $"{s.TmdbApiBase}/3/search/movie" +
                  $"?api_key={Uri.EscapeDataString(s.TmdbApiKey)}" +
                  $"&query={Uri.EscapeDataString(title)}" +
                  $"&language={Uri.EscapeDataString(s.Language)}" +
                  "&include_adult=false&page=1";
        if (year > 0) url += $"&year={year}";

        try
        {
            using var resp = await Http.GetForeignClient().GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return null;

            (int id, string? title, string? originalTitle, int year, string? overview, double rating,
                string? posterPath, string? backdropPath)? best = null;
            double bestScore = 0.42;

            foreach (var r in results.EnumerateArray())
            {
                if (!r.TryGetProperty("id", out var idEl)) continue;
                var id = idEl.GetInt32();

                var t = Str(r, "title");
                var orig = Str(r, "original_title");
                var ry = ParseYear(Str(r, "release_date"));

                var score = Math.Max(
                    TextSimilarity.Score(t, ry, title, year),
                    TextSimilarity.Score(orig, ry, title, year));

                if (score <= bestScore) continue;
                bestScore = score;
                best = (id, t, orig, ry, Str(r, "overview"), Num(r, "vote_average"),
                    Str(r, "poster_path"), Str(r, "backdrop_path"));
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private sealed record TmdbDetail(
        string? title, int year, string? overview, double rating, int? ratingCount,
        int? runtime, string? genres, string? director, string? actors, string? countries,
        string? releaseDate, string? imdbId, string? posterPath, string? backdropPath);

    private async Task<TmdbDetail?> GetDetailAsync(Models.AppSettings s, int id, CancellationToken ct)
    {
        var url = $"{s.TmdbApiBase}/3/movie/{id}" +
                  $"?api_key={Uri.EscapeDataString(s.TmdbApiKey)}" +
                  $"&language={Uri.EscapeDataString(s.Language)}" +
                  "&append_to_response=credits";

        try
        {
            using var resp = await Http.GetForeignClient().GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            string? genres = null;
            if (root.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                var names = g.EnumerateArray().Select(x => Str(x, "name"))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (names.Count > 0) genres = string.Join(" / ", names!);
            }

            string? countries = null;
            if (root.TryGetProperty("production_countries", out var pc) && pc.ValueKind == JsonValueKind.Array)
            {
                var names = pc.EnumerateArray().Select(x => Str(x, "name"))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (names.Count > 0) countries = string.Join(" / ", names!);
            }

            string? director = null, actors = null, imdbId = null;
            if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
            {
                if (credits.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
                {
                    var directors = crew.EnumerateArray()
                        .Where(x => string.Equals(Str(x, "job"), "Director", StringComparison.OrdinalIgnoreCase))
                        .Select(x => Str(x, "name"))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Take(4).ToList();
                    if (directors.Count > 0) director = string.Join(" / ", directors!);
                }

                if (credits.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array)
                {
                    var acts = cast.EnumerateArray().Select(x => Str(x, "name"))
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Take(8).ToList();
                    if (acts.Count > 0) actors = string.Join(" / ", acts!);
                }
            }

            imdbId = Str(root, "imdb_id");

            int? runtime = null;
            if (root.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Number)
            {
                var v = rt.GetInt32();
                if (v > 0) runtime = v;
            }

            int ratingCount = 0;
            if (root.TryGetProperty("vote_count", out var vc) && vc.ValueKind == JsonValueKind.Number)
                ratingCount = vc.GetInt32();

            return new TmdbDetail(
                Str(root, "title"),
                ParseYear(Str(root, "release_date")),
                Str(root, "overview"),
                Num(root, "vote_average"),
                ratingCount,
                runtime, genres, director, actors, countries,
                Str(root, "release_date"), imdbId,
                Str(root, "poster_path"), Str(root, "backdrop_path"));
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildImageUrl(string imageBase, string size, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return $"{imageBase.TrimEnd('/')}/t/p/{size}{path}";
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static int ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4) return 0;
        return int.TryParse(date.AsSpan(0, 4), out var y) ? y : 0;
    }

    public async Task<(bool ok, string message)> TestAsync(CancellationToken ct)
    {
        var s = SettingsStore.Instance.Current;
        if (string.IsNullOrWhiteSpace(s.TmdbApiKey))
            return (false, "还没填 API Key");

        if (!await ProxyUsableAsync(ct, force: true).ConfigureAwait(false))
            return (false, $"代理 {s.ProxyUrl} 连不上（Clash 没开？）");

        try
        {
            var url = $"{s.TmdbApiBase}/3/configuration?api_key={Uri.EscapeDataString(s.TmdbApiKey)}";
            using var resp = await Http.GetForeignClient().GetAsync(url, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return (true, "TMDB 连接正常");
            if ((int)resp.StatusCode == 401) return (false, "API Key 无效（401）");
            return (false, $"TMDB 返回 {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, "连不上：" + ex.Message.Split('\n')[0]);
        }
    }
}
