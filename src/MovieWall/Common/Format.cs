using System.Globalization;

namespace MovieWall.Common;

/// <summary>展示层格式化。集中一处，避免各控件里散落 if/else。</summary>
public static class Fmt
{
    public static string Duration(TimeSpan? ts)
    {
        if (ts is null || ts.Value <= TimeSpan.Zero) return "—";
        var t = ts.Value;
        if (t.TotalHours >= 1)
        {
            var h = (int)t.TotalHours;
            var m = t.Minutes;
            return m == 0 ? $"{h} 小时" : $"{h} 小时 {m} 分";
        }
        return $"{Math.Max(1, t.Minutes)} 分钟";
    }

    public static string DurationShort(TimeSpan? ts)
    {
        if (ts is null || ts.Value <= TimeSpan.Zero) return "—";
        var t = ts.Value;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m";
        return $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))}s";
    }

    public static string Bitrate(long? bps)
    {
        if (bps is null or <= 0) return "—";
        double v = bps.Value;
        if (v >= 1_000_000_000) return (v / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture) + " Gbps";
        if (v >= 1_000_000) return (v / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + " Mbps";
        return (v / 1000).ToString("0", CultureInfo.InvariantCulture) + " Kbps";
    }

    public static string Size(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[i];
    }

    public static string Rating(double r) => r <= 0 ? "—" : r.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>把 3840×2160 归到一个短标签：4K / 2K / 1080P / 720P / SD。</summary>
    public static string ResolutionTag(int? w, int? h)
    {
        var px = h ?? 0;
        if (w is > 0 && h is > 0) px = Math.Min(w.Value, h.Value) == h.Value ? h.Value : h.Value;
        return px switch
        {
            >= 2000 => "4K",
            >= 1400 => "2K",
            >= 1000 => "1080P",
            >= 700 => "720P",
            >= 400 => "480P",
            > 0 => "SD",
            _ => ""
        };
    }

    public static string Resolution(int? w, int? h)
        => w is > 0 && h is > 0 ? $"{w} × {h}" : "—";

    /// <summary>码率拿不到时用「文件体积 ÷ 时长」估算。</summary>
    public static long? EstimateBitrate(long fileSizeBytes, TimeSpan? duration)
    {
        if (fileSizeBytes <= 0 || duration is null || duration.Value.TotalSeconds < 1) return null;
        return (long)(fileSizeBytes * 8.0 / duration.Value.TotalSeconds);
    }
}
