namespace MovieWall.Models;

/// <summary>
/// 应用设置，序列化为 %LocalAppData%\MovieWall\settings.json。
/// 刻意保持扁平，方便用户直接手改。
/// </summary>
public sealed class AppSettings
{
    // ---------- 元数据源 ----------

    /// <summary>TMDB v3 API Key。留空则只用免 Key 的 iTunes 源。</summary>
    public string TmdbApiKey { get; set; } = "";

    /// <summary>TMDB API 域名。国内直连不通时可改成镜像/中转。</summary>
    public string TmdbApiBase { get; set; } = "https://api.themoviedb.org";

    /// <summary>TMDB 图片域名。可用 <c>https://static-mdb.v.geilijiasu.com</c> 之类的中转加速。</summary>
    public string TmdbImageBase { get; set; } = "https://image.tmdb.org";

    /// <summary>
    /// 访问境外站点（TMDB、Bangumi 图床）用的代理，例 <c>http://127.0.0.1:7890</c>。
    /// 留空 = 跟随系统设置。豆瓣始终直连，绝不走这里。
    ///
    /// 之所以要单独一项：Clash 开 TUN 或没开「系统代理」时，.NET 拿不到系统代理，
    /// TMDB 就一直是超时，看起来像「TMDB 用不了」。
    /// </summary>
    public string ProxyUrl { get; set; } = "";

    /// <summary>元数据语言。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>强制只用豆瓣，不走 TMDB（没配 Key 时本来也会自动跳过）。</summary>
    public bool PreferDoubanOnly { get; set; }

    /// <summary>
    /// 配好 TMDB 之后，是否已经给「历史失败条目」补过一次机会。
    /// 只补一次就置真：否则每次启动都把那几条翻回 Pending，永远重跑。
    /// </summary>
    public bool TmdbFailedRetryDone { get; set; }

    /// <summary>网络请求超时（秒）。</summary>
    public int HttpTimeoutSeconds { get; set; } = 15;

    // ---------- 播放 ----------

    /// <summary>指定播放器可执行文件。留空 = 用系统默认关联程序打开。</summary>
    public string PlayerPath { get; set; } = "";

    /// <summary>播放器显示名，按钮上会写成「用 XXX 播放」。</summary>
    public string PlayerName { get; set; } = "Yaoplayer";

    /// <summary>播放器参数模板，{file} 会被替换成带引号的完整路径。</summary>
    public string PlayerArgs { get; set; } = "{file}";

    // ---------- 扫描 ----------

    /// <summary>用户手动添加的扫描根目录。留空时自动扫描所有固定磁盘。</summary>
    public List<string> ScanRoots { get; set; } = new();

    /// <summary>启动时自动增量扫描。</summary>
    public bool AutoScanOnStart { get; set; } = true;

    /// <summary>自动扫描所有固定磁盘（不含 ScanRoots 里已列出的）。</summary>
    public bool AutoScanFixedDrives { get; set; } = true;

    /// <summary>监听 U 盘 / 移动硬盘插拔。</summary>
    public bool WatchRemovableDrives { get; set; } = true;

    /// <summary>固定磁盘上的最小计入体积（MB），过滤掉预告片、样片。</summary>
    public int MinFileSizeMb { get; set; } = 150;

    /// <summary>U 盘上的最小计入体积（MB），稍微放宽。</summary>
    public int MinRemovableFileSizeMb { get; set; } = 100;

    /// <summary>扫描时要跳过的目录名（小写匹配）。</summary>
    public List<string> ExcludedFolders { get; set; } = new()
    {
        "windows", "program files", "program files (x86)", "programdata",
        "$recycle.bin", "system volume information", "$windows.~bt", "$windows.~ws",
        "appdata", "node_modules", ".git", ".vs", "packages", "windowsapps",
        "recovery", "perflogs", "msocache", "config.msi", "intel", "amd"
    };

    /// <summary>
    /// 海报墙上只显示「像电影」的条目。默认开。
    /// 手机录像、分集切片、课程视频通常会占掉硬盘视频总数的八九成，
    /// 全铺上墙的结果是「一面墙两百多张卡，能刮到海报的只有个位数」。
    /// </summary>
    public bool ShowMoviesOnly { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
