using System.Text;

namespace MovieWall.Common;

/// <summary>片名匹配用的相似度。用 bigram Dice 系数，对中英文都工作，且比编辑距离更容忍词序差异。</summary>
public static class TextSimilarity
{
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    public static double Dice(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        // 包含关系要分方向看，这是刮削准确率的关键：
        //
        //   「雷神3」去搜，豆瓣会同时返回「雷神」(2011) 和「雷神3：诸神黄昏」(2017)。
        //   旧实现不分方向地给「包含」高分，短串一律占便宜 —— 「雷神」因为短，
        //   比例项反而更高，于是「雷神3」被配成了第一部，海报全错。
        //
        //   现在：候选比查询更完整（查询是它的前缀）→ 高分；
        //         候选比查询少东西（候选是查询的子串）→ 明显降分。
        bool wantInCand = x.Contains(y);   // 查询被候选包含：候选更完整
        bool candInWant = y.Contains(x);   // 候选被查询包含：候选信息不全

        if (wantInCand)
        {
            double ratio = (double)y.Length / x.Length;   // 查询占候选的比例
            return 0.80 + 0.20 * ratio;                   // 「雷神3」→「雷神3诸神黄昏」约 0.90
        }

        if (candInWant)
        {
            double ratio = (double)x.Length / y.Length;
            return 0.45 + 0.25 * ratio;                   // 「雷神」←「雷神3」约 0.62，需靠年份校正才过阈
        }

        if (x.Length < 2 || y.Length < 2) return x == y ? 1.0 : 0;

        var grams = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < x.Length - 1; i++)
        {
            var g = x.Substring(i, 2);
            grams[g] = grams.GetValueOrDefault(g) + 1;
        }

        int overlap = 0;
        for (int i = 0; i < y.Length - 1; i++)
        {
            var g = y.Substring(i, 2);
            if (grams.TryGetValue(g, out var n) && n > 0)
            {
                overlap++;
                grams[g] = n - 1;
            }
        }

        return 2.0 * overlap / ((x.Length - 1) + (y.Length - 1));
    }

    /// <summary>综合打分：片名相似度为主，年份作为强校正项。</summary>
    public static double Score(string? candidate, int candidateYear, string? wanted, int wantedYear)
    {
        var s = Dice(candidate, wanted);
        if (wantedYear > 0 && candidateYear > 0)
        {
            var diff = Math.Abs(candidateYear - wantedYear);
            s -= diff switch
            {
                0 => 0,
                1 => 0.12,
                2 => 0.22,
                _ => 0.38
            };
        }
        else if (wantedYear > 0 && candidateYear == 0)
        {
            s -= 0.10;
        }
        return s;
    }
}
