namespace MovieWall.Models;

/// <summary>
/// 一条音频轨道。
/// </summary>
public sealed class AudioTrackInfo
{
    public string? Format { get; set; }          // DTS / AC-3 / AAC
    public string? FormatProfile { get; set; }   // MA / LC / Core
    public string? CodecId { get; set; }
    public string? Language { get; set; }        // en / zh / ja
    public string? LanguageLabel { get; set; }   // 英语 / 中文（本地化）
    public string? Title { get; set; }
    public string? Channels { get; set; }        // 6
    public string? ChannelLayout { get; set; }
    public int? SamplingRate { get; set; }
    public long? BitRate { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>「英语 DTS-HD.MA 5.1」这种给人看的一行。</summary>
    public string DisplayName
    {
        get
        {
            var fmt = Format ?? "音频";
            if (!string.IsNullOrWhiteSpace(FormatProfile) && FormatProfile != "None")
                fmt += "." + FormatProfile;
            var ch = Channels switch
            {
                "1" => "1.0",
                "2" => "2.0",
                "6" => "5.1",
                "8" => "7.1",
                _ => null
            };
            var parts = new[] { LanguageLabel, fmt, ch }
                .Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join(" ", parts);
        }
    }

    /// <summary>右侧那列短标签，如「DTS-HD MA」。</summary>
    public string ShortLabel => (Format ?? "") + (string.IsNullOrWhiteSpace(FormatProfile) || FormatProfile == "None" ? "" : " " + FormatProfile);
}

/// <summary>一条字幕轨道。</summary>
public sealed class TextTrackInfo
{
    public string? Format { get; set; }        // PGS / ASS / SRT / UTF-8
    public string? CodecId { get; set; }
    public string? Language { get; set; }
    public string? LanguageLabel { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }

    public string DisplayName
        => string.IsNullOrWhiteSpace(Title) ? (LanguageLabel ?? Format ?? "字幕") : Title!;

    /// <summary>左侧那个小方框里的格式缩写。</summary>
    public string Badge => (Format ?? "SUB").ToUpperInvariant() switch
    {
        "PGS" => "PGS",
        "ASS" or "SSA" => "ASS",
        "SUBRIP" => "SRT",
        "UTF-8" or "UTF8" => "SRT",
        var f => f.Length > 4 ? f[..4] : f
    };

    public string SubLine => string.Join(" · ", new[] { LanguageLabel, IsDefault ? "默认" : null }
        .Where(x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>
/// MediaInfo 探测结果。字段命名刻意贴近 MediaInfo 自己的输出，
/// 这样对着参考图（图2 完整参数页）逐行都能找到对应的值。
/// </summary>
public sealed class MediaDetail
{
    // ---------------- 容器 ----------------
    public string? ContainerFormat { get; set; }      // Matroska
    public string? ContainerProfile { get; set; }     // v4
    public string? ContainerCodecId { get; set; }
    public long FileSize { get; set; }
    public TimeSpan? Duration { get; set; }
    public long? OverallBitRate { get; set; }
    public long? FrameCount { get; set; }
    public string? EncodedDate { get; set; }
    public string? TaggedDate { get; set; }
    public string? EncodedApplication { get; set; }
    public string? EncodedLibrary { get; set; }
    public int VideoCount { get; set; }
    public int AudioCount { get; set; }
    public int TextCount { get; set; }
    public string? ModifiedDate { get; set; }

    // ---------------- 视频轨道 ----------------
    public string? VideoFormat { get; set; }          // HEVC
    public string? VideoProfile { get; set; }         // Main 10
    public string? VideoLevel { get; set; }           // 5.1
    public string? VideoCodecId { get; set; }         // V_MPEGH/ISO/HEVC
    public TimeSpan? VideoDuration { get; set; }
    public long? VideoBitRate { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DisplayAspectRatio { get; set; }   // 1.778
    public double? PixelAspectRatio { get; set; }     // 1.000
    public double? FrameRate { get; set; }
    public string? FrameRateMode { get; set; }        // CFR / VFR
    public string? ColorSpace { get; set; }           // YUV
    public string? ChromaSubsampling { get; set; }    // 4:2:0
    public int? BitDepth { get; set; }                // 10
    public string? ScanType { get; set; }
    public string? HdrFormat { get; set; }
    public string? HdrCompatibility { get; set; }
    public string? ColorPrimaries { get; set; }
    public string? TransferCharacteristics { get; set; }
    public string? MatrixCoefficients { get; set; }
    public string? MasteringDisplayLuminance { get; set; }
    public string? MasteringDisplayPrimaries { get; set; }
    public long? VideoStreamSize { get; set; }
    public string? VideoTitle { get; set; }
    public bool VideoIsDefault { get; set; }
    public bool VideoIsForced { get; set; }

    public List<AudioTrackInfo> AudioTracks { get; set; } = new();
    public List<TextTrackInfo> TextTracks { get; set; } = new();

    // ---------------- 派生标签 ----------------

    /// <summary>4K UHD / 2K / 1080P / 720P</summary>
    public string ResolutionLabel
    {
        get
        {
            var px = Height ?? Width ?? 0;
            if (px >= 2000) return "4K UHD";
            if (px >= 1400) return "2K";
            if (px >= 1000) return "1080P";
            if (px >= 700) return "720P";
            if (px >= 400) return "480P";
            return px > 0 ? "SD" : "—";
        }
    }

    public bool Is4K => (Height ?? Width ?? 0) >= 2000;

    /// <summary>HDR10 / HDR10+ / 杜比视界</summary>
    public string? HdrLabel
    {
        get
        {
            var f = HdrFormat ?? "";
            if (f.Length == 0) return null;
            var lower = f.ToLowerInvariant();
            if (lower.Contains("dolby vision") || lower.Contains("dvhe") || lower.Contains("dovi")) return "杜比视界";
            if (lower.Contains("hdr10+") || lower.Contains("smpte st 2096") || lower.Contains("hdr10p")) return "HDR10+";
            if (lower.Contains("hdr10") || lower.Contains("smpte st 2086")) return "HDR10";
            if (lower.Contains("hlg")) return "HLG";
            return "HDR";
        }
    }

    public bool HasHdr => HdrLabel is not null;

    /// <summary>HEVC · 10BIT</summary>
    public string CodecLabel
    {
        get
        {
            var fmt = VideoFormat switch
            {
                "HEVC" => "HEVC",
                "AVC" => "H.264",
                "AV1" => "AV1",
                "MPEG-4 Visual" => "MPEG-4",
                null => "—",
                var x => x
            };
            var bits = BitDepth is > 0 ? $"{BitDepth}BIT" : null;
            return bits is null ? fmt : $"{fmt} · {bits}";
        }
    }

    /// <summary>「HEVC · Main 10」——技术规格页「编码」那一行用。</summary>
    public string CodecProfileLabel
    {
        get
        {
            var fmt = VideoFormat ?? "—";
            if (!string.IsNullOrWhiteSpace(VideoProfile) && VideoProfile != "None")
                return $"{fmt} · {VideoProfile}";
            return fmt;
        }
    }

    public string AudioSummary
    {
        get
        {
            if (AudioTracks.Count == 0) return "无音轨";
            var t = AudioTracks[0];
            var fmt = t.Format ?? "";
            if (!string.IsNullOrWhiteSpace(t.FormatProfile) && t.FormatProfile != "None")
                fmt += "-" + t.FormatProfile;
            return string.IsNullOrWhiteSpace(fmt) ? "音频" : fmt;
        }
    }

    public string SubtitleSummary => TextCount > 0 ? $"内封字幕 · {TextCount}" : "无字幕";

    /// <summary>音轨里出现过的语言，用于「国语/英语」这类摘要。</summary>
    public string LanguageSummary
    {
        get
        {
            var langs = AudioTracks
                .Select(a => a.LanguageLabel)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();
            return langs.Count == 0 ? "" : string.Join(" / ", langs!);
        }
    }

    /// <summary>详情页那排 chips：4K UHD、HDR10、HEVC · 10BIT、DTS-HD MA、内封字幕 · 3</summary>
    public IEnumerable<string> SpecChips()
    {
        yield return ResolutionLabel;
        if (HdrLabel is not null) yield return HdrLabel;
        yield return CodecLabel;
        if (AudioTracks.Count > 0) yield return AudioSummary;
        if (TextCount > 0) yield return SubtitleSummary;
    }
}

/// <summary>规格表里的一行：左字段名，右值。</summary>
public sealed record SpecRow(string Label, string Value)
{
    public bool HasValue => !string.IsNullOrWhiteSpace(Value) && Value != "—";
}
