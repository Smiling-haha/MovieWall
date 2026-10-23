using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using MovieWall.Models;

namespace MovieWall.Services;

public sealed record ScanProgress(string Root, int FilesScanned, int MoviesFound, string CurrentFile)
{
    public override string ToString() => CurrentFile;
}

public sealed record ScanResult(int MoviesFound, int EpisodesSkipped, int FilesScanned, TimeSpan Elapsed, bool Cancelled);

/// <summary>
/// 文件系统扫描。只用文件名 + 体积 + 修改时间，不读取文件内容，
/// 因此对用户隐私的影响仅限于「本机内存里的一份路径清单」。
///
/// 刻意只做「发现 + 解析」这种微秒级的事，技术参数探测和网络刮削全部丢给
/// <see cref="EnrichmentQueue"/> 异步补。这样一插上 U 盘，海报墙几乎立刻开始长东西。
/// </summary>
public sealed class LibraryScanner
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".ts", ".m2ts",
        ".mpg", ".mpeg", ".rmvb", ".rm", ".webm", ".vob", ".ogv", ".divx", ".asf", ".3gp"
    };

    public static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && VideoExtensions.Contains(ext);
    }

    // 用户（或下载工具）常常会把海报和片子放在同一个目录里。
    // 这种情况不用联网，也不用复制文件，直接引用那张图就行。
    private static readonly string[] LocalPosterNames = { "poster", "folder", "cover", "front", "movie", "海报" };
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

    /// <summary>
    /// 找同目录下的本地海报，返回绝对路径；没有就返回 null。
    /// 顺序：poster/folder/cover/海报 → 与视频同名的图片 → xxx-poster.jpg。
    /// 只读文件系统，不复制、不改动用户任何文件。
    /// </summary>
    public static string? FindLocalPoster(string videoPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(videoPath);
            if (string.IsNullOrEmpty(dir)) return null;

            var stem = Path.GetFileNameWithoutExtension(videoPath);

            foreach (var n in LocalPosterNames)
                foreach (var e in ImageExts)
                {
                    var p = Path.Combine(dir, n + e);
                    if (File.Exists(p)) return p;
                }

            foreach (var e in ImageExts)
            {
                var p = Path.Combine(dir, stem + e);
                if (File.Exists(p)) return p;

                p = Path.Combine(dir, stem + "-poster" + e);
                if (File.Exists(p)) return p;
            }
        }
        catch { }

        return null;
    }

    public ScanResult ScanRoot(
        string root,
        bool isRemovable,
        ConcurrentDictionary<string, Movie> known,
        Action<Movie> onDiscovered,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var settings = SettingsStore.Instance.Current;
        var minBytes = (long)Math.Max(0, isRemovable ? settings.MinRemovableFileSizeMb : settings.MinFileSizeMb) * 1024 * 1024;
        var excluded = new HashSet<string>(settings.ExcludedFolders, StringComparer.OrdinalIgnoreCase);

        // 卷标签只查一次，别给每个文件都 new 一个 DriveInfo
        string volumeLabel = "";
        bool volumeRemovable = isRemovable;
        try
        {
            var rootPath = Path.GetPathRoot(root) ?? root;
            var di = new DriveInfo(rootPath);
            if (di.IsReady) volumeLabel = di.VolumeLabel ?? "";
            volumeRemovable = di.DriveType is DriveType.Removable or DriveType.Network;
        }
        catch { }

        int filesScanned = 0, found = 0, episodes = 0;
        var lastReport = Stopwatch.StartNew();

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var dir = stack.Pop();

            string[] files;
            string[] subs;
            try
            {
                files = Directory.GetFiles(dir);
                subs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            catch (Exception) { continue; }

            foreach (var sub in subs)
            {
                try
                {
                    var name = Path.GetFileName(sub);
                    if (excluded.Contains(name)) continue;

                    var attrs = File.GetAttributes(sub);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue; // 防止符号链接成环

                    stack.Push(sub);
                }
                catch { }
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                filesScanned++;

                if (!IsVideoFile(file)) continue;

                FileInfo fi;
                try
                {
                    fi = new FileInfo(file);
                    if (!fi.Exists || fi.Length < minBytes) continue;
                }
                catch { continue; }

                // 同名片子已经在库里，且体积/时间都没变 → 直接复用，不重复探测
                if (known.TryGetValue(file, out var exist)
                    && exist.FileSize == fi.Length
                    && exist.LastWriteTicks == fi.LastWriteTimeUtc.Ticks)
                {
                    exist.LastSeenUtc = DateTime.UtcNow;
                    continue;
                }

                var parsed = FileNameParser.Parse(fi.Name);
                if (parsed.LooksLikeEpisode)
                {
                    episodes++;
                    continue; // 电视剧单集不进电影墙，否则一部剧会刷出几十张卡
                }

                var localPoster = FindLocalPoster(fi.FullName);

                var movie = new Movie
                {
                    FilePath = fi.FullName,
                    FileName = fi.Name,
                    FileSize = fi.Length,
                    LastWriteTicks = fi.LastWriteTimeUtc.Ticks,
                    VolumeRoot = Path.GetPathRoot(fi.FullName) ?? root,
                    VolumeLabel = volumeLabel,
                    IsRemovable = volumeRemovable,
                    Title = parsed.Title,
                    Year = parsed.Year,
                    IsAvailable = true,
                    LastSeenUtc = DateTime.UtcNow,
                    ScrapeState = ScrapeState.Pending,
                    PosterFile = localPoster,
                    PosterOrigin = localPoster is null ? null : "local"
                };

                // 如果旧记录存在（体积变了，可能是重新压制），保留已刮到的元数据
                if (known.TryGetValue(file, out var old))
                {
                    movie.Id = old.Id;
                    movie.PosterFile = old.PosterFile;
                    movie.BackdropFile = old.BackdropFile;
                    movie.PosterOrigin = old.PosterOrigin;
                    movie.Overview = old.Overview;
                    movie.Rating = old.Rating;
                    movie.Genres = old.Genres;
                    movie.TmdbId = old.TmdbId;
                    movie.ScrapeState = old.ScrapeState;
                    if (old.Year > 0) movie.Year = old.Year;
                    if (!string.IsNullOrWhiteSpace(old.Title)) movie.Title = old.Title;
                }

                known[file] = movie;
                onDiscovered(movie);
                found++;

                if (lastReport.ElapsedMilliseconds > 120)
                {
                    lastReport.Restart();
                    progress?.Report(new ScanProgress(root, filesScanned, found, fi.FullName));
                }
            }

            progress?.Report(new ScanProgress(root, filesScanned, found, dir));
        }

        progress?.Report(new ScanProgress(root, filesScanned, found, ""));

        return new ScanResult(found, episodes, filesScanned, sw.Elapsed, false);
    }

    /// <summary>列出当前所有可用的卷（固定盘 + 可移动盘）。</summary>
    public static List<DriveInfo> EnumerateDrives(bool includeRemovable)
    {
        var list = new List<DriveInfo>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType == DriveType.Fixed) list.Add(d);
                    else if (includeRemovable &&
                             (d.DriveType == DriveType.Removable || d.DriveType == DriveType.Network))
                        list.Add(d);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>判断某个卷是不是「可移动」——U 盘、SD 卡、部分 USB 移动硬盘。</summary>
    public static bool IsRemovable(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            return d.DriveType is DriveType.Removable or DriveType.Network;
        }
        catch { return false; }
    }
}
