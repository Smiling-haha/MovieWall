using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MovieWall.Models;

namespace MovieWall.Services;

public readonly record struct ParsedName(string Title, int Year, bool LooksLikeEpisode);

/// <summary>
/// 从文件名里抠出「片名 + 年份」。
/// 这是整个刮削准确率的地基，规则写得比较细，但全部是纯函数，容易单测和调整。
///
/// 中文资源库的文件名普遍长这样：
///   [DBD-Raws][4K_HDR][蜘蛛侠：英雄远征][IMAX版][2160P][WebRip][HEVC-10bit][FLAC].mkv
///   [No.88]摩登时代 BD720P.mp4
///   [黑豹].Black.Panther.2018.BD.1080P.x265.10bit.DD5.1.英国双语.内封特效中英双字-FFansBD.mkv
/// 能不能刮到海报，全看这几行规则能不能把片名捞干净——脏名字搜出去只会换来一堆空结果，
/// 最后连累整队列被判定成限流。
/// </summary>
public static class FileNameParser
{
    // 年份前后不能紧邻数字，避免从 1080p / 2160p 里误取；
    // 也不能是「1920x1080」这种分辨率的前半截（后面跟 x 或 × 再跟数字就排除）。
    private static readonly Regex YearRx = new(
        @"(?<!\d)(19\d{2}|20\d{2})(?!\d)(?!\s*[x×X*]\s*\d)",
        RegexOptions.Compiled);

    private static readonly Regex EpisodeRx = new(
        @"(?i)\bS\d{1,2}\s*[._\- ]?\s*E\d{1,3}\b|第\s*\d{1,3}\s*[集话話期]|\bSeason\s*\d{1,2}\b",
        RegexOptions.Compiled);

    // 方括号里既可能是发布组/规格（[DBD-Raws] [2160P]），也可能是中文片名（[黑豹] [蜘蛛侠：英雄远征]），
    // 所以不能一律丢掉，得逐个判断。
    private static readonly Regex BracketRx = new(@"[\[【（(][^\]】）)]*[\]】）)]", RegexOptions.Compiled);

    // 「BD720P」「BD.1080P」这种粘连写法，必须在分词前拆开
    private static readonly Regex SquashRx = new(
        @"(?i)\b(bd|hd|web|dvd|blu|bluray|uhd|bdrip|brrip|webrip|hdtv|remux)[\s._-]?(\d{3,4}[pi])\b",
        RegexOptions.Compiled);

    // 中文资源常见「片源标记 + 字幕标记」粘连成一坨：「窗中女孩 BD中英双字」
    private static readonly Regex AttachedTagsRx = new(
        @"(?i)(bd|hd|web|dvd|hdrip|webrip|bluray)(中英双字|中英字幕|国语中字|中文字幕|简繁双字|简繁字幕|双字|中字|国语|中英)",
        RegexOptions.Compiled);

    // 「国英台粤四语」「国粤双语」这类多语言标记
    private static readonly Regex MultiLangRx = new(
        @"[国粤英日韩台]{2,6}[一二三四五六]?(?:语|双语|双字|中字|字幕)",
        RegexOptions.Compiled);

    // 结尾的 -FFansBD / -GROUP 之类发布组
    private static readonly Regex ReleaseGroupRx = new(@"-[A-Za-z0-9@]{3,}$", RegexOptions.Compiled);

    // 这些词只可能出现在发布信息里，出现在片名里几乎不可能
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        // 分辨率 / 画质
        "1080p","720p","2160p","480p","4k","8k","uhd","fhd","hd","sd","hq","2k",
        "hdr","hdr10","hdr10p","dv","dovi","dolby","vision","sdr","10bit","8bit","12bit","hi10p",
        // 片源 / 压制
        "bluray","blu","bdrip","brrip","bdremux","remux","webrip","webdl","web","hdtv","hdrip",
        "dvdrip","dvd","bd","vhs","camrip","hdcam","ts","tc","r5","iso","mkv","mp4","avi",
        "x264","x265","h264","h265","hevc","avc","av1","vp9","xvid","divx",
        "proper","repack","internal","limited","retail","readnfo","nfo","nfofix","dirfix",
        "extended","unrated","uncut","theatrical","directors","cut","imax","criterion",
        "remastered","restored","reissue","anniversary","web-dl","ddp5","dd5",
        // 音轨 / 字幕
        "aac","ac3","eac3","dts","dtshd","truehd","atmos","flac","mp3","opus","ddp","dd","lpcm","pcm",
        "ma","hdma","5","1","7","2","0","6","4","sub","subs","subbed","hardsub","softsub",
        "dub","dubbed","multi","dual","audio","chi","eng","jpn","japanese","english","chinese",
        // 中文发布标记
        "国语","粤语","英语","日语","韩语","中字","中英","中英双字","双语","简繁","简中","繁中",
        "简体","繁体","简体中文","繁體中文","中文字幕","特效字幕","内嵌","外挂","内封","外封",
        "高清","超清","蓝光","原盘","修复版","加长版","完整版","未删减","加长","重制版",
        "国配","台配","港配","日配","多语","双字","字幕","无水印","收藏版","典藏版",
        "合集","全集","高清版","正式版","抢先版","枪版","韩版","蓝光版","特效","内封特效",
        "英国双语","英国","双语版","中英特效"
    };

    // 方括号内容里出现这些字/词，就说明它不是片名
    private static readonly string[] BracketNoiseHints =
    {
        "版", "语", "字幕", "压制", "字幕组", "www", "http", ".com", ".tv", ".net", ".org",
        "rip", "raws", "raw", "组", "制作", "发布", "转载", "电影网"
    };

    public static bool IsLikelyEpisode(string fileName)
        => EpisodeRx.IsMatch(Path.GetFileNameWithoutExtension(fileName) ?? fileName);

    public static ParsedName Parse(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)) stem = fileName;

        var looksLikeEpisode = EpisodeRx.IsMatch(stem);

        // 0) 粘连标记拆开
        stem = SquashRx.Replace(stem, " ");
        stem = AttachedTagsRx.Replace(stem, " ");
        stem = MultiLangRx.Replace(stem, " ");
        stem = ReleaseGroupRx.Replace(stem, " ");

        // 1) 方括号：像片名的留下当候选，像发布信息的丢掉
        var bracketTitles = new List<string>();
        var withoutBrackets = BracketRx.Replace(stem, m =>
        {
            // 只有 [] 和 【】 里可能是片名。圆括号里几乎一定是备注：
            // 「2号屏1（循环）」「(1994)」「(国语)」——收进来反而会把真片名挤掉，
            // 之前「2号屏1（循环）.mov」就被解析成了片名「循环」。
            if (m.Value[0] is '[' or '【')
            {
                var inner = m.Value.Trim('[', ']', '【', '】').Trim();
                if (LooksLikeBracketTitle(inner)) bracketTitles.Add(inner);
            }
            return " ";
        });

        // 方括号里的中文片名本身不带年份，年份只在主串里出现一次
        // （[复仇者联盟4.终局之战].Avengers.Endgame.**2019**.IMAX…）。
        // 候选里找不到年份时用它补，否则年份丢了，系列片匹配全靠猜。
        var globalYear = LastYear(stem);

        // 2) 候选排序：括号里的中文片名最准 → 去掉括号的正文 → 原始串兜底
        var candidates = new List<string>();
        candidates.AddRange(bracketTitles);
        if (!string.IsNullOrWhiteSpace(withoutBrackets)) candidates.Add(withoutBrackets);
        candidates.Add(stem);

        foreach (var raw in candidates)
        {
            var tokens = Tokenize(raw);
            if (tokens.Length == 0) continue;

            var (title, year) = FindTitleAndYear(tokens);
            if (string.IsNullOrWhiteSpace(title)) continue;

            if (year == 0) year = globalYear;

            // 清理后如果短得离谱（比如片名整个被噪声表吃掉），退回到未清理版本
            var cleaned = CleanTokens(title);
            var final = cleaned.Length >= 2 ? cleaned : title;

            var joined = TrimReleaseSuffix(SmartJoin(final));
            if (joined.Length < 2) continue;

            return new ParsedName(joined, year, looksLikeEpisode);
        }

        return new ParsedName(TrimReleaseSuffix(SmartJoin(stem)), globalYear, looksLikeEpisode);
    }

    // 中文发布标记常常**直接粘在片名屁股上**，中间没有空格也没有点：
    //   小鞋子内嵌中字 / 狮子王国语中字 / 天堂电影院剧场版 / 心灵捕手 BD720P
    // 分词那一步是按分隔符切的，粘成一坨就整串当一个词，噪声表永远命中不了，
    // 于是「小鞋子内嵌中字」整串拿去豆瓣搜，必然零结果——这类片子就永远没海报。
    // 所以最后再兜一道：反复从尾部削掉这些尾巴。
    private static readonly string[] ReleaseSuffixes =
    {
        "内嵌中字", "内嵌字幕", "内嵌简中", "国语中字", "粤语中字", "中英双字", "中英字幕",
        "简繁双字", "简繁字幕", "国粤双语", "国英双语", "中文字幕", "特效字幕", "内嵌特效",
        "导演剪辑版", "未删减版", "导演剪辑", "加长版", "修复版", "重制版", "完整版", "收藏版",
        "剧场版", "高清版", "蓝光版", "正式版", "国语版", "粤语版", "双语版", "特效版",
        "内嵌", "内封", "外挂", "中字", "双字", "字幕", "国语", "粤语", "双语", "国配", "台配",
        "高清", "超清", "蓝光", "原盘", "未删减", "加长", "重制", "修复", "无水印", "特效",
        "bd720p", "bd1080p", "bdrip", "webrip", "bluray", "remux", "hdtv", "hdrip", "dvdrip",
        "1080p", "720p", "2160p", "4k", "8k", "hdr", "x264", "x265", "h264", "h265", "hevc",
        "aac", "ac3", "dts", "ddp", "5 1", "7 1"
    };

    /// <summary>削掉尾巴上的中文/英文发布标记，直到削不动为止（「小鞋子内嵌中字BD720P」→「小鞋子」）。</summary>
    public static string TrimReleaseSuffix(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title ?? string.Empty;

        var s = title.Trim();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suf in ReleaseSuffixes)
            {
                if (s.Length <= suf.Length + 1) continue;   // 别把片名本身削没了（「国语」是片名时也要留）
                if (!s.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) continue;

                s = s[..^suf.Length].TrimEnd(' ', '-', '_', '.', '·', '[', ']', '【', '】', '（', '）', '(', ')');
                changed = true;
                break;
            }
        }
        return s;
    }

    /// <summary>
    /// 取文件名里最后一个像年份的 4 位数。
    /// 取最后一个是因为「银翼杀手2049.2017.1080p」这种串里，前面的 2049 是片名的一部分，
    /// 后面那个才是真正的发行年。
    /// </summary>
    private static int LastYear(string s)
    {
        var maxYear = DateTime.Now.Year + 2;
        int found = 0;
        foreach (System.Text.RegularExpressions.Match m in YearRx.Matches(s))
        {
            if (!int.TryParse(m.Value, out var y)) continue;
            if (y < 1900 || y > maxYear) continue;
            found = y;
        }
        return found;
    }

    /// <summary>
    /// 用当前规则重跑一批条目的标题。
    ///
    /// 只处理「没有海报、也没有刮削成功过」的记录——已经拿到海报的那些，
    /// 标题多半是豆瓣返回的正式名，比本地解析更准，不能覆盖。
    /// 旧规则会把发布信息一起吃进标题（「Captain America BD DD5 英国双语内封特效中英双字」），
    /// 这种名字拿去搜必然全空，于是那些片子永远停在没海报的状态。
    /// </summary>
    public static int ReparseStale(IEnumerable<Movie> movies)
    {
        Touched.Clear();
        int changed = 0;

        foreach (var m in movies)
        {
            if (m.HasPoster) continue;
            if (m.ScrapeState == ScrapeState.Success) continue;

            var fresh = Parse(m.FileName);
            if (string.IsNullOrWhiteSpace(fresh.Title)) continue;
            if (string.Equals(fresh.Title, m.Title, StringComparison.Ordinal)) continue;

            m.Title = fresh.Title;
            if (fresh.Year > 0) m.Year = fresh.Year;

            // 之前判了 Failed/Skipped 的，说明旧标题搜不到，给一次重试机会
            if (m.ScrapeState is ScrapeState.Failed or ScrapeState.Skipped)
                m.ScrapeState = ScrapeState.Pending;

            Touched.Add(m);          // 交给调用方回写数据库
            changed++;
        }

        return changed;
    }

    /// <summary>上一次 <see cref="ReparseStale"/> 改过标题的条目，调用方据此落库。</summary>
    public static List<Movie> Touched { get; } = new();

    /// <summary>方括号里到底是片名还是发布信息？只认「含中文、不含噪声词」的短串。</summary>
    private static bool LooksLikeBracketTitle(string inner)
    {
        if (inner.Length < 2 || inner.Length > 32) return false;
        if (!inner.Any(IsCjk)) return false;

        foreach (var hint in BracketNoiseHints)
            if (inner.Contains(hint, StringComparison.OrdinalIgnoreCase)) return false;

        var tokens = Tokenize(inner);
        return tokens.Length > 0 && CleanTokens(string.Join(' ', tokens)).Length >= 2;
    }

    private static string[] Tokenize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            // 点 / 下划线 / 各类连字符与空格一样当分隔符；中文标点保留在词内
            sb.Append(ch switch
            {
                '.' or '_' or '+' or '|' or '~' or '-' or '/' or '\\' => ' ',
                _ => ch
            });
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static (string title, int year) FindTitleAndYear(string[] tokens)
    {
        var maxYear = DateTime.Now.Year + 2;

        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i].Trim('-', ' ', '(', ')', '[', ']');
            if (t.Length != 4 || !int.TryParse(t, out var y)) continue;
            if (y < 1900 || y > maxYear) continue;

            // 特例：片名本身是年份（1917 / 2012），此时后面紧跟的那个才是发行年
            if (i == 0 && tokens.Length > 1)
            {
                var next = tokens[1].Trim('-', ' ', '(', ')', '[', ']');
                if (next.Length == 4 && int.TryParse(next, out var ny) && ny >= 1900 && ny <= maxYear)
                    continue;
            }

            var titleTokens = tokens[..i];
            return (string.Join(' ', titleTokens).Trim(), y);
        }

        // 没有年份：整串都当标题
        return (string.Join(' ', tokens).Trim(), 0);
    }

    private static string CleanTokens(string title)
    {
        var parts = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);

        foreach (var p in parts)
        {
            var bare = p.Trim('-', '.', '_', '(', ')', '[', ']', '【', '】', '、', '，', ',');
            if (bare.Length == 0) continue;

            // 纯数字（≥5 位）多半是 ID / 哈希
            if (bare.Length >= 5 && bare.All(char.IsDigit)) continue;

            if (Noise.Contains(bare)) continue;

            // 结尾带「版 / 语 / 字幕 / 组」的分词，基本都是发布信息
            if (bare.Length <= 8 && (bare.EndsWith("版") || bare.EndsWith("语") || bare.EndsWith("字幕")))
                continue;

            kept.Add(bare);
        }

        return string.Join(' ', kept);
    }

    /// <summary>中文之间不加空格，"The Matrix" 之间加空格。</summary>
    private static string SmartJoin(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return Regex.Replace(s, @"\s+", " ").Trim();

        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                bool leftCjk = EndsWithCjk(parts[i - 1]);
                bool rightCjk = StartsWithCjk(parts[i]);
                if (!(leftCjk && rightCjk)) sb.Append(' ');
            }
            sb.Append(parts[i]);
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim(' ', '-', '.', '_');
    }

    private static bool StartsWithCjk(string s) => s.Length > 0 && IsCjk(s[0]);
    private static bool EndsWithCjk(string s) => s.Length > 0 && IsCjk(s[^1]);

    private static bool IsCjk(char c)
        => (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF);
}
