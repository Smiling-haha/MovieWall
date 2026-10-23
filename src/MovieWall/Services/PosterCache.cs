using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MovieWall.Services;

/// <summary>
/// 海报位图的内存 LRU + 磁盘缓存。
/// 内存里最多留 <see cref="MaxCached"/> 张已解码图，滚出海报墙的会被回收；
/// 磁盘上永久保留，所以再次进入可视区只需几毫秒重新解码。
/// </summary>
public static class PosterCache
{
    private const int MaxCached = 140;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Lru = new();

    // 限制同时解码的数量，避免快速滚动时把线程池打满
    private static readonly SemaphoreSlim DecodeGate = new(4, 4);

    public static async Task<BitmapSource?> LoadAsync(string fileName, int decodeWidth)
    {
        var path = Paths.PosterPath(fileName);
        var key = fileName + "|" + decodeWidth;

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit))
            {
                Lru.Remove(key);
                Lru.AddFirst(key);
                return hit;
            }
        }

        if (!File.Exists(path))
        {
            Log.Write($"poster: MISSING FILE {fileName}");
            return null;
        }

        await DecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var bmp = await Task.Run(() => Decode(path, decodeWidth)).ConfigureAwait(false);
            if (bmp is null)
            {
                Log.Write($"poster: DECODE FAILED {fileName}");
                return null;
            }

            lock (Gate)
            {
                if (Cache.TryAdd(key, bmp)) Lru.AddFirst(key);
                while (Lru.Count > MaxCached)
                {
                    var last = Lru.Last!.Value;
                    Lru.RemoveLast();
                    Cache.Remove(last);
                }
            }
            return bmp;
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    /// <summary>
    /// 后台线程安全的解码。
    ///
    /// 注意：不要在这里用 new BitmapImage()。它对 StreamSource 的缓存走 Uri 作为 key，
    /// 后台线程下 UriSource 为空会抛 ArgumentNullException("key")。
    /// BitmapDecoder + TransformedBitmap 走的是纯 WIC 路径，随便哪个线程都能跑。
    /// </summary>
    private static BitmapSource? Decode(string path, int decodeWidth)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, false);

            var decoder = BitmapDecoder.Create(
                fs,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0) return null;

            BitmapSource frame = decoder.Frames[0];
            frame.Freeze();

            if (decodeWidth > 0 && frame.PixelWidth > decodeWidth)
            {
                double scale = (double)decodeWidth / frame.PixelWidth;
                var scaled = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                scaled.Freeze();
                return scaled;
            }

            return frame;
        }
        catch (Exception ex)
        {
            Log.Write($"poster: DECODE EX {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>把远程图存到本地缓存，返回文件名。已存在则直接返回。</summary>
    /// <param name="referer">豆瓣等站点有防盗链，图片请求必须带上来源页。</param>
    public static async Task<string?> SaveFromUrlAsync(HttpClient http, string url, CancellationToken ct,
        string? referer = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var ext = GuessExtension(url);
        var name = Hash(url) + ext;
        var dest = Paths.PosterPath(name);

        // 只有「确实是一张能解码的图」才算已缓存。
        // 以前这里只判长度 > 512，结果豆瓣返回的 HTML 页面（几十 KB）被当成图片缓存下来了……
        if (IsUsableImageFile(dest)) return name;

        try
        {
            var bytes = await FetchAsync(http, url, referer, ct).ConfigureAwait(false);

            if (bytes is null || bytes.Length < 512)
                return null;

            if (!LooksLikeImage(bytes))
            {
                // 豆瓣在风控/防盗链命中时会把这些图片地址 302 到「海报详情页」，
                // 拿到的是 HTML 而不是图。这里从页面里把真正的图片地址挖出来再下一次。
                Log.Write($"    poster 拿到的不是图片（{bytes.Length} 字节，疑似 HTML），尝试从页面里补救");
                var alt = await RecoverFromHtmlAsync(http, bytes, referer, ct).ConfigureAwait(false);
                if (alt is null)
                {
                    Log.Write("    poster 补救失败，本条先留空");
                    return null;
                }
                bytes = alt;
            }

            var tmp = dest + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, dest, overwrite: true);
            return name;
        }
        catch (Exception ex)
        {
            Log.Write($"    poster download ERROR {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>当前 posters 目录里这类「假图」有多少张（诊断用）。</summary>
    public static (int bad, int total) ScanBrokenFiles()
    {
        int bad = 0, total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Paths.PosterDir))
            {
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                if (!IsUsableImageFile(f)) bad++;
            }
        }
        catch { }
        return (bad, total);
    }

    /// <summary>
    /// 把 posts 目录里那些「其实是 HTML」的假海报就地修好：
    /// 从 HTML 里挖出真图地址重新下载，覆盖同名文件——库里的引用一个字都不用改。
    /// </summary>
    /// <returns>修好 / 失败数量。</returns>
    public static async Task<(int fixedCount, int failed)> RepairBrokenFilesAsync(
        Func<HttpClient> pickClient, CancellationToken ct)
    {
        int fixedCount = 0, failed = 0;

        List<string> files;
        try { files = Directory.EnumerateFiles(Paths.PosterDir).ToList(); }
        catch { return (0, 0); }

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsUsableImageFile(path)) continue;

            bool ok = false;
            try
            {
                var html = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                var img = await RecoverFromHtmlAsync(pickClient(), html, null, ct).ConfigureAwait(false);
                if (img is not null)
                {
                    await File.WriteAllBytesAsync(path, img, ct).ConfigureAwait(false);
                    fixedCount++;
                    ok = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Write($"poster repair: {Path.GetFileName(path)} {ex.GetType().Name}"); }

            if (!ok)
            {
                // 救不回来就删掉：下次启动的「海报缺失自愈」会把这条件重新排进刮削队列。
                try { File.Delete(path); } catch { }
                failed++;
                Log.Write($"poster repair: 放弃 {Path.GetFileName(path)}，已删除待重新刮削");
            }
        }

        Log.Write($"poster repair: 修复 {fixedCount} 张，放弃 {failed} 张");
        return (fixedCount, failed);
    }

    /// <summary>文件是不是一张能用的图：够大、而且真实格式是 JPEG/PNG/GIF/WEBP。</summary>
    public static bool IsUsableImageFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 512) return false;

            Span<byte> head = stackalloc byte[12];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 12);
            int n = fs.Read(head);
            return n >= 12 && LooksLikeImage(head[..n]);
        }
        catch { return false; }
    }

    private static bool LooksLikeImage(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12) return false;

        // JPEG
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
        // PNG
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
        // GIF
        if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return true;
        // WEBP: RIFF....WEBP
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
            && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return true;

        return false;
    }

    private static async Task<byte[]?> FetchAsync(HttpClient http, string url, string? referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(referer))
            req.Headers.Referrer = new Uri(referer);

        // 明确只要位图，别让 CDN 回 webp/avif——WIC 不一定解得了
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("image/jpeg,image/png,image/*;q=0.8,*/*;q=0.5");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Write($"    poster download {(int)resp.StatusCode} {url}");
            return null;
        }

        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>豆瓣的图片地址，形如 https://img9.doubanio.com/view/photo/s_ratio_poster/public/p1234567890.jpg</summary>
    private static readonly System.Text.RegularExpressions.Regex DoubanPhotoIdRx =
        new(@"p\d{6,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>HTML 里出现的豆瓣图片地址（海报详情页会把自己的图挂在 og:image / mainphoto 上）。</summary>
    private static readonly System.Text.RegularExpressions.Regex DoubanImgUrlRx = new(
        @"https?://img\d\.doubanio\.com/view/photo/[^""'\s<>\\]+",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 从一段 HTML 里找出真正的图片地址并下载。
    /// 优先用「海报详情页」里那张大图（l_ratio_poster），没有再退回页面里的其他 view/photo 地址。
    /// </summary>
    private static async Task<byte[]?> RecoverFromHtmlAsync(
        HttpClient http, byte[] htmlBytes, string? referer, CancellationToken ct)
    {
        string html;
        try { html = Encoding.UTF8.GetString(htmlBytes); }
        catch { return null; }

        foreach (var candidate in CandidateImageUrls(html))
        {
            try
            {
                var bytes = await FetchAsync(http, candidate, referer ?? "https://movie.douban.com/", ct)
                                .ConfigureAwait(false);
                if (bytes is not null && bytes.Length > 512 && LooksLikeImage(bytes))
                {
                    Log.Write($"    poster 从 HTML 里恢复成功：{candidate}");
                    return bytes;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return null;
    }

    private static IEnumerable<string> CandidateImageUrls(string html)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 先按「图片 id」拼 l_ratio_poster（大图，比 s_ratio_poster 清楚）
        foreach (System.Text.RegularExpressions.Match m in DoubanPhotoIdRx.Matches(html))
        {
            var id = m.Value;
            foreach (var host in new[] { "img9", "img1", "img2", "img3" })
            {
                var u = $"https://{host}.doubanio.com/view/photo/l_ratio_poster/public/{id}.jpg";
                if (seen.Add(u)) yield return u;
            }

            // 同一张图别在四个 host 上都试一遍太久，取到第一个 id 就够了
            break;
        }

        // 2) 再退化：页面里出现过的图片地址，把尺寸段换成 l_ratio_poster 试一次
        foreach (System.Text.RegularExpressions.Match m in DoubanImgUrlRx.Matches(html))
        {
            var raw = m.Value.Replace("\\/", "/");
            if (seen.Add(raw)) yield return raw;

            var big = System.Text.RegularExpressions.Regex.Replace(
                raw, @"/view/photo/[^/]+/", "/view/photo/l_ratio_poster/");
            if (big != raw && seen.Add(big)) yield return big;
        }
    }

    private static string GuessExtension(string url)
    {
        var q = url.IndexOf('?');
        var clean = q >= 0 ? url[..q] : url;
        var ext = Path.GetExtension(clean);
        return ext.Length is > 1 and <= 5 ? ext.ToLowerInvariant() : ".jpg";
    }

    private static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static long DiskUsageBytes()
    {
        try
        {
            if (!Directory.Exists(Paths.PosterDir)) return 0;
            return new DirectoryInfo(Paths.PosterDir).EnumerateFiles().Sum(f => f.Length);
        }
        catch { return 0; }
    }

    public static void ClearDisk()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Paths.PosterDir))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
        lock (Gate) { Cache.Clear(); Lru.Clear(); }
    }
}
