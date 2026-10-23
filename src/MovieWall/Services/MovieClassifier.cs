using System.Text.RegularExpressions;
using MovieWall.Models;

namespace MovieWall.Services;

/// <summary>
/// 「这东西到底是不是一部电影」的判定。
///
/// 硬盘上真正占多数的往往不是电影：手机录像、屏幕录制、宣传片、花絮、
/// 分集切片（01.mp4 02.mp4 …）、课程视频。它们拿去豆瓣搜必然全空，
/// 却会挤占刮削配额、把队列拖成长跑，还会让限流判定误伤真正的电影。
///
/// 判定刻意做得**保守**：只有拿到确凿证据（竖屏 / 时长太短 / 纯编号名 / 明确噪声词）
/// 才判「不是电影」，探测还没完成的条目一律先当电影看——
/// 宁可墙上多留一张没海报的卡，也不能把用户的片子藏起来。
/// </summary>
public static class MovieClassifier
{
    /// <summary>出现这些词，基本可以确定不是正片。</summary>
    private static readonly string[] NonFeatureWords =
    {
        "宣传片", "预告", "花絮", "录制", "片头", "片尾", "广告", "样片", "样张",
        "制作特辑", "幕后", "访谈", "彩蛋", "混剪", "教学", "演示", "屏保", "开机动画",
        "trailer", "teaser", "sample", "behind the scenes", "making of", "featurette",
        "录屏", "直播回放", "会议", "课程", "讲座",
        // 硬盘里那批口述史 / 民俗纪录 / 展演录像：片名就是个人名或一句说明，
        // 时长二三十分钟，豆瓣上根本不存在对应条目，会永远占着一张空卡。
        "后人", "口述", "专访", "实录", "纪录", "展演", "非遗", "传承人", "民俗",
        "教学片", "示范", "操作视频", "产品介绍"
    };

    /// <summary>哈希命名的片子：e31310cebb75d3c84db307995a385247.mp4 —— 一定是下载工具随手起的名字。</summary>
    private static readonly Regex HashNameRx = new(
        @"^[0-9a-f]{16,}(\s|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>分集编号：S01E10 / S01E10v2 / EP03 / 第3集 —— 剧集切片，豆瓣 TMDB 都查不到单集。</summary>
    private static readonly Regex EpisodeRx = new(
        @"^(S\s*\d{1,2}\s*[Ee]\s*\d{1,3}|EP?\s*\d{1,3}|第\s*\d+\s*[集话話期])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 展陈屏 / 广告机 / 监控的循环素材：待机画面、循环播放、几号屏、视频1、VID。
    /// 这一类在用户机器上成批出现（同一个工程导出几十个），
    /// 每一条都要花一次豆瓣往返，几批下来能把配额烧干净。
    /// </summary>
    private static readonly Regex ScreenLoopRx = new(
        @"(循环|待机|屏保|号屏|\bLOOP\b|\bLOOPING\b|^视频\s*\d|^VID$|^IMG\s*\d*$|^MOV$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>成人站点的水印词。这类文件名一定搜不到，只会在队列里耗时间。</summary>
    private static readonly string[] AdultWords =
    {
        "麻豆", "onlyfans", "blacked", "brazzers", "porn", "xxx", "uncensored",
        "无码", "有码", "私密", "大尺度", "性交", "失禁", "乳首", "口交", "自慰", "淫"
    };

    /// <summary>手机/相机录制的默认命名：VID_20251008_152909、IMG_1234、MOV_0001。</summary>
    private static readonly Regex CameraNameRx = new(
        @"^(VID|IMG|MOV|PXL|GOPR|DJI)[-_ ]?\d",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>纯编号：01 / 02 / S01E10 / 第03集 / 2024-11-12。</summary>
    private static readonly Regex PureNumberRx = new(
        @"^[\s\d\-_.:：集话話期第]+$",
        RegexOptions.Compiled);

    /// <summary>
    /// 用在刮削前：这条值不值得花一次网络请求。
    /// 探测已完成时判定更准；没探测过就只看名字。
    /// </summary>
    public static bool LooksLikeNonFeature(Movie m)
    {
        if (m.Duration is { } d && d.TotalMinutes > 0 && d.TotalMinutes < 12) return true;
        return NameIsNonFeature(m.DisplayTitle);
    }

    /// <summary>
    /// 只看名字判断。两处（刮削前 / 墙上过滤）共用同一套规则，避免两边判得不一样：
    /// 出现过「队列里跳过、墙上还显示」的尴尬。
    /// </summary>
    private static bool NameIsNonFeature(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.Trim().Length < 2) return true;

        foreach (var w in NonFeatureWords)
            if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var w in AdultWords)
            if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;

        if (PureNumberRx.IsMatch(name) && !LooksLikeNumberTitle(name)) return true;
        if (CameraNameRx.IsMatch(name)) return true;
        if (HashNameRx.IsMatch(name)) return true;
        if (EpisodeRx.IsMatch(name)) return true;
        if (ScreenLoopRx.IsMatch(name)) return true;

        return false;
    }

    /// <summary>
    /// 纯数字片名≤4 位：
    /// 「1917」「2012」「1984」都是正经电影，不能当分集编号吃掉；
    /// 「01」「02」「2024-11-12」才是切片和日期。
    /// </summary>
    private static bool LooksLikeNumberTitle(string name)
    {
        var t = name.Trim();
        if (t.Length != 4 || !t.All(char.IsDigit)) return false;
        return int.TryParse(t, out var v) && v is >= 1900 and <= 2035;
    }

    /// <summary>
    /// 刮削优先级，分数越高越先刮。
    ///
    /// 千万别用「有没有年份」当主排序。豆瓣 TOP250 那一批经典片
    /// （阿甘正传 / 肖申克的救赎 / 霸王别姬 …）的文件名往往就是干净的中文名，
    /// 一个数字都没有，按年份排序会把它们全压到队尾——
    /// 结果就是「漫威系列全都有海报了，一百多部经典片一张都没有」。
    /// 真正该优先的是「像一部完整电影」的特征：正片时长、干净的名字、中文名。
    /// </summary>
    public static int ScrapePriority(Movie m)
    {
        int score = 0;

        var title = m.DisplayTitle;
        if (!string.IsNullOrWhiteSpace(title) && title.Any(char.IsLetter)) score += 2;
        if (!string.IsNullOrWhiteSpace(title) && title.Any(IsCjk)) score += 2;
        if (m.Duration is { TotalMinutes: > 70 }) score += 3;
        if (m.Year > 1900) score += 1;

        // 十几分钟、无中文名、纯数字编号的，基本都是切片/录像，别占配额
        if (m.Duration is { TotalMinutes: < 20 }) score -= 4;

        return score;
    }

    private static bool IsCjk(char c)
        => (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF);

    /// <summary>
    /// 用在海报墙过滤：是否「明确不是电影」。
    /// 已经刮到元数据或已经有海报的，一律保留——宁可多留，不可错杀。
    /// </summary>
    public static bool IsDefinitelyNotMovie(Movie m)
    {
        if (m.HasPoster) return false;
        if (m.ScrapeState is ScrapeState.Success or ScrapeState.Ignored) return false;

        // 竖屏：手机拍的视频、短视频，不可能是电影
        if (m.VideoWidth > 0 && m.VideoHeight > 0 && m.VideoHeight > m.VideoWidth) return true;

        // 时长很短（广告、花絮、切片）
        if (m.Duration is { } d && d.TotalMinutes > 0 && d.TotalMinutes < 15) return true;

        if (NameIsNonFeature(m.DisplayTitle)) return true;

        // 已经用清理过的片名搜过、豆瓣就是没有这条 —— 与其在墙上留一张永远空的卡，
        // 不如收进「非影片」。真正的电影即使名字不干净，现在也会先被重新解析再搜，
        // 所以走到 Failed 这一步的基本都是录像/访谈/成人片。想找回来关掉「只看影片」即可。
        if (m.ScrapeState == ScrapeState.Failed) return true;

        return false;
    }
}
