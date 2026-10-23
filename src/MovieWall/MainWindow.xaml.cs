using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MovieWall.Models;
using MovieWall.Services;
using MovieWall.ViewModels;

namespace MovieWall;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private DriveWatcher? _watcher;

    private readonly List<(string name, string path)> _players = new();
    private string _selectedPlayerPath = "";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ---------------- 启动 ----------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            await RunSelfTestAsync();
            Application.Current.Shutdown();
            return;
        }

        // 刮削单条自检：--probe-scrape "雷神3：诸神黄昏"
        // 只跑一次真实搜索，把命中的标题/海报/类型/片长写进 moviewall.log，用来确认数据源还活着。
        var rawArgv = Environment.GetCommandLineArgs();
        int pi2 = Array.FindIndex(rawArgv, a => a.Equals("--probe-scrape", StringComparison.OrdinalIgnoreCase));
        if (pi2 >= 0)
        {
            var q = pi2 + 1 < rawArgv.Length ? rawArgv[pi2 + 1] : "雷神3";
            try
            {
                var svc = new MetadataService();
                var meta = await svc.Douban.SearchAsync(q, 0, CancellationToken.None);
                if (meta is null)
                {
                    Log.Write($"PROBE «{q}» -> NULL（没搜到，或数据源不可用）");
                }
                else
                {
                    Log.Write($"PROBE «{q}» -> {meta.Source} 「{meta.Title}」/「{meta.OriginalTitle}」 {meta.Year} " +
                              $"rating={meta.Rating} runtime={meta.RuntimeMinutes} genres={meta.Genres} " +
                              $"countries={meta.Countries} poster={meta.PosterUrl}");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"PROBE «{q}» EX {ex.GetType().Name}: {ex.Message}");
            }
            Application.Current.Shutdown();
            return;
        }

        // TMDB 自检：--probe-tmdb "Inception"
        // 先测配置（Key + 代理），再用真实片名搜一次。TMDB 没配好时这一段会明确告诉是哪一步挂了：
        // 401 = Key 不对；代理连不上 = Clash 没开；NULL = 搜不到或评分没过阈值。
        int ti = Array.FindIndex(rawArgv, a => a.Equals("--probe-tmdb", StringComparison.OrdinalIgnoreCase));
        if (ti >= 0)
        {
            var q = ti + 1 < rawArgv.Length ? rawArgv[ti + 1] : "Inception";
            try
            {
                var svc = new MetadataService();
                var (ok, msg) = await svc.Tmdb.TestAsync(CancellationToken.None);
                Log.Write($"TMDB-TEST -> {(ok ? "OK" : "FAIL")} {msg}");

                var meta = await svc.Tmdb.SearchAsync(q, 0, CancellationToken.None);
                Log.Write(meta is null
                    ? $"TMDB-PROBE «{q}» -> NULL"
                    : $"TMDB-PROBE «{q}» -> 「{meta.Title}」/「{meta.OriginalTitle}」 {meta.Year} " +
                      $"rating={meta.Rating} runtime={meta.RuntimeMinutes} genres={meta.Genres} poster={meta.PosterUrl}");
            }
            catch (Exception ex)
            {
                Log.Write($"TMDB-PROBE «{q}» EX {ex.GetType().Name}: {ex.Message}");
            }
            Application.Current.Shutdown();
            return;
        }

        // 片名解析自检：--parse-test 把库里所有条目的「旧标题 -> 新解析结果」写进日志，
        // 用来判断新规则是修好了还是改坏了，不写库。
        if (rawArgv.Any(a => a.Equals("--parse-test", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var db = new LibraryDatabase();
                var all = await db.LoadAllAsync();
                int changed = 0;
                foreach (var m in all)
                {
                    var p = FileNameParser.Parse(m.FileName);
                    if (string.Equals(p.Title, m.Title, StringComparison.Ordinal)) continue;
                    changed++;
                    Log.Write($"PARSE  {m.FileName}\n         旧: {m.Title}  ({m.Year})\n         新: {p.Title}  ({p.Year})");
                }
                Log.Write($"PARSE 完成：{all.Count} 条，标题有变化 {changed} 条");
            }
            catch (Exception ex)
            {
                Log.Write($"PARSE EX {ex.GetType().Name}: {ex.Message}");
            }
            Application.Current.Shutdown();
            return;
        }

        // 批量刮削自检：--batch-scrape 30 跑前 30 条没海报的，把结果写日志，不开窗。
        int bi = Array.FindIndex(rawArgv, a => a.Equals("--batch-scrape", StringComparison.OrdinalIgnoreCase));
        if (bi >= 0)
        {
            int limit = 20;
            if (bi + 1 < rawArgv.Length) int.TryParse(rawArgv[bi + 1], out limit);

            try
            {
                var db = new LibraryDatabase();
                await db.InitializeAsync();
                var all = await db.LoadAllAsync();

                var reparsed = FileNameParser.ReparseStale(all);
                Log.Write($"BATCH 先重解析标题 {reparsed} 条");

                var svc = new MetadataService();
                int ok = 0, fail = 0, newPoster = 0, i = 0;

                var targets = all
                    .Where(m => !m.HasPoster)
                    .OrderBy(m => MovieClassifier.LooksLikeNonFeature(m) ? 1 : 0)
                    .ThenByDescending(m => m.Year > 1900 ? 1 : 0)
                    .ThenByDescending(m => m.Duration?.TotalMinutes ?? 0)
                    .ToList();

                foreach (var m in targets)
                {
                    if (i++ >= limit) break;
                    bool r;
                    try { r = await svc.EnrichAsync(m, CancellationToken.None); }
                    catch (Exception ex) { Log.Write($"BATCH «{m.DisplayTitle}» EX {ex.Message}"); r = false; }

                    if (r) ok++; else fail++;
                    if (!string.IsNullOrEmpty(m.PosterFile)) newPoster++;

                    Log.Write($"BATCH {i}/{Math.Min(limit, targets.Count)} «{m.DisplayTitle}»({m.Year}) -> " +
                              $"{(r ? "OK" : "FAIL")} state={m.ScrapeState} poster={m.PosterFile ?? "-"}");
                    try { await db.UpsertAsync(m); } catch { }
                }

                Log.Write($"BATCH 完成：尝试 {Math.Min(i, targets.Count)} 条，成功 {ok}，失败 {fail}，有海报 {newPoster}");
            }
            catch (Exception ex)
            {
                Log.Write($"BATCH EX {ex.GetType().Name}: {ex.Message}");
            }

            Application.Current.Shutdown();
            return;
        }

        // 非影片判定自检：--classify-test 把库里每条的名字和判定结果写进日志。
        // 用来核对「这条为什么被藏起来 / 为什么还在刮」——判定只看名字和时长，不联网。
        if (rawArgv.Any(a => a.Equals("--classify-test", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var db = new LibraryDatabase();
                await db.InitializeAsync();
                var all = await db.LoadAllAsync();

                int nonMovie = 0;
                foreach (var m in all.OrderBy(x => MovieClassifier.LooksLikeNonFeature(x) ? 0 : 1)
                                     .ThenBy(x => x.DisplayTitle, StringComparer.CurrentCulture))
                {
                    bool nf = MovieClassifier.LooksLikeNonFeature(m);
                    if (nf) nonMovie++;
                    Log.Write($"CLASSIFY {(nf ? "非影片" : "影片  ")} state={m.ScrapeState,-8} «{m.DisplayTitle}»");
                }
                Log.Write($"CLASSIFY 完成：{all.Count} 条，判为非影片 {nonMovie} 条");
            }
            catch (Exception ex)
            {
                Log.Write($"CLASSIFY EX {ex.GetType().Name}: {ex.Message}");
            }
            Application.Current.Shutdown();
            return;
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;

        _watcher = new DriveWatcher(this);
        _watcher.DriveArrived += root => _vm.HandleDriveArrived(root);
        _watcher.DriveRemoved += root => _vm.HandleDriveRemoved(root);

        await _vm.InitializeAsync();

        FadeInPage(PagesOf(_vm.Page));

        // 界面截图自检：--shot settings C:\tmp\shot.png
        // 深色主题最容易出「文字和背景一个颜色」的问题，肉眼点开一次才发现太慢。
        // 这个模式把窗口渲染成 PNG，改完样式直接对比，不用人工复现。
        int shi = Array.FindIndex(rawArgv, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
        if (shi >= 0)
        {
            var target = shi + 1 < rawArgv.Length ? rawArgv[shi + 1].ToLowerInvariant() : "settings";
            var outPath = shi + 2 < rawArgv.Length
                ? rawArgv[shi + 2]
                : Path.Combine(Path.GetTempPath(), "moviewall-shot.png");

            await Task.Delay(900);
            if (target.StartsWith("settings")) OpenSettings();

            await Task.Delay(800);   // 等淡入动画和布局跑完

            // --shot settings-bottom：滚到设置面板底部，检查下半部分控件
            if (target.EndsWith("-bottom"))
            {
                SettingsScroll.ScrollToEnd();
                await Task.Delay(400);
            }

            Log.Write($"SHOT 状态 内存key={SettingsStore.Instance.Current.TmdbApiKey.Length}字符 " +
                      $"proxy={SettingsStore.Instance.Current.ProxyUrl} " +
                      $"TxtKey={TxtTmdbKey.Text.Length}字符 TxtProxy={TxtProxy.Text.Length}字符 " +
                      $"CmbSel={CmbPlayer.SelectedIndex} CmbText={TxtPlayerName.Text}");
            try
            {
                var rtb = new RenderTargetBitmap(
                    Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                rtb.Render(this);

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(outPath)) enc.Save(fs);

                Log.Write($"SHOT {target} -> {outPath}");
            }
            catch (Exception ex)
            {
                Log.Write($"SHOT EX {ex.GetType().Name}: {ex.Message}");
            }

            Application.Current.Shutdown();
            return;
        }

        var argv = Environment.GetCommandLineArgs();
        if (argv.Any(a => a.Equals("--demo-detail", StringComparison.OrdinalIgnoreCase))
            || argv.Any(a => a.Equals("--demo-spec", StringComparison.OrdinalIgnoreCase)))
        {
            await Task.Delay(1400);
            // --demo-spec 可以指定片名关键字，默认取第一部有海报的
            var list = argv.ToList();
            int ki = list.FindIndex(a => a.Equals("--demo-spec", StringComparison.OrdinalIgnoreCase)
                                      || a.Equals("--demo-detail", StringComparison.OrdinalIgnoreCase));
            var key = ki >= 0 && ki + 1 < list.Count && !list[ki + 1].StartsWith("--")
                ? list[ki + 1] : null;
            var target = (key is null ? null : _vm.VisibleMovies.FirstOrDefault(m => m.DisplayTitle.Contains(key)))
                         ?? _vm.VisibleMovies.FirstOrDefault(m => m.HasPoster)
                         ?? _vm.VisibleMovies.FirstOrDefault();
            if (target is not null)
            {
                _vm.OpenDetail(target);
                if (argv.Any(a => a.Equals("--demo-spec", StringComparison.OrdinalIgnoreCase)))
                {
                    await Task.Delay(900);
                    _vm.OpenSpec();
                }
            }
        }
    }

    /// <summary>自检：验证 Upsert 是否能完整存下并读回所有字段。</summary>
    private static async Task RunSelfTestAsync()
    {
        var db = new LibraryDatabase();
        await db.InitializeAsync();

        var probe = new MediaDetail
        {
            ContainerFormat = "Matroska · v4",
            Duration = TimeSpan.FromMinutes(120),
            OverallBitRate = 68_700_000,
            FrameCount = 259473,
            EncodedApplication = "mkvmerge v83.0",
            VideoCount = 1, AudioCount = 2, TextCount = 3,
            VideoFormat = "HEVC", VideoProfile = "Main 10", VideoLevel = "5.1",
            VideoCodecId = "V_MPEGH/ISO/HEVC",
            Width = 3840, Height = 2160,
            FrameRate = 23.976, BitDepth = 10,
            HdrFormat = "Dolby Vision / SMPTE ST 2086",
            ColorPrimaries = "BT.2020",
            AudioTracks = { new AudioTrackInfo { Format = "DTS", FormatProfile = "MA", Channels = "6", Language = "zh" } },
            TextTracks = { new TextTrackInfo { Format = "PGS", Language = "zh", Title = "简英双语", IsDefault = true } }
        };

        var m = new Movie
        {
            FilePath = @"Z:/selftest/奥本海默.mkv",
            FileName = "奥本海默.mkv",
            FileSize = 86_500_000_000,
            VolumeLabel = "新加卷",
            IsRemovable = true,
            Title = "奥本海默",
            OriginalTitle = "Oppenheimer",
            Year = 2023,
            Rating = 8.8,
            RatingCount = 851398,
            Genres = "剧情 / 传记 / 历史",
            Director = "克里斯托弗·诺兰",
            Actors = "基里安·墨菲 / 艾米莉·布朗特",
            Countries = "美国 / 英国",
            ReleaseDate = "2023-08-30",
            Overview = "故事简介测试",
            ImdbId = "tt15398776",
            DoubanId = "35593344",
            PosterFile = "selftest.jpg",
            PosterOrigin = "douban",
            ScrapeState = ScrapeState.Success,
            ProbeDone = true,
            ProbeJson = System.Text.Json.JsonSerializer.Serialize(probe),
            Detail = probe,
            IsFavorite = true
        };

        await db.UpsertAsync(m);
        var loaded = await db.LoadAllAsync();
        var back = loaded.FirstOrDefault(x => x.FilePath == m.FilePath);

        if (back is null) { Log.Write("SELFTEST: 读回失败，记录不存在"); return; }

        var checks = new (string name, bool ok, string got)[]
        {
            ("Title", back.Title == "奥本海默", back.Title),
            ("OriginalTitle", back.OriginalTitle == "Oppenheimer", back.OriginalTitle),
            ("Year", back.Year == 2023, back.Year.ToString()),
            ("Rating", Math.Abs(back.Rating - 8.8) < 0.01, back.Rating.ToString()),
            ("RatingCount", back.RatingCount == 851398, back.RatingCount.ToString()),
            ("Director", back.Director == "克里斯托弗·诺兰", back.Director ?? "null"),
            ("PosterFile", back.PosterFile == "selftest.jpg", back.PosterFile ?? "null"),
            ("ScrapeState", back.ScrapeState == ScrapeState.Success, back.ScrapeState.ToString()),
            ("ProbeDone", back.ProbeDone, back.ProbeDone.ToString()),
            ("VolumeLabel", back.VolumeLabel == "新加卷", back.VolumeLabel),
            ("IsRemovable", back.IsRemovable, back.IsRemovable.ToString()),
            ("IsFavorite", back.IsFavorite, back.IsFavorite.ToString()),
            ("ProbeJson", !string.IsNullOrEmpty(back.ProbeJson), (back.ProbeJson?.Length ?? 0).ToString()),
            ("Detail", back.Detail is not null, back.Detail is null ? "null" : "ok")
        };

        int pass = 0;
        foreach (var c in checks)
        {
            Log.Write($"SELFTEST {(c.ok ? "PASS" : "FAIL")}  {c.name,-16} = {c.got}");
            if (c.ok) pass++;
        }
        Log.Write($"SELFTEST 结果 {pass}/{checks.Length}");

        await db.DeleteAsync(m.Id);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _watcher?.Dispose();
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Page):
                FadeInPage(PagesOf(_vm.Page));
                break;

            case nameof(MainViewModel.ToastText):
                AnimateToast(_vm.ToastText is not null);
                break;
        }
    }

    private UIElement? PagesOf(AppPage page) => page switch
    {
        AppPage.Detail => PageDetail,
        AppPage.Spec => PageSpec,
        _ => PageLibrary
    };

    private void FadeInPage(UIElement? page)
    {
        if (page is null) return;

        page.BeginAnimation(OpacityProperty, null);
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
        page.Opacity = 1;
    }

    // ---------------- 窗口按钮 ----------------

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M0,3 L6,3 L6,9 M3,0 L9,0 L9,6 L3,6 Z"
            : "M0,0 L9,0 L9,9 L0,9 Z");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------------- 设置 ----------------

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var s = SettingsStore.Instance.Current;

        TxtTmdbKey.Text = s.TmdbApiKey;
        TxtProxy.Text = s.ProxyUrl;
        ChkDoubanOnly.IsChecked = s.PreferDoubanOnly;
        ChkAutoScan.IsChecked = s.AutoScanOnStart;
        ChkWatchDrives.IsChecked = s.WatchRemovableDrives;
        ChkMoviesOnly.IsChecked = s.ShowMoviesOnly;
        TxtMinSize.Text = s.MinFileSizeMb.ToString();
        TxtPlayerName.Text = s.PlayerName;
        TxtSourceStatus.Text = "";

        LstRoots.Items.Clear();
        foreach (var r in s.ScanRoots) LstRoots.Items.Add(r);

        _players.Clear();
        CmbPlayer.Items.Clear();
        CmbPlayer.Items.Add("系统默认播放器");

        foreach (var (name, path) in PlayerLauncher.DetectPlayers())
        {
            _players.Add((name, path));
            CmbPlayer.Items.Add($"{name}  ·  {path}");
        }
        CmbPlayer.Items.Add("自定义…");

        _selectedPlayerPath = s.PlayerPath ?? "";
        int idx = _players.FindIndex(p => string.Equals(p.path, _selectedPlayerPath, StringComparison.OrdinalIgnoreCase));
        CmbPlayer.SelectedIndex = idx >= 0 ? idx + 1 : 0;

        UpdateCacheSizeLabel();

        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
        SettingsOverlay.Opacity = 1;
    }

    private void CloseSettings()
    {
        var fade = new DoubleAnimation(SettingsOverlay.Opacity, 0, TimeSpan.FromMilliseconds(150))
        {
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            SettingsOverlay.BeginAnimation(OpacityProperty, null);
            SettingsOverlay.Opacity = 0;
            SettingsOverlay.Visibility = Visibility.Collapsed;
        };
        SettingsOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void OnCancelSettingsClick(object sender, RoutedEventArgs e) => CloseSettings();

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        var s = SettingsStore.Instance.Current;
        s.TmdbApiKey = TxtTmdbKey.Text.Trim();
        s.ProxyUrl = TxtProxy.Text.Trim();
        s.PreferDoubanOnly = ChkDoubanOnly.IsChecked == true;
        s.AutoScanOnStart = ChkAutoScan.IsChecked == true;
        s.WatchRemovableDrives = ChkWatchDrives.IsChecked == true;
        s.ShowMoviesOnly = ChkMoviesOnly.IsChecked == true;
        s.PlayerPath = _selectedPlayerPath;
        s.PlayerName = string.IsNullOrWhiteSpace(TxtPlayerName.Text) ? "Yaoplayer" : TxtPlayerName.Text.Trim();

        if (int.TryParse(TxtMinSize.Text.Trim(), out var mb)) s.MinFileSizeMb = Math.Max(0, mb);
        s.ScanRoots = LstRoots.Items.Cast<string>().Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        SettingsStore.Instance.Save();

        // 走 ViewModel 的 setter：它负责重新过滤并刷新计数（直接改 settings 不会刷新墙）
        _vm.MoviesOnly = s.ShowMoviesOnly;

        CloseSettings();

        _vm.ShowToast("设置已保存");
        await _vm.RescanConfiguredRootsAsync();
    }

    private async void OnTestSourceClick(object sender, RoutedEventArgs e)
    {
        TxtSourceStatus.Text = "正在测试…";

        var s = SettingsStore.Instance.Current;
        var backup = s.TmdbApiKey;
        s.TmdbApiKey = TxtTmdbKey.Text.Trim();

        var (tmdbOk, tmdbMsg, doubanOk, doubanMsg) = await _vm.TestSourcesAsync();
        s.TmdbApiKey = backup;

        TxtSourceStatus.Text = $"TMDB：{tmdbMsg}    ·    豆瓣：{doubanMsg}";
        TxtSourceStatus.Foreground = tmdbOk || doubanOk
            ? new SolidColorBrush(Color.FromRgb(0x8F, 0xCF, 0x8F))
            : new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A));
    }

    private void OnBrowsePlayerClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择播放器",
            Filter = "可执行文件|*.exe|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _selectedPlayerPath = dlg.FileName;
            CmbPlayer.Items.Add($"自定义  ·  {dlg.FileName}");
            CmbPlayer.SelectedIndex = CmbPlayer.Items.Count - 1;
            if (string.IsNullOrWhiteSpace(TxtPlayerName.Text))
                TxtPlayerName.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private void OnAddRootClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择电影目录" };
        if (dlg.ShowDialog() == true && !LstRoots.Items.Contains(dlg.FolderName))
            LstRoots.Items.Add(dlg.FolderName);
    }

    private void OnRemoveRootClick(object sender, RoutedEventArgs e)
    {
        if (LstRoots.SelectedItem is string s) LstRoots.Items.Remove(s);
    }

    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        PosterCache.ClearDisk();
        UpdateCacheSizeLabel();

        // 图片删了但库里的引用还在，会把卡片变成一片占位符。
        // 这里顺手把引用清掉并重新排队，等于「清空并重新下载」。
        var requeued = await _vm.RequeuePostersAsync().ConfigureAwait(true);
        _vm.ShowToast(requeued > 0
            ? $"海报缓存已清空，{requeued} 张正在重新下载"
            : "海报缓存已清空");
    }

    private void UpdateCacheSizeLabel()
        => TxtCacheSize.Text = $"海报缓存占用 {Common.Fmt.Size(PosterCache.DiskUsageBytes())}";

    // ---------------- 快捷键 ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (SettingsOverlay.Visibility == Visibility.Visible)
                {
                    CloseSettings();
                    e.Handled = true;
                }
                else if (_vm.Page == AppPage.Spec)
                {
                    _vm.BackToDetail();
                    e.Handled = true;
                }
                else if (_vm.Page == AppPage.Detail)
                {
                    _vm.BackToLibrary();
                    e.Handled = true;
                }
                else if (PageLibrary.ClearSearchIfAny())
                {
                    e.Handled = true;
                }
                break;

            case Key.F when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                if (_vm.Page != AppPage.Library) _vm.BackToLibrary();
                PageLibrary.FocusSearch();
                e.Handled = true;
                break;

            case Key.Space:
                if (_vm.CurrentMovie is { } m && _vm.Page != AppPage.Library)
                {
                    _vm.Play(m);
                    e.Handled = true;
                }
                break;

            case Key.Enter:
                if (_vm.Page == AppPage.Detail)
                {
                    _vm.OpenSpec();
                    e.Handled = true;
                }
                break;
        }
    }

    // ---------------- Toast ----------------

    private void AnimateToast(bool show)
    {
        if (show)
        {
            ToastHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            ToastHost.Opacity = 1;
        }
        else
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) => { ToastHost.BeginAnimation(OpacityProperty, null); ToastHost.Opacity = 0; };
            ToastHost.BeginAnimation(OpacityProperty, fade);
        }
    }
}
