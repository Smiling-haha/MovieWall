using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MovieWall.Common;
using MovieWall.Services;

namespace MovieWall.Models;

/// <summary>
/// 一部电影。既是 SQLite 里的一行，也是海报墙直接绑定的 UI 对象。
/// </summary>
public sealed class Movie : ObservableObject
{
    // ---------------- 身份 / 位置 ----------------

    public long Id { get; set; }
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public long LastWriteTicks { get; set; }
    public string VolumeRoot { get; set; } = "";

    private string _volumeLabel = "";
    /// <summary>卷标，比如「新加卷」「8641」。</summary>
    public string VolumeLabel { get => _volumeLabel; set => Set(ref _volumeLabel, value); }

    private bool _isRemovable;
    public bool IsRemovable { get => _isRemovable; set => Set(ref _isRemovable, value); }

    // ---------------- 元数据 ----------------

    private string _title = "";
    public string Title
    {
        get => _title;
        set
        {
            if (!Set(ref _title, value)) return;
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(Initials));
            RefreshPlaceholder();
        }
    }

    private string _originalTitle = "";
    public string OriginalTitle { get => _originalTitle; set { if (Set(ref _originalTitle, value)) OnPropertyChanged(nameof(HasOriginalTitle)); } }

    private int _year;
    public int Year { get => _year; set { if (Set(ref _year, value)) { OnPropertyChanged(nameof(YearText)); OnPropertyChanged(nameof(CardMeta)); } } }

    private string? _overview;
    public string? Overview { get => _overview; set { if (Set(ref _overview, value)) OnPropertyChanged(nameof(HasOverview)); } }

    private double _rating;
    public double Rating { get => _rating; set { if (Set(ref _rating, value)) { OnPropertyChanged(nameof(RatingText)); OnPropertyChanged(nameof(HasRating)); } } }

    private int _ratingCount;
    public int RatingCount { get => _ratingCount; set { if (Set(ref _ratingCount, value)) OnPropertyChanged(nameof(RatingCountText)); } }

    private string? _genres;
    public string? Genres { get => _genres; set { if (Set(ref _genres, value)) { OnPropertyChanged(nameof(GenreText)); OnPropertyChanged(nameof(HasGenres)); } } }

    private string? _director;
    public string? Director { get => _director; set { if (Set(ref _director, value)) OnPropertyChanged(nameof(HasCredits)); } }

    private string? _actors;
    public string? Actors { get => _actors; set { if (Set(ref _actors, value)) OnPropertyChanged(nameof(HasCredits)); } }

    private string? _countries;
    public string? Countries { get => _countries; set { if (Set(ref _countries, value)) OnPropertyChanged(nameof(HasCredits)); } }

    private string? _releaseDate;
    public string? ReleaseDate { get => _releaseDate; set { if (Set(ref _releaseDate, value)) OnPropertyChanged(nameof(HasCredits)); } }

    public string? TmdbId { get; set; }
    public string? DoubanId { get; set; }
    public string? ImdbId { get; set; }

    // ---------------- 技术参数 ----------------

    private MediaDetail? _detail;
    /// <summary>
    /// MediaInfo 探测结果。为空表示还没探测，或探测失败（此时退回到下面的扁平字段）。
    /// </summary>
    public MediaDetail? Detail
    {
        get => _detail;
        set
        {
            if (!Set(ref _detail, value)) return;
            NotifyTechDeps();
            OnPropertyChanged(nameof(Badges));
            OnPropertyChanged(nameof(HasHdr));
            OnPropertyChanged(nameof(Is4K));
        }
    }

    /// <summary>MediaInfo 原始 JSON，持久化用。</summary>
    public string? ProbeJson { get; set; }

    private long? _durationMs;
    public long? DurationMs { get => _durationMs; set { if (Set(ref _durationMs, value)) NotifyTechDeps(); } }

    private int? _runtimeMinutes;
    public int? RuntimeMinutes { get => _runtimeMinutes; set { if (Set(ref _runtimeMinutes, value)) NotifyTechDeps(); } }

    private int? _videoWidth;
    public int? VideoWidth { get => _videoWidth; set { if (Set(ref _videoWidth, value)) NotifyTechDeps(); } }

    private int? _videoHeight;
    public int? VideoHeight { get => _videoHeight; set { if (Set(ref _videoHeight, value)) NotifyTechDeps(); } }

    private long? _bitrateBps;
    public long? BitrateBps { get => _bitrateBps; set { if (Set(ref _bitrateBps, value)) NotifyTechDeps(); } }

    private string? _videoCodec;
    public string? VideoCodec { get => _videoCodec; set { if (Set(ref _videoCodec, value)) NotifyTechDeps(); } }

    private string? _audioCodec;
    public string? AudioCodec { get => _audioCodec; set { if (Set(ref _audioCodec, value)) NotifyTechDeps(); } }

    // ---------------- 状态 ----------------

    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set { if (Set(ref _isFavorite, value)) OnPropertyChanged(nameof(FavoriteGlyph)); } }

    public string FavoriteGlyph => _isFavorite ? "♥" : "♡";

    public ScrapeState ScrapeState { get; set; }
    public bool IsAvailable { get; set; } = true;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public bool ProbeDone { get; set; }

    // ---------------- 海报 ----------------

    private string? _posterFile;
    public string? PosterFile
    {
        get => _posterFile;
        set
        {
            if (!Set(ref _posterFile, value)) return;
            _posterImage = null;
            _posterRequested = false;
            OnPropertyChanged(nameof(PosterImage));
            OnPropertyChanged(nameof(HasPoster));
            RefreshPlaceholder();
        }
    }

    private string? _backdropFile;
    public string? BackdropFile
    {
        get => _backdropFile;
        set
        {
            if (!Set(ref _backdropFile, value)) return;
            _backdropImage = null;
            _backdropRequested = false;
            OnPropertyChanged(nameof(BackdropImage));
        }
    }

    public string? PosterOrigin { get; set; }

    private ImageSource? _posterImage;
    private bool _posterRequested;
    private ImageSource? _backdropImage;
    private bool _backdropRequested;

    public ImageSource? PosterImage
    {
        get
        {
            if (_posterImage is null && !_posterRequested && !string.IsNullOrEmpty(_posterFile))
            {
                _posterRequested = true;
                _ = LoadPosterAsync(_posterFile!);
            }
            return _posterImage;
        }
        private set => Set(ref _posterImage, value);
    }

    public ImageSource? BackdropImage
    {
        get
        {
            if (_backdropImage is null && !_backdropRequested && !string.IsNullOrEmpty(_backdropFile))
            {
                _backdropRequested = true;
                _ = LoadBackdropAsync(_backdropFile!);
            }
            return _backdropImage;
        }
        private set => Set(ref _backdropImage, value);
    }

    private async Task LoadPosterAsync(string fileName)
    {
        var img = await PosterCache.LoadAsync(fileName, 420).ConfigureAwait(true);
        if (img is not null) PosterImage = img;
    }

    private async Task LoadBackdropAsync(string fileName)
    {
        var img = await PosterCache.LoadAsync(fileName, 1600).ConfigureAwait(true);
        if (img is not null) BackdropImage = img;
    }

    public void RebindImages()
    {
        OnPropertyChanged(nameof(PosterImage));
        OnPropertyChanged(nameof(BackdropImage));
    }

    // ---------------- 派生展示 ----------------

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(_title) ? Path.GetFileNameWithoutExtension(FileName) : _title;

    public string YearText => _year > 0 ? _year.ToString() : "—";

    public string RatingText => _rating > 0 ? _rating.ToString("0.0") : "—";

    public string RatingCountText => _ratingCount > 0 ? $"{_ratingCount:N0} 人评分" : "";

    public bool HasRating => _rating > 0;

    public bool HasPoster => !string.IsNullOrEmpty(_posterFile);

    public bool HasOverview => !string.IsNullOrWhiteSpace(_overview);

    public bool HasGenres => !string.IsNullOrWhiteSpace(_genres);

    public bool HasOriginalTitle => !string.IsNullOrWhiteSpace(_originalTitle)
                                    && !string.Equals(_originalTitle, _title, StringComparison.OrdinalIgnoreCase);

    public bool HasCredits => !string.IsNullOrWhiteSpace(_director)
                              || !string.IsNullOrWhiteSpace(_actors)
                              || !string.IsNullOrWhiteSpace(_countries)
                              || !string.IsNullOrWhiteSpace(_releaseDate);

    public string GenreText => _genres ?? "";

    public TimeSpan? Duration
        => _detail?.Duration
           ?? (_durationMs is > 0 ? TimeSpan.FromMilliseconds(_durationMs!.Value) : (TimeSpan?)null)
           ?? (_runtimeMinutes is > 0 ? TimeSpan.FromMinutes(_runtimeMinutes!.Value) : (TimeSpan?)null);

    public string DurationText => Duration is { } d ? Fmt.Duration(d) : "—";

    /// <summary>「1h 59m」——卡片下面那行用紧凑写法。</summary>
    public string DurationShort => Duration is { } d ? Fmt.DurationShort(d) : "—";

    public string ResolutionLabel
        => _detail?.ResolutionLabel ?? Fmt.ResolutionTag(_videoWidth, _videoHeight) switch
        {
            "4K" => "4K UHD",
            "" => "—",
            var x => x
        };

    public bool Is4K => _detail?.Is4K ?? (Math.Max(_videoWidth ?? 0, _videoHeight ?? 0) >= 2000);

    public bool HasHdr => _detail?.HasHdr ?? false;

    public long? EffectiveBitrate
        => _detail?.VideoBitRate ?? (_bitrateBps is > 0 ? _bitrateBps : Fmt.EstimateBitrate(FileSize, Duration));

    public string BitrateText => Fmt.Bitrate(EffectiveBitrate);

    public string SizeText => Fmt.Size(FileSize);

    public int? Width => _detail?.Width ?? _videoWidth;
    public int? Height => _detail?.Height ?? _videoHeight;

    public string ResolutionText => Fmt.Resolution(Width, Height);

    public string CodecText
    {
        get
        {
            if (_detail is not null) return _detail.CodecProfileLabel;
            var parts = new[] { _videoCodec, _audioCodec }.Where(x => !string.IsNullOrWhiteSpace(x));
            var s = string.Join(" · ", parts);
            return s.Length == 0 ? "—" : s;
        }
    }

    // ---------------- 卡片元素 ----------------

    /// <summary>卡片左上角的规格角标，比如 ["4K","UHD"]。</summary>
    [JsonIgnore]
    public List<string> Badges
    {
        get
        {
            var list = new List<string>();
            var res = ResolutionLabel;
            if (res is "4K UHD") { list.Add("4K"); list.Add("UHD"); }
            else if (res is "1080P" or "2K" or "720P") list.Add(res);
            return list;
        }
    }

    public bool HasBadges => Badges.Count > 0;

    [JsonIgnore]
    public string? HdrBadge => _detail?.HdrLabel ?? (_bitrateBps is null ? null : null);

    /// <summary>磁盘标签：「G: 8641」。</summary>
    [JsonIgnore]
    public string DiskLabel
    {
        get
        {
            var letter = DriveLetter;
            if (string.IsNullOrWhiteSpace(letter)) return "";
            return string.IsNullOrWhiteSpace(_volumeLabel) ? letter : $"{letter} {_volumeLabel}";
        }
    }

    public string DriveLetter
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(FilePath);
                if (string.IsNullOrEmpty(root)) return "";
                return root.TrimEnd('\\', ':').ToUpperInvariant();
            }
            catch { return ""; }
        }
    }

    /// <summary>「已断开」/「在线」。</summary>
    [JsonIgnore]
    public string AvailabilityText => IsAvailable ? "在线" : "已断开";

    /// <summary>卡片下面那行小字：「2019 · 1h 59m · 4K UHD」。</summary>
    [JsonIgnore]
    public string CardMeta
    {
        get
        {
            var bits = new List<string>(4);
            if (_year > 0) bits.Add(_year.ToString());
            var d = DurationShort;
            if (d != "—") bits.Add(d);
            var res = ResolutionLabel;
            if (res != "—") bits.Add(res);
            if (bits.Count == 0) bits.Add(Fmt.Size(FileSize));
            return string.Join("   ·   ", bits);
        }
    }

    /// <summary>技术规格页顶部那行路径摘要。</summary>
    [JsonIgnore]
    public string TechSummary
    {
        get
        {
            var bits = new List<string>(4);
            if (_detail is not null)
            {
                bits.Add(_detail.ResolutionLabel);
                if (_detail.HdrLabel is not null) bits.Add(_detail.HdrLabel);
                bits.Add(_detail.CodecLabel);
            }
            else
            {
                bits.Add(ResolutionText);
            }
            bits.Add(Fmt.Bitrate(EffectiveBitrate));
            return string.Join("  ·  ", bits);
        }
    }

    [JsonIgnore]
    public string Initials
    {
        get
        {
            var t = DisplayTitle.Trim();
            if (t.Length == 0) return "?";
            return t.Length >= 2 && char.IsSurrogatePair(t, 0) ? t[..2] : t[..1];
        }
    }

    /// <summary>详情页顶部那行眉标：「FEATURE PRESENTATION · 2023 · TT15398776」。</summary>
    [JsonIgnore]
    public string EyebrowLine
    {
        get
        {
            var parts = new List<string> { "FEATURE PRESENTATION" };
            if (_year > 0) parts.Add(_year.ToString());
            if (!string.IsNullOrWhiteSpace(ImdbId)) parts.Add(ImdbId!.Trim().ToUpperInvariant());
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>「851,398 人评分 · 简介来自豆瓣」。</summary>
    [JsonIgnore]
    public string ScoreLine
    {
        get
        {
            var parts = new List<string>();
            if (_ratingCount > 0) parts.Add($"{_ratingCount:N0} 人评分");
            parts.Add(string.Equals(PosterOrigin, "tmdb", StringComparison.OrdinalIgnoreCase)
                ? "简介来自 TMDB"
                : "简介来自豆瓣");
            return string.Join("  ·  ", parts);
        }
    }

    [JsonIgnore]
    public string PosterSourceLabel => string.Equals(PosterOrigin, "tmdb", StringComparison.OrdinalIgnoreCase)
        ? "TMDB 海报"
        : "官方海报";

    // ---------------- 占位渐变 ----------------

    private Brush? _placeholderBrush;

    public Brush PlaceholderBrush => _placeholderBrush ??= BuildPlaceholder(DisplayTitle);

    private void RefreshPlaceholder()
    {
        _placeholderBrush = null;
        OnPropertyChanged(nameof(PlaceholderBrush));
    }

    private static Brush BuildPlaceholder(string key)
    {
        unchecked
        {
            var h = 17u;
            foreach (var c in key) h = h * 31 + c;
            var hue = h % 360;
            var a = Hsl(hue, 0.30, 0.20);
            var b = Hsl((hue + 42) % 360, 0.36, 0.09);
            var brush = new LinearGradientBrush(a, b, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
            brush.Freeze();
            return brush;
        }
    }

    private static Color Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    // ---------------- 规格表（技术规格页 / 完整参数页） ----------------

    private List<SpecRow>? _videoRows, _fileRows, _fullLeft, _fullRight;
    private List<string>? _chips;

    [JsonIgnore]
    public List<string> Chips
        => _chips ??= _detail?.SpecChips().Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                      ?? new List<string> { ResolutionLabel };

    [JsonIgnore]
    public List<SpecRow> VideoRows => _videoRows ??= BuildVideoRows();

    [JsonIgnore]
    public List<SpecRow> FileRows => _fileRows ??= BuildFileRows();

    [JsonIgnore]
    public List<SpecRow> FullRowsLeft => _fullLeft ??= BuildFullLeft();

    [JsonIgnore]
    public List<SpecRow> FullRowsRight => _fullRight ??= BuildFullRight();

    [JsonIgnore]
    public List<AudioTrackInfo> AudioTracks => _detail?.AudioTracks ?? new List<AudioTrackInfo>();

    [JsonIgnore]
    public List<TextTrackInfo> TextTracks => _detail?.TextTracks ?? new List<TextTrackInfo>();

    [JsonIgnore]
    public bool HasAudioTracks => AudioTracks.Count > 0;

    [JsonIgnore]
    public bool HasTextTracks => TextTracks.Count > 0;

    private List<SpecRow> BuildVideoRows()
    {
        var d = _detail;
        return new List<SpecRow>
        {
            new("编码", d?.CodecProfileLabel ?? CodecText),
            new("分辨率", ResolutionText),
            new("帧率", d?.FrameRate is > 0 ? $"{d.FrameRate:0.##} fps" : "—"),
            new("位深", d?.BitDepth is > 0 ? $"{d.BitDepth} bit" : "—"),
            new("色彩", ColorLine(d)),
            new("动态范围", d?.HdrLabel ?? "SDR")
        };
    }

    private static string ColorLine(MediaDetail? d)
    {
        if (d is null) return "—";

        // 有色彩原色/传递特性时拼成 MediaInfo 那种原始串
        if (!string.IsNullOrWhiteSpace(d.TransferCharacteristics)
            || !string.IsNullOrWhiteSpace(d.MatrixCoefficients)
            || !string.IsNullOrWhiteSpace(d.ColorPrimaries))
        {
            var parts = new[] { d.TransferCharacteristics, d.MatrixCoefficients, d.ColorPrimaries }
                .Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join("/", parts);
        }

        var basic = new[] { d.ColorSpace, d.ChromaSubsampling }.Where(x => !string.IsNullOrWhiteSpace(x));
        var s = string.Join(" ", basic);
        return s.Length == 0 ? "—" : s;
    }

    private List<SpecRow> BuildFileRows()
    {
        var d = _detail;
        return new List<SpecRow>
        {
            new("容器", d?.ContainerFormat?.Split('·')[0].Trim() ?? Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant()),
            new("文件大小", SizeText),
            new("总码率", Fmt.Bitrate(d?.OverallBitRate ?? EffectiveBitrate)),
            new("时长", DurationText),
            new("磁盘", string.IsNullOrWhiteSpace(_volumeLabel) ? DriveLetter + ":" : $"{_volumeLabel}  ·  {DriveLetter}:"),
            new("识别状态", ProbeDone ? "已完成" : "分析中…")
        };
    }

    private List<SpecRow> BuildFullLeft()
    {
        var d = _detail;
        return new List<SpecRow>
        {
            new("容器", d?.ContainerFormat ?? "—"),
            new("完整路径", FilePath),
            new("容器格式", d?.ContainerFormat ?? "—"),
            new("文件大小", GiB(FileSize)),
            new("时长", Clock(d?.Duration ?? Duration)),
            new("总码率", BitrateSlash(d?.OverallBitRate)),
            new("总帧数", d?.FrameCount is > 0 ? d.FrameCount.Value.ToString("N0") : "—"),
            new("封装日期", CleanDate(d?.EncodedDate)),
            new("封装软件", d?.EncodedApplication ?? "—"),
            new("编码库", d?.EncodedLibrary ?? "—"),
            new("流数量", d is null ? "—" : $"{d.VideoCount} 视频 · {d.AudioCount} 音频 · {d.TextCount} 字幕"),
            new("文件修改", CleanDate(d?.ModifiedDate))
        };
    }

    private List<SpecRow> BuildFullRight()
    {
        var d = _detail;
        if (d is null)
            return new List<SpecRow> { new("状态", "尚未分析（未找到 MediaInfo 或文件不可读）") };

        var title = string.IsNullOrWhiteSpace(d.VideoTitle) ? "—" : d.VideoTitle!;

        return new List<SpecRow>
        {
            new("视频 1", $"{d.VideoFormat ?? "—"} · {title}"),
            new("编码格式", d.VideoFormat ?? "—"),
            new("Profile / Level", ProfileLevel(d)),
            new("Codec ID", d.VideoCodecId ?? "—"),
            new("时长", Clock(d.VideoDuration ?? d.Duration)),
            new("码率", BitrateSlash(d.VideoBitRate)),
            new("分辨率", $"{d.Width} × {d.Height}"),
            new("画面比例", AspectRatio(d.DisplayAspectRatio)),
            new("像素宽高比", d.PixelAspectRatio is > 0 ? d.PixelAspectRatio.Value.ToString("0.000") : "—"),
            new("帧率", d.FrameRate is > 0 ? $"{d.FrameRate:0.###} FPS" : "—"),
            new("帧率模式", d.FrameRateMode ?? "—"),
            new("色彩空间", d.ColorSpace ?? "—"),
            new("色度抽样", d.ChromaSubsampling ?? "—"),
            new("位深", d.BitDepth is > 0 ? $"{d.BitDepth} bits" : "—"),
            new("HDR 格式", d.HdrFormat ?? (d.HasHdr ? d.HdrLabel! : "—")),
            new("色彩原色", d.ColorPrimaries ?? "—"),
            new("传递特性", d.TransferCharacteristics ?? "—"),
            new("色彩矩阵", d.MatrixCoefficients ?? "—"),
            new("母版亮度", d.MasteringDisplayLuminance ?? "—"),
            new("母版原色", d.MasteringDisplayPrimaries ?? "—"),
            new("流大小", d.VideoStreamSize is > 0
                ? $"{GiB(d.VideoStreamSize.Value)} ({d.VideoStreamSize.Value * 100.0 / Math.Max(1, FileSize):0}%)"
                : "—"),
            new("标题", title),
            new("默认 / 强制", $"{(d.VideoIsDefault ? "是" : "否")} / {(d.VideoIsForced ? "是" : "否")}")
        };
    }

    private static string ProfileLevel(MediaDetail d)
    {
        var parts = new[] { d.VideoProfile, d.VideoLevel is null ? null : "Level " + d.VideoLevel }
            .Where(x => !string.IsNullOrWhiteSpace(x) && x != "None");
        var s = string.Join(" · ", parts);
        return s.Length == 0 ? "—" : s;
    }

    private static string AspectRatio(double? ratio)
    {
        if (ratio is null or <= 0) return "—";
        var r = ratio.Value;
        // 常见比例直接给名字，其余保留三位小数
        if (Math.Abs(r - 1.778) < 0.02) return "16:9";
        if (Math.Abs(r - 1.333) < 0.02) return "4:3";
        if (Math.Abs(r - 2.35) < 0.05) return "2.35:1";
        if (Math.Abs(r - 2.39) < 0.05) return "2.39:1";
        if (Math.Abs(r - 1.85) < 0.05) return "1.85:1";
        return r.ToString("0.000");
    }

    private static string GiB(long bytes)
    {
        if (bytes <= 0) return "—";
        double gib = bytes / 1024.0 / 1024.0 / 1024.0;
        return gib >= 1 ? $"{gib:0.00} GiB" : $"{bytes / 1024.0 / 1024.0:0.0} MiB";
    }

    private static string Clock(TimeSpan? ts)
    {
        if (ts is null or { TotalSeconds: <= 0 }) return "—";
        var t = ts.Value;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    private static string BitrateSlash(long? bps)
    {
        if (bps is null or <= 0) return "—";
        double v = bps.Value;
        if (v >= 1_000_000) return $"{v / 1_000_000:0.#} Mb/s";
        return $"{v / 1000:0} kb/s";
    }

    private static string CleanDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        return raw.Replace(" UTC", " UTC").Trim();
    }

    private void NotifyTechDeps()
    {
        _videoRows = null;
        _fileRows = null;
        _fullLeft = null;
        _fullRight = null;
        _chips = null;

        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(DurationShort));
        OnPropertyChanged(nameof(ResolutionLabel));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(BitrateText));
        OnPropertyChanged(nameof(EffectiveBitrate));
        OnPropertyChanged(nameof(CodecText));
        OnPropertyChanged(nameof(CardMeta));
        OnPropertyChanged(nameof(TechSummary));
        OnPropertyChanged(nameof(Is4K));
        OnPropertyChanged(nameof(VideoRows));
        OnPropertyChanged(nameof(FileRows));
        OnPropertyChanged(nameof(FullRowsLeft));
        OnPropertyChanged(nameof(FullRowsRight));
        OnPropertyChanged(nameof(Chips));
        OnPropertyChanged(nameof(HasAudioTracks));
        OnPropertyChanged(nameof(HasTextTracks));
    }

    [JsonIgnore]
    public bool FileExists
    {
        get { try { return System.IO.File.Exists(FilePath); } catch { return false; } }
    }

    public void NotifyAvailability()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(AvailabilityText));
    }
}
