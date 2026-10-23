using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using MovieWall.Models;

namespace MovieWall.Services;

/// <summary>
/// 媒体技术参数探测。走 MediaInfo CLI 的 JSON 输出。
///
/// 为什么必须是 MediaInfo：参考设计里的「Profile/Level、CodecID、色彩原色、传递特性、
/// 母版亮度、色度抽样、封装软件、编码库」这些字段，Windows 属性系统一个都给不了。
/// MediaInfo 是业界标准，这些值就是它算出来的。
///
/// 找不到 MediaInfo 时调用方要能优雅降级（<see cref="Available"/> 为 false）。
/// </summary>
public static class MediaProbe
{
    private static string? _toolPath;

    public static bool Available => _toolPath is not null;

    public static string ToolPath => _toolPath ?? "";

    public static string ToolVersion { get; private set; } = "";

    /// <summary>在应用目录、tools 子目录和 PATH 里找 MediaInfo.exe。</summary>
    public static void Initialize(string baseDir)
    {
        var candidates = new[]
        {
            Path.Combine(baseDir, "tools", "MediaInfo.exe"),
            Path.Combine(baseDir, "MediaInfo.exe"),
            Path.Combine(baseDir, "tools", "mediainfo.exe"),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) { _toolPath = c; break; }
        }

        if (_toolPath is null)
        {
            try
            {
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var p = Path.Combine(dir.Trim(), "MediaInfo.exe");
                    if (File.Exists(p)) { _toolPath = p; break; }
                }
            }
            catch { }
        }

        if (_toolPath is not null) ToolVersion = DetectVersion(_toolPath);
        Log.Write($"MediaInfo: {(_toolPath ?? "未找到")} {ToolVersion}");
    }

    private static string DetectVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "--Version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            // 输出形如 "MediaInfoLib - v26.05"
            var idx = outp.IndexOf("v", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 && outp.Length > idx + 1 ? outp[idx..].Trim().Split('\n')[0].Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>同步探测一部片子的全部技术参数。失败返回 null（调用方降级到属性系统）。</summary>
    public static MediaDetail? Probe(string filePath, CancellationToken ct = default)
    {
        if (_toolPath is null || string.IsNullOrWhiteSpace(filePath)) return null;

        try
        {
            if (!File.Exists(filePath)) return null;

            var psi = new ProcessStartInfo(_toolPath)
            {
                Arguments = $"--Output=JSON --Language=raw --Details=0 \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            // 大文件（REMUX / 原盘）MediaInfo 扫得快，但网络盘可能慢，给足 30 秒
            string json;
            using (var reader = proc.StandardOutput)
            {
                var readTask = reader.ReadToEndAsync(ct);
                if (!readTask.Wait(TimeSpan.FromSeconds(30), ct))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    Log.Write($"probe timeout: {Path.GetFileName(filePath)}");
                    return null;
                }
                json = readTask.Result;
            }

            proc.WaitForExit(3000);

            // 偶尔前面会混进 BOM 或告警行，从第一个 { 开始截
            var start = json.IndexOf('{');
            if (start < 0) return null;
            json = json[start..];

            return Parse(json, filePath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"probe error {Path.GetFileName(filePath)}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    // ---------------- JSON 解析 ----------------

    private static MediaDetail? Parse(string json, string filePath)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("media", out var media)) return null;
        if (!media.TryGetProperty("track", out var tracksEl)) return null;

        var tracks = tracksEl.ValueKind == JsonValueKind.Array
            ? tracksEl.EnumerateArray().ToList()
            : new List<JsonElement> { tracksEl };

        var d = new MediaDetail();

        foreach (var t in tracks)
        {
            var type = Str(t, "@type") ?? "";

            switch (type)
            {
                case "General":
                    d.ContainerFormat = Str(t, "Format");
                    d.ContainerProfile = Str(t, "Format_Profile");
                    d.ContainerCodecId = Str(t, "CodecID");
                    d.FileSize = Long(t, "FileSize") ?? 0;
                    d.Duration = Seconds(t, "Duration");
                    d.OverallBitRate = Long(t, "OverallBitRate");
                    d.FrameCount = Long(t, "FrameCount");
                    d.EncodedApplication = Str(t, "Encoded_Application") ?? Str(t, "Encoded_Library");
                    d.EncodedLibrary = Str(t, "Encoded_Library");
                    d.EncodedDate = Str(t, "Encoded_Date") ?? Str(t, "File_Created_Date");
                    d.TaggedDate = Str(t, "Tagged_Date");
                    d.ModifiedDate = Str(t, "File_Modified_Date");
                    d.VideoCount = Int(t, "VideoCount") ?? 0;
                    d.AudioCount = Int(t, "AudioCount") ?? 0;
                    d.TextCount = Int(t, "TextCount") ?? 0;

                    // 容器格式展示成「Matroska · v4」
                    if (!string.IsNullOrWhiteSpace(d.ContainerProfile) && d.ContainerProfile != "None")
                        d.ContainerFormat = $"{d.ContainerFormat} · {d.ContainerProfile}";
                    break;

                case "Video":
                    if (d.VideoFormat is not null) break; // 只取第一条视频轨
                    d.VideoFormat = Str(t, "Format");
                    d.VideoProfile = Str(t, "Format_Profile");
                    d.VideoLevel = Str(t, "Format_Level");
                    d.VideoCodecId = Str(t, "CodecID");
                    d.VideoDuration = Seconds(t, "Duration");
                    d.VideoBitRate = Long(t, "BitRate");
                    d.Width = Int(t, "Width");
                    d.Height = Int(t, "Height");
                    d.DisplayAspectRatio = Dbl(t, "DisplayAspectRatio");
                    d.PixelAspectRatio = Dbl(t, "PixelAspectRatio");
                    d.FrameRate = Dbl(t, "FrameRate");
                    d.FrameRateMode = Str(t, "FrameRate_Mode");
                    d.ColorSpace = Str(t, "ColorSpace");
                    d.ChromaSubsampling = Str(t, "ChromaSubsampling");
                    d.BitDepth = Int(t, "BitDepth");
                    d.ScanType = Str(t, "ScanType");
                    d.HdrFormat = Str(t, "HDR_Format");
                    d.HdrCompatibility = Str(t, "HDR_Format_Compatibility");
                    d.ColorPrimaries = Str(t, "colour_primaries") ?? Str(t, "ColorPrimaries");
                    d.TransferCharacteristics = Str(t, "transfer_characteristics") ?? Str(t, "TransferCharacteristics");
                    d.MatrixCoefficients = Str(t, "matrix_coefficients") ?? Str(t, "MatrixCoefficients");
                    d.MasteringDisplayLuminance = Str(t, "MasteringDisplay_Luminance");
                    d.MasteringDisplayPrimaries = Str(t, "MasteringDisplay_ColorPrimaries");
                    d.VideoStreamSize = Long(t, "StreamSize");
                    d.VideoTitle = Str(t, "Title");
                    d.VideoIsDefault = Str(t, "Default") == "Yes";
                    d.VideoIsForced = Str(t, "Forced") == "Yes";
                    break;

                case "Audio":
                    d.AudioTracks.Add(new AudioTrackInfo
                    {
                        Format = Str(t, "Format"),
                        FormatProfile = Str(t, "Format_Profile"),
                        CodecId = Str(t, "CodecID"),
                        Language = Str(t, "Language"),
                        LanguageLabel = LangLabel(Str(t, "Language"), Str(t, "Title")),
                        Title = Str(t, "Title"),
                        Channels = Str(t, "Channels"),
                        ChannelLayout = Str(t, "ChannelLayout"),
                        SamplingRate = Int(t, "SamplingRate"),
                        BitRate = Long(t, "BitRate"),
                        IsDefault = Str(t, "Default") == "Yes"
                    });
                    break;

                case "Text":
                    d.TextTracks.Add(new TextTrackInfo
                    {
                        Format = Str(t, "Format"),
                        CodecId = Str(t, "CodecID"),
                        Language = Str(t, "Language"),
                        LanguageLabel = LangLabel(Str(t, "Language"), Str(t, "Title")),
                        Title = Str(t, "Title"),
                        IsDefault = Str(t, "Default") == "Yes",
                        IsForced = Str(t, "Forced") == "Yes"
                    });
                    break;
            }
        }

        if (d.TextCount == 0) d.TextCount = d.TextTracks.Count;
        if (d.AudioCount == 0) d.AudioCount = d.AudioTracks.Count;

        return d;
    }

    /// <summary>把 ISO 639 语言码翻成中文名。带 Title 的中文标记优先。</summary>
    private static string? LangLabel(string? iso, string? title)
    {
        // 有些资源把「国语」「简英双语」写在轨道标题里，这个比语言码更准
        if (!string.IsNullOrWhiteSpace(title))
        {
            foreach (var k in new[] { "国语", "国配", "普通话", "粤语", "英语", "日语", "韩语", "简英", "简中", "繁中", "双语" })
            {
                if (title.Contains(k, StringComparison.OrdinalIgnoreCase)) return k;
            }
        }

        if (string.IsNullOrWhiteSpace(iso)) return null;
        var code = iso.Trim().ToLowerInvariant();
        if (code.StartsWith("zh")) return "中文";
        if (code.StartsWith("en")) return "英语";

        return code switch
        {
            "ja" or "jpn" => "日语",
            "ko" or "kor" => "韩语",
            "fr" or "fre" or "fra" => "法语",
            "de" or "ger" or "deu" => "德语",
            "es" or "spa" => "西班牙语",
            "ru" or "rus" => "俄语",
            "it" or "ita" => "意大利语",
            "pt" or "por" => "葡萄牙语",
            "th" or "tha" => "泰语",
            "cn" => "粤语",
            _ => iso.ToUpperInvariant()
        };
    }

    // ---------------- 字段读取辅助（MediaInfo 的数值一律是字符串） ----------------

    private static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
    }

    private static long? Long(JsonElement e, string name)
    {
        var s = Str(e, name);
        if (s is null) return null;
        if (long.TryParse(s, out var l)) return l;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return (long)d;
        return null;
    }

    private static int? Int(JsonElement e, string name)
    {
        var l = Long(e, name);
        return l is null ? null : (int)l.Value;
    }

    private static double? Dbl(JsonElement e, string name)
    {
        var s = Str(e, name);
        if (s is null) return null;
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static TimeSpan? Seconds(JsonElement e, string name)
    {
        var d = Dbl(e, name);
        return d is > 0 ? TimeSpan.FromSeconds(d.Value) : null;
    }
}
