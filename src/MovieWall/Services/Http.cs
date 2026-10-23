using System.Net;
using System.Net.Http;

namespace MovieWall.Services;

/// <summary>全局共用一个 HttpClient（连接复用），避免每部电影重新握手。</summary>
public static class Http
{
    /// <summary>豆瓣会校验 UA，必须是完整浏览器标识，否则直接 403。</summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/121.0.0.0 Safari/537.36";

    public static HttpClient Client { get; } = Create();

    /// <summary>
    /// 访问国内站点（豆瓣图片、豆瓣接口）专用的客户端：强制直连、绝不过代理。
    /// 豆瓣对境外 IP 会静默返回空数据，用户开着 Clash 时如果走代理就会「看起来接口挂了」。
    /// </summary>
    public static HttpClient DomesticClient { get; } = Create(useProxy: false);

    private static HttpClient? _foreignClient;
    private static string _foreignProxy = "\u0000";

    /// <summary>
    /// 访问境外站点（TMDB、Bangumi 图床）用：用户填了代理就用代理，否则跟系统设置。
    ///
    /// 单独做一件事的原因：Clash 开 TUN 模式、或者没勾「系统代理」时，
    /// .NET 的 UseProxy=true 其实拿不到代理，TMDB 一律超时——
    /// 用户看到的就是「配了 Key 也用不了」。这里允许直接写 http://127.0.0.1:7890。
    /// </summary>
    public static HttpClient GetForeignClient()
    {
        var url = SettingsStore.Instance.Current.ProxyUrl?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(url)) return Client;          // 没配就跟随系统

        if (_foreignClient is not null && string.Equals(url, _foreignProxy, StringComparison.OrdinalIgnoreCase))
            return _foreignClient;

        _foreignProxy = url;
        _foreignClient = Create(useProxy: false, proxyUrl: url);
        return _foreignClient;
    }

    private static HttpClient Create(bool useProxy = true, string? proxyUrl = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 6,
            // 默认走系统代理：用户开着 Clash 之类的工具时，TMDB 才有机会连通
            UseProxy = useProxy
        };

        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxyUrl.Trim());
        }

        var c = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        c.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return c;
    }
}

/// <summary>统一搜索结果，屏蔽 TMDB / 豆瓣的差异。</summary>
public sealed record MovieMeta(
    string Title,
    string? OriginalTitle,
    int Year,
    string? Overview,
    double Rating,
    int? RatingCount,
    int? RuntimeMinutes,
    string? Genres,
    string? Director,
    string? Actors,
    string? Countries,
    string? ReleaseDate,
    string? PosterUrl,
    string? BackdropUrl,
    string? TmdbId,
    string? DoubanId,
    string? ImdbId,
    string Source,
    string? ImageReferer = null);
