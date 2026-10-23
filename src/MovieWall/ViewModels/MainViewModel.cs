using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MovieWall.Common;
using MovieWall.Models;
using MovieWall.Services;

namespace MovieWall.ViewModels;

public enum AppPage { Library, Detail, Spec }

public enum LibraryFilter { All, Local, External, Favorite }

public enum SortMode { Title, Year, Rating, Size, Recent }

/// <summary>左上角磁盘清单里的一枚 chip。</summary>
public sealed class DiskChip : ObservableObject
{
    public string Root { get; init; } = "";
    public string Letter { get; init; } = "";
    public string Label { get; init; } = "";

    private int _count;
    public int Count { get => _count; set { if (Set(ref _count, value)) { OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(Detail)); } } }

    private bool _online;
    public bool Online { get => _online; set { if (Set(ref _online, value)) { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(Detail)); } } }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public string CountText => _count.ToString();

    public string StateText => _online ? _count.ToString() : "已断开";

    /// <summary>chip 里那行小字：「E: 新加卷」或者「F: 已断开」。</summary>
    public string Title => string.IsNullOrWhiteSpace(Label) ? Letter : $"{Letter} {Label}";

    public string Detail => _online ? $"{_count}" : $"已断开 · {_count}";
}

/// <summary>
/// 主视图模型：库生命周期、扫描编排、磁盘分组、筛选、三页导航。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly LibraryDatabase _db = new();
    private readonly MetadataService _meta = new();
    private readonly LibraryScanner _scanner = new();
    private readonly EnrichmentQueue _enrich;

    private readonly ConcurrentDictionary<string, Movie> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Movie> _all = new();
    private readonly ConcurrentQueue<Movie> _incoming = new();
    private readonly ConcurrentDictionary<string, byte> _activeScans = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _statusTimer;

    private CancellationTokenSource? _scanCts;
    private bool _disposed;

    public MainViewModel()
    {
        _enrich = new EnrichmentQueue(_meta, _db);
        _enrich.Updated += _ => RunOnUi(RefreshStats);

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(160) };
        _flushTimer.Tick += (_, _) => FlushIncoming();

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(190) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastText = null; };

        // 顶部那行字里有「待补海报 N 部」和限流倒计时，靠它每秒刷一次才不会看起来卡死。
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => OnPropertyChanged(nameof(TopStatus));
        _statusTimer.Start();
    }

    // ---------------- 集合 ----------------

    public RangeObservableCollection<Movie> VisibleMovies { get; } = new();

    public ObservableCollection<DiskChip> Disks { get; } = new();

    // ---------------- 导航 ----------------

    private AppPage _page = AppPage.Library;
    public AppPage Page
    {
        get => _page;
        private set
        {
            if (!Set(ref _page, value)) return;
            OnPropertyChanged(nameof(IsLibraryPage));
            OnPropertyChanged(nameof(IsDetailPage));
            OnPropertyChanged(nameof(IsSpecPage));
        }
    }

    public bool IsLibraryPage => _page == AppPage.Library;
    public bool IsDetailPage => _page == AppPage.Detail;
    public bool IsSpecPage => _page == AppPage.Spec;

    private Movie? _currentMovie;
    public Movie? CurrentMovie
    {
        get => _currentMovie;
        private set => Set(ref _currentMovie, value);
    }

    public void OpenDetail(Movie movie)
    {
        CurrentMovie = movie;
        Page = AppPage.Detail;
        OnPropertyChanged(nameof(PlayerName));
    }

    public void OpenSpec()
    {
        if (_currentMovie is null) return;
        Page = AppPage.Spec;
    }

    public void BackToLibrary()
    {
        Page = AppPage.Library;
        CurrentMovie = null;
    }

    public void BackToDetail()
    {
        if (_currentMovie is null) { BackToLibrary(); return; }
        Page = AppPage.Detail;
    }

    // ---------------- 页头统计 ----------------

    private string _headerSummary = "";
    public string HeaderSummary { get => _headerSummary; private set => Set(ref _headerSummary, value); }

    private string _statusLine = "离线索引 · 只在本机";
    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }

    /// <summary>参与展示的条目（受「只看影片」过滤影响）。计数都基于它，不是原始文件数。</summary>
    private IEnumerable<Movie> Scope => MoviesOnly
        ? _all.Where(m => !MovieClassifier.IsDefinitelyNotMovie(m))
        : _all;

    public int AllCount => Scope.Count();
    public int LocalCount => Scope.Count(m => !m.IsRemovable);
    public int ExternalCount => Scope.Count(m => m.IsRemovable);
    public int FavoriteCount => Scope.Count(m => m.IsFavorite);

    public string AllCountText => AllCount.ToString();
    public string LocalCountText => LocalCount.ToString();
    public string ExternalCountText => ExternalCount.ToString();
    public string FavoriteCountText => FavoriteCount.ToString();

    // ---------------- 筛选 / 搜索 / 排序 ----------------

    private LibraryFilter _filter = LibraryFilter.All;
    public LibraryFilter Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            OnPropertyChanged(nameof(IsAllFilter));
            OnPropertyChanged(nameof(IsLocalFilter));
            OnPropertyChanged(nameof(IsExternalFilter));
            OnPropertyChanged(nameof(IsFavoriteFilter));
            ApplyFilter();
        }
    }

    public bool IsAllFilter => _filter == LibraryFilter.All;
    public bool IsLocalFilter => _filter == LibraryFilter.Local;
    public bool IsExternalFilter => _filter == LibraryFilter.External;
    public bool IsFavoriteFilter => _filter == LibraryFilter.Favorite;

    /// <summary>
    /// 只显示「像电影」的条目。默认开启：硬盘里手机录像、分集切片、课程视频
    /// 往往比电影多十倍，全铺在墙上既看不到海报也找不到片子。
    /// 关掉它就回到「所有视频文件都上墙」的旧行为。
    /// </summary>
    private bool _moviesOnly = true;
    public bool MoviesOnly
    {
        get => _moviesOnly;
        set
        {
            if (!Set(ref _moviesOnly, value)) return;
            SettingsStore.Instance.Current.ShowMoviesOnly = value;
            SettingsStore.Instance.Save();
            ApplyFilter();
            RefreshStats();
        }
    }

    private string? _diskFilter;
    /// <summary>点磁盘 chip 时按盘过滤，null = 不过滤。</summary>
    public string? DiskFilter
    {
        get => _diskFilter;
        set
        {
            if (!Set(ref _diskFilter, value)) return;
            foreach (var d in Disks)
                d.IsSelected = string.Equals(d.Root, value, StringComparison.OrdinalIgnoreCase);
            ApplyFilter();
        }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    private SortMode _sortMode = SortMode.Title;
    public SortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (!Set(ref _sortMode, value)) return;
            OnPropertyChanged(nameof(SortLabel));
            ApplyFilter();
        }
    }

    public string SortLabel => _sortMode switch
    {
        SortMode.Year => "年份",
        SortMode.Rating => "评分",
        SortMode.Size => "文件大小",
        SortMode.Recent => "最近添加",
        _ => "名称"
    };

    public string PlayerName => PlayerLauncher.CurrentPlayerName;

    /// <summary>顶栏中间那行状态。</summary>
    public string TopStatus
    {
        get
        {
            if (IsScanning) return "正在整理资料库…";

            // 墙上还空着的那些位置是「还在排队下载」，不是坏了。
            // 豆瓣一限流队列就会停住，所以把倒计时直接写在这儿，用户不用去翻日志。
            var missing = _all.Count(m => !m.HasPoster && !MovieClassifier.IsDefinitelyNotMovie(m));
            if (missing == 0) return $"资料库已就绪  ·  {AllCount} 部";

            var waitMs = DoubanClient.ThrottleRemainingMs;
            var tail = waitMs > 0
                ? $"  ·  豆瓣限流，{Math.Max(1, (waitMs + 59_999) / 60_000)} 分钟后继续"
                : "  ·  海报下载中";

            return $"资料库已就绪  ·  {AllCount} 部  ·  待补海报 {missing} 部{tail}";
        }
    }

    /// <summary>技术规格页右上角那个版本水印。</summary>
    public string MediaInfoLabel
    {
        get
        {
            if (!MediaProbe.Available) return "MEDIAINFO 未安装";
            var v = MediaProbe.ToolVersion.TrimStart('v', 'V').Trim();
            return string.IsNullOrEmpty(v) ? "MEDIAINFO" : $"MEDIAINFO {v}";
        }
    }

    public bool MediaProbeAvailable => MediaProbe.Available;

    // ---------------- 状态 ----------------

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(TopStatus));
            OnPropertyChanged(nameof(ScanButtonText));
        }
    }

    public bool IsEmpty => _all.Count == 0 && !IsScanning;

    /// <summary>扫描按钮的文案：扫描中变成「停止扫描」，避免用户以为点击没反应。</summary>
    public string ScanButtonText => IsScanning ? "停止扫描" : "重新扫描";

    private string? _toastText;
    public string? ToastText { get => _toastText; private set { if (Set(ref _toastText, value)) OnPropertyChanged(nameof(IsToastVisible)); } }
    public bool IsToastVisible => _toastText is not null;

    // ---------------- 初始化 ----------------

    public async Task InitializeAsync()
    {
        Paths.EnsureAll();

        // 上一次运行如果被豆瓣风控糊过一脸 HTML，posters 里就会躺着一批几十 KB、
        // 打不开的「假 jpg」。这些 HTML 页面本身带着真图地址，就地修好最划算——
        // 不用重新搜豆瓣，不占那点可怜的搜索配额，库里的引用也不用改。
        var (badCount, _) = PosterCache.ScanBrokenFiles();
        if (badCount > 0)
        {
            try
            {
                var (ok, fail) = await PosterCache
                    .RepairBrokenFilesAsync(() => Http.DomesticClient, CancellationToken.None)
                    .ConfigureAwait(true);
                Log.Write($"poster repair(启动): 坏图 {badCount} 张，修好 {ok}，放弃 {fail}");
            }
            catch (Exception ex) { Log.Write($"poster repair(启动) 失败 {ex.GetType().Name}: {ex.Message}"); }
        }

        MediaProbe.Initialize(AppDomain.CurrentDomain.BaseDirectory);
        OnPropertyChanged(nameof(MediaInfoLabel));
        OnPropertyChanged(nameof(MediaProbeAvailable));

        await _db.InitializeAsync().ConfigureAwait(true);

        _moviesOnly = SettingsStore.Instance.Current.ShowMoviesOnly;
        OnPropertyChanged(nameof(MoviesOnly));

        var loaded = await _db.LoadAllAsync().ConfigureAwait(true);

        foreach (var m in loaded)
        {
            // 把上次存下来的技术参数还原回内存对象
            if (!string.IsNullOrEmpty(m.ProbeJson))
            {
                try { m.Detail = JsonSerializer.Deserialize<MediaDetail>(m.ProbeJson); }
                catch { }
            }
            m.IsAvailable = m.FileExists;
        }

        _all.AddRange(loaded);
        foreach (var m in loaded) _index[m.FilePath] = m;

        // 旧版解析器会把发布信息一起吃进标题：
        //   「Captain America BD DD5 英国双语内封特效中英双字-FFansBD」
        //   「DBD-Raws][4K HDR][蜘蛛侠：英雄远征][IMAX版]…」
        // 这种名字拿去搜必然全空，所以这些片子就永远停在「没海报」。
        // 这里对「还没有海报、也没刮成功过」的条目用当前解析器重抠一次片名。
        var reparsed = FileNameParser.ReparseStale(loaded);
        if (reparsed > 0)
        {
            Log.Write($"reparse: {reparsed} 条标题已用新规则重新解析");
            // 只改了内存不够：不落库的话，下次启动又是脏标题（虽然会再算一遍，
            // 但中途崩溃 / 换版本时就会前功尽弃）。
            foreach (var m in FileNameParser.Touched)
            {
                try { await _db.UpsertAsync(m).ConfigureAwait(true); }
                catch (Exception ex) { Log.Write($"reparse 落库失败 {m.FileName}: {ex.Message}"); }
            }
        }

        // 海报图片可能被手动删掉了（用户清理磁盘，或点了「清空海报缓存」）。
        // 库里的引用还在，但文件不在了——这里先把这批修掉，它们会重新进队列下载。
        var repaired = await RepairMissingPostersAsync().ConfigureAwait(true);
        if (repaired > 0) ShowToast($"{repaired} 张海报文件已丢失，正在重新下载…");

        RebuildDisks();
        ApplyFilter();
        RefreshStats();

        _enrich.Start();
        _flushTimer.Start();

        // 顺序很关键：豆瓣对被封的出口 IP 容忍度很低，几百条里只要有一小部分能刮到，
        // 也得先给「最可能是电影」的那些，用户才会第一时间看到海报。
        // 这里三级排序：没海报的优先 > 真电影优先 > 有年份、时长正常的优先。
        // 没有这一层，队列会被手机录像和分集切片占满，真电影排在两百名之后。
        var backlog = loaded
            .Where(m => m.IsAvailable
                        && m.ScrapeState is not (ScrapeState.Skipped or ScrapeState.Ignored)
                        && (m.ScrapeState == ScrapeState.Pending || !m.ProbeDone))
            // 开了「只看影片」就不给非影片排队：豆瓣的请求额度很贵，
            // 两百条里有一百九十条是手机录像和切片，让它们排队等于把真电影饿死。
            .Where(m => !_moviesOnly || !MovieClassifier.LooksLikeNonFeature(m))
            .OrderBy(m => m.HasPoster ? 1 : 0)
            .ThenByDescending(m => MovieClassifier.ScrapePriority(m))
            .ThenByDescending(m => m.FileSize)
            .ToList();
        if (backlog.Count > 0) _enrich.EnqueueRange(backlog);

        // 刚配上 TMDB 时，把之前「豆瓣查无此片」的条目补一次机会：
        // 那批里最多的就是外语片——文件名是原名，豆瓣只有中文译名，怎么搜都对不上。
        // 只补一次（置 TmdbFailedRetryDone），否则每次启动都翻回 Pending 反复重跑。
        var st = SettingsStore.Instance.Current;
        if (!st.PreferDoubanOnly && !string.IsNullOrWhiteSpace(st.TmdbApiKey) && !st.TmdbFailedRetryDone)
        {
            var retry = loaded
                .Where(m => m.IsAvailable && m.ScrapeState == ScrapeState.Failed)
                .Where(m => !MovieClassifier.LooksLikeNonFeature(m))
                .ToList();

            st.TmdbFailedRetryDone = true;
            SettingsStore.Instance.Save();

            if (retry.Count > 0)
            {
                foreach (var m in retry) m.ScrapeState = ScrapeState.Pending;
                _enrich.EnqueueRange(retry);
                Log.Write($"启动重试：{retry.Count} 条历史失败条目改由 TMDB 再试一次");
            }
        }

        if (SettingsStore.Instance.Current.AutoScanOnStart)
            _ = ScanAllAsync();
    }

    // ---------------- 扫描 ----------------

    /// <summary>
    /// 把「库里有海报、磁盘上却没有」的条目修好。
    ///
    /// 出现这种情况只有两种原因：用户在 posters 目录里手动删了图片，或者点了「清空海报缓存」。
    /// 不管哪一种，库里的 PosterFile 都还指着一个不存在的文件，结果是卡片一直挂着占位符，
    /// 而队列看状态是 Success 也不会再试——海报就这么永久丢了。
    ///
    /// 这里统一检测一遍：文件真的不在了，就清空引用、退回 Pending 重新排队。
    /// 只改元数据，不碰用户的视频文件，也不会删除任何还在的海报。
    /// </summary>
    /// <returns>修好的条数。</returns>
    public async Task<int> RepairMissingPostersAsync()
    {
        var broken = new List<Movie>();

        foreach (var m in _all)
        {
            if (string.IsNullOrWhiteSpace(m.PosterFile)) continue;

            // 两种「坏」都算坏：文件被删了，或者文件还在但其实是 HTML／损坏的图。
            // 后者以前会被当成「已有海报」永远不再修，界面上就是一片打不开的白页。
            bool ok;
            try
            {
                var path = Path.IsPathRooted(m.PosterFile)
                    ? m.PosterFile
                    : Paths.PosterPath(m.PosterFile);
                ok = PosterCache.IsUsableImageFile(path);
            }
            catch { ok = false; }

            if (!ok) broken.Add(m);
        }

        if (broken.Count == 0) return 0;

        foreach (var m in broken)
        {
            m.PosterFile = null;
            m.BackdropFile = null;
            // 只有还「活着」的条目才值得重刮：用户明确忽略/判定为非影片的不要翻案。
            if (m.ScrapeState == ScrapeState.Success) m.ScrapeState = ScrapeState.Pending;
        }

        try { await _db.ClearPosterRefsAsync(broken.Select(m => m.Id)).ConfigureAwait(true); }
        catch (Exception ex) { Log.Write($"poster repair: 写库失败 {ex.GetType().Name} {ex.Message}"); }

        Log.Write($"poster repair: {broken.Count} 条海报文件已不在磁盘上，退回待刮削");
        return broken.Count;
    }

    /// <summary>
    /// 「清空海报缓存」按钮的收尾：文件删完之后把引用也清掉并重新排队，
    /// 否则界面上会剩一片「有记录、没图片」的空卡。
    /// </summary>
    public async Task<int> RequeuePostersAsync()
    {
        var repaired = await RepairMissingPostersAsync().ConfigureAwait(true);
        if (repaired == 0) return 0;

        var q = _all
            .Where(m => m.IsAvailable && m.ScrapeState == ScrapeState.Pending)
            .Where(m => !_moviesOnly || !MovieClassifier.LooksLikeNonFeature(m))
            .OrderByDescending(m => MovieClassifier.ScrapePriority(m))
            .ToList();

        if (q.Count > 0) _enrich.EnqueueRange(q);
        return q.Count;
    }

    public async Task ScanAllAsync()
    {
        if (IsScanning) return;

        IsScanning = true;
        StatusLine = "正在扫描…";
        _scanCts = new CancellationTokenSource();

        try
        {
            var roots = BuildScanRoots();
            if (roots.Count == 0)
            {
                StatusLine = "没有可扫描的位置";
                return;
            }

            foreach (var (root, removable) in roots)
            {
                if (_scanCts.IsCancellationRequested) break;
                StatusLine = $"正在读取 {root}";
                await ScanRootAsync(root, removable, _scanCts.Token).ConfigureAwait(true);
            }

            FlushIncoming();
            RebuildDisks();
            ApplyFilter();
            RefreshStats();
            StatusLine = "离线索引 · 只在本机";
        }
        catch (OperationCanceledException)
        {
            StatusLine = "扫描已停止";
        }
        catch (Exception ex)
        {
            StatusLine = "扫描出错：" + ex.Message;
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public async Task ScanSingleRootAsync(string root, bool removable)
    {
        if (!_activeScans.TryAdd(root, 0)) return;

        try
        {
            IsScanning = true;
            StatusLine = removable ? $"正在读取 {root}" : $"正在扫描 {root}";

            using var cts = new CancellationTokenSource();
            await ScanRootAsync(root, removable, cts.Token).ConfigureAwait(true);

            FlushIncoming();
            RebuildDisks();
            ApplyFilter();
            RefreshStats();

            StatusLine = removable ? $"{root} 已接入" : "离线索引 · 只在本机";
        }
        catch { }
        finally
        {
            _activeScans.TryRemove(root, out _);
            IsScanning = _activeScans.Count > 0;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private Task ScanRootAsync(string root, bool removable, CancellationToken ct)
    {
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.CurrentFile))
                StatusLine = Trim(p.CurrentFile);
        });

        return Task.Run(() =>
        {
            try { _scanner.ScanRoot(root, removable, _index, _incoming.Enqueue, progress, ct); }
            catch (OperationCanceledException) { }
            catch { }
        }, ct);
    }

    private static string Trim(string path)
    {
        if (path.Length <= 58) return path;
        return path[..26] + " … " + path[^26..];
    }

    private void FlushIncoming()
    {
        if (_incoming.IsEmpty) return;

        var batch = new List<Movie>();
        while (_incoming.TryDequeue(out var m)) batch.Add(m);
        if (batch.Count == 0) return;

        _all.AddRange(batch);

        bool simpleView = string.IsNullOrWhiteSpace(SearchText)
                          && SortMode == SortMode.Title
                          && Filter == LibraryFilter.All
                          && DiskFilter is null;

        if (simpleView)
        {
            VisibleMovies.AddRange(batch.OrderBy(m => m.DisplayTitle, StringComparer.CurrentCulture));
        }
        else
        {
            ApplyFilter();
        }

        RefreshStats();
        _ = PersistBatchAsync(batch);
    }

    private async Task PersistBatchAsync(List<Movie> batch)
    {
        foreach (var m in batch)
        {
            if (_disposed) return;
            try { await _db.UpsertAsync(m).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write($"db upsert2 «{m.DisplayTitle}» FAILED: {ex.Message}"); }
            if (m.IsAvailable) _enrich.Enqueue(m);
        }
    }

    private List<(string root, bool removable)> BuildScanRoots()
    {
        var s = SettingsStore.Instance.Current;
        var result = new List<(string, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string Key(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\').ToUpperInvariant(); }
            catch { return p.ToUpperInvariant(); }
        }

        foreach (var r in s.ScanRoots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            bool exists;
            try { exists = Directory.Exists(r); } catch { exists = false; }
            if (!exists) continue;
            if (!seen.Add(Key(r))) continue;
            result.Add((r, LibraryScanner.IsRemovable(r)));
        }

        if (s.AutoScanFixedDrives)
        {
            foreach (var d in LibraryScanner.EnumerateDrives(includeRemovable: false))
            {
                try
                {
                    if (d.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(Key(d.RootDirectory.FullName))) continue;
                    result.Add((d.RootDirectory.FullName, false));
                }
                catch { }
            }
        }

        return result;
    }

    public void CancelScan() => _scanCts?.Cancel();

    // ---------------- 磁盘与统计 ----------------

    private void RebuildDisks()
    {
        var groups = _all
            .GroupBy(m => m.VolumeRoot, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existing = Disks.ToDictionary(d => d.Root, StringComparer.OrdinalIgnoreCase);

        Disks.Clear();
        foreach (var g in groups)
        {
            var root = g.Key;
            var sample = g.First();
            var online = IsVolumeOnline(root);

            if (existing.TryGetValue(root, out var chip))
            {
                chip.Count = g.Count();
                chip.Online = online;
                Disks.Add(chip);
            }
            else
            {
                Disks.Add(new DiskChip
                {
                    Root = root,
                    Letter = sample.DriveLetter + ":",
                    Label = sample.VolumeLabel,
                    Count = g.Count(),
                    Online = online
                });
            }
        }
    }

    private static bool IsVolumeOnline(string root)
    {
        try
        {
            var di = new DriveInfo(Path.GetPathRoot(root) ?? root);
            return di.IsReady;
        }
        catch { return false; }
    }

    private void RefreshStats()
    {
        long bytes = 0;
        int external = 0, offline = 0;

        // 用户视角的数量是「墙上显示了多少部」，不是「硬盘里有多少个视频文件」。
        // 过滤开着的时候，手机录像和切片不计入，但要告诉他隐藏了多少，免得以为丢了。
        IEnumerable<Movie> counted = MoviesOnly
            ? _all.Where(m => !MovieClassifier.IsDefinitelyNotMovie(m))
            : _all;

        int shown = 0;
        foreach (var m in counted)
        {
            shown++;
            bytes += m.FileSize;
            if (m.IsRemovable) external++;
            if (!m.IsAvailable) offline++;
        }

        var hidden = _all.Count - shown;

        var parts = new List<string> { $"{shown} 部电影", Fmt.Size(bytes) };
        if (external > 0) parts.Add($"外接磁盘 {external} 部");
        if (offline > 0) parts.Add($"{offline} 部离线保留");
        if (hidden > 0) parts.Add($"已隐藏 {hidden} 个非影片文件");
        HeaderSummary = string.Join("   ·   ", parts);

        OnPropertyChanged(nameof(AllCount));
        OnPropertyChanged(nameof(LocalCount));
        OnPropertyChanged(nameof(ExternalCount));
        OnPropertyChanged(nameof(FavoriteCount));
        OnPropertyChanged(nameof(AllCountText));
        OnPropertyChanged(nameof(LocalCountText));
        OnPropertyChanged(nameof(ExternalCountText));
        OnPropertyChanged(nameof(FavoriteCountText));
        OnPropertyChanged(nameof(TopStatus));
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ---------------- 过滤排序 ----------------

    private void ApplyFilter()
    {
        IEnumerable<Movie> q = _all;

        // 硬盘里大半文件根本不是电影（手机录像、分集切片、课程视频）。
        // 默认把「明确不是电影」的挡在墙外，否则一面墙 200 多张卡里能刮到海报的只有个位数，
        // 看上去就是「海报怎么全都不显示」。已刮到元数据或已有海报的一律保留，绝不误杀。
        if (MoviesOnly)
            q = q.Where(m => !MovieClassifier.IsDefinitelyNotMovie(m));

        q = Filter switch
        {
            LibraryFilter.Local => q.Where(m => !m.IsRemovable),
            LibraryFilter.External => q.Where(m => m.IsRemovable),
            LibraryFilter.Favorite => q.Where(m => m.IsFavorite),
            _ => q
        };

        if (DiskFilter is not null)
            q = q.Where(m => string.Equals(m.VolumeRoot, DiskFilter, StringComparison.OrdinalIgnoreCase));

        var search = SearchText?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            q = q.Where(m => terms.All(t => Matches(m, t)));
        }

        q = SortMode switch
        {
            SortMode.Year => q.OrderByDescending(m => m.Year).ThenBy(m => m.DisplayTitle, StringComparer.CurrentCulture),
            SortMode.Rating => q.OrderByDescending(m => m.Rating).ThenBy(m => m.DisplayTitle, StringComparer.CurrentCulture),
            SortMode.Size => q.OrderByDescending(m => m.FileSize),
            SortMode.Recent => q.OrderByDescending(m => m.LastSeenUtc),
            _ => q.OrderBy(m => m.DisplayTitle, StringComparer.CurrentCulture)
        };

        VisibleMovies.ReplaceAll(q);
    }

    private static bool Matches(Movie m, string term)
    {
        if (m.DisplayTitle.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        if (m.OriginalTitle is not null && m.OriginalTitle.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        if (m.FileName.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Genres is not null && m.Genres.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Director is not null && m.Director.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Actors is not null && m.Actors.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Year > 0 && m.Year.ToString().Contains(term)) return true;
        return false;
    }

    // ---------------- 磁盘插拔 ----------------

    public void HandleDriveArrived(string root)
    {
        if (!SettingsStore.Instance.Current.WatchRemovableDrives) return;

        RunOnUi(() =>
        {
            foreach (var m in _all.Where(x => RootEquals(x.VolumeRoot, root)))
            {
                if (m.IsAvailable) continue;
                m.IsAvailable = true;
                m.NotifyAvailability();
            }
            RebuildDisks();
            RefreshStats();
            ApplyFilter();
        });

        _ = _db.SetAvailabilityAsync(root, true);
        _ = ScanSingleRootAsync(root, LibraryScanner.IsRemovable(root));
    }

    public void HandleDriveRemoved(string root)
    {
        RunOnUi(() =>
        {
            foreach (var m in _all.Where(x => RootEquals(x.VolumeRoot, root)))
            {
                m.IsAvailable = false;
                m.NotifyAvailability();
            }
            RebuildDisks();
            RefreshStats();
            ApplyFilter();
            if (_all.Count > 0) ShowToast($"{root} 已断开");
        });

        _ = _db.SetAvailabilityAsync(root, false);
    }

    private static bool RootEquals(string? a, string? b)
        => string.Equals(a?.TrimEnd('\\'), b?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // ---------------- 操作 ----------------

    public void Play(Movie movie)
    {
        var (ok, message) = PlayerLauncher.Play(movie);
        if (!ok) ShowToast(message);
    }

    public void RevealInExplorer(Movie movie) => PlayerLauncher.RevealInExplorer(movie);

    public async Task ToggleFavoriteAsync(Movie movie)
    {
        movie.IsFavorite = !movie.IsFavorite;
        try { await _db.SetFavoriteAsync(movie.Id, movie.IsFavorite).ConfigureAwait(true); }
        catch { }
        RefreshStats();
        if (Filter == LibraryFilter.Favorite) ApplyFilter();
        ShowToast(movie.IsFavorite ? "已收藏" : "已取消收藏");
    }

    public async Task RescrapeAsync(Movie movie)
    {
        ShowToast("正在重新获取信息…");
        try
        {
            await _enrich.RescrapeAsync(movie, CancellationToken.None).ConfigureAwait(true);
            ApplyFilter();
            ShowToast(movie.HasPoster ? "已更新" : "没找到匹配的影片信息");
        }
        catch (Exception ex)
        {
            ShowToast("失败：" + ex.Message);
        }
    }

    public async Task ReproceAsync(Movie movie)
    {
        ShowToast("正在重新分析媒体参数…");
        try
        {
            await _enrich.ReproceAsync(movie, CancellationToken.None).ConfigureAwait(true);
            ShowToast(MediaProbe.Available ? "参数已刷新" : "未找到 MediaInfo，已用系统属性降级");
        }
        catch (Exception ex)
        {
            ShowToast("失败：" + ex.Message);
        }
    }

    public void ShowToast(string text)
    {
        RunOnUi(() =>
        {
            ToastText = text;
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    public async Task RescanConfiguredRootsAsync() => await ScanAllAsync().ConfigureAwait(true);

    /// <summary>设置页「测试」：同时探一下 TMDB 和豆瓣。</summary>
    public Task<(bool tmdbOk, string tmdbMsg, bool doubanOk, string doubanMsg)> TestSourcesAsync()
        => _meta.TestAllAsync(CancellationToken.None);

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanCts?.Cancel();
        _flushTimer.Stop();
        _searchDebounce.Stop();
        _toastTimer.Stop();
        _statusTimer.Stop();
        _enrich.Dispose();
    }
}
