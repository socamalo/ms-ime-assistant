using System.Text;

namespace MsImeDict.Core;

/// <summary>
/// 把词条文本转成微软拼音自定义短语可用的编码。
///
/// 微软拼音是按「你打的字」字面匹配自定义短语的，所以同一词条写两条最划算：
///   - 全拼（如 hanzishuru）：你打全拼音时命中
///   - 简拼（如 hzsr）：你打首字母时命中
/// </summary>
public sealed class PinyinEncoder
{
    /// <summary>
    /// 每个字取几个读音参与组合。
    ///
    /// 取 1（首选读音）的原因：多音字的次常用读音会生成没人会打的编码，
    /// 例如「已」的 sì 会产出 sijing...，「大」的 tài 会产出 tainao...，
    /// 结果是同一个词在输入法里挂三四条无用编码，还可能撞掉别人的码。
    /// 需要别的读音时，在词库管理界面手工加一条指定编码即可。
    /// </summary>
    public const int MaxReadingsPerChar = 1;

    /// <summary>组合数上限，防止长词 + 多读音时爆炸。</summary>
    public const int MaxCombinations = 8;

    /// <summary>一个词条最多产出多少个编码。</summary>
    public const int MaxCodes = 6;

    public sealed class Encoded
    {
        public string Full { get; init; } = "";      // 全拼，如 hanzishuru
        public string Abbr { get; init; } = "";      // 简拼，如 hzsr
        public IReadOnlyList<string> CandidateCodes { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }

    /// <summary>生成候选编码。全拼与简拼各取若干读音组合。</summary>
    public Encoded Encode(string text)
    {
        var notes = new List<string>();
        var parts = SplitParts(text);

        if (parts.Count == 0)
        {
            notes.Add("文本里没有可编码的内容");
            return new Encoded { Notes = notes };
        }

        // 每个字的所有读音。取不到读音的字整字丢弃，保证 parts 与 readingsPerPart 下标对齐
        var usableParts = new List<(string Text, bool IsChinese)>();
        var readingsPerPart = new List<string[]>();
        foreach (var part in parts)
        {
            string[] readings;
            if (part.IsChinese)
            {
                readings = PinyinTable.Readings(part.Text[0]);
                if (readings.Length == 0)
                {
                    notes.Add($"字「{part.Text}」没有读音数据，已跳过");
                    continue;
                }
            }
            else
            {
                readings = new[] { part.Text };
            }
            usableParts.Add(part);
            readingsPerPart.Add(readings);
        }

        if (readingsPerPart.Count == 0)
        {
            notes.Add("没有可编码的字");
            return new Encoded { Notes = notes };
        }

        var combos = ExpandCombinations(readingsPerPart);

        var full = new List<string>();
        var abbr = new List<string>();
        foreach (var combo in combos)
        {
            var f = string.Concat(combo);
            var a = string.Concat(combo.Select((s, i) =>
                usableParts[i].IsChinese ? s[..1] : usableParts[i].Text[..1]));
            if (!full.Contains(f)) full.Add(f);
            if (!abbr.Contains(a)) abbr.Add(a);
        }

        // 只产出全拼一条。
        //
        // 简拼不放在这里生成：母库里一个词只存一条全拼记录，
        // 简拼由 AbbreviationOf 单独算出来存到 abbr 列，
        // 部署时以第二条记录写进输入法（见 PhraseStore.Project）。
        // 这样母库是「一词一条」，输入法里是「一个词两条编码」。
        var hasChinese = usableParts.Any(p => p.IsChinese);
        var codes = new List<string>();
        foreach (var f in full)
        {
            if (f.Length <= Mschxudp.MaxCodeLength) codes.Add(f);
            else notes.Add($"全拼 '{f}' 超过 {Mschxudp.MaxCodeLength} 字符，丢弃");
        }

        // 纯英文/数字的词：全拼即原文
        _ = hasChinese;

        // u / v 开头会撞微软拼音的模式键
        var filtered = new List<string>();
        foreach (var c in codes)
        {
            if (c.Length > 0 && (c[0] == 'u' || c[0] == 'v'))
                notes.Add($"编码 '{c}' 以 u/v 开头，会与输入法模式键冲突，丢弃");
            else
                filtered.Add(c);
        }
        codes = filtered;

        // 多音字会放大编码数量，收敛到前 6 个
        if (codes.Count > MaxCodes)
        {
            notes.Add($"编码超过 {MaxCodes} 个，只保留前 {MaxCodes} 个");
            codes = codes.Take(MaxCodes).ToList();
        }

        if (codes.Count == 0) notes.Add("没有可用编码");

        return new Encoded
        {
            Full = full.FirstOrDefault() ?? "",
            Abbr = abbr.FirstOrDefault() ?? "",
            CandidateCodes = codes,
            Notes = notes
        };
    }

    /// <summary>
    /// 取词条的首字母简拼（每字一个字母），如「天安门」→ tam。
    ///
    /// 输入法不会自己把全拼条目映射到首字母，实测打 tam 只有系统词，
    /// 自定义词必须单独挂一条简拼码。返回空串表示这个词不配简拼：
    ///   - 单字词只有一个字母，会把整个字母的候选表顶掉
    ///   - u / v 开头会撞输入法模式键
    ///   - 有字取不到读音
    /// </summary>
    public static string AbbreviationOf(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var parts = SplitParts(text);
        if (parts.Count < 2) return "";

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (!part.IsChinese) { sb.Append(part.Text[0]); continue; }

            var readings = PinyinTable.Readings(part.Text[0]);
            if (readings.Length == 0) return "";
            sb.Append(readings[0][0]);
        }

        var abbr = sb.ToString();
        if (abbr.Length < 2 || abbr.Length > Mschxudp.MaxCodeLength) return "";

        // 与全拼同样的限制：u / v 开头会被输入法当成模式键
        if (abbr[0] == 'u' || abbr[0] == 'v') return "";

        return abbr;
    }

    private static List<(string Text, bool IsChinese)> SplitParts(string text)
    {
        var parts = new List<(string, bool)>();
        var latin = new StringBuilder();

        void FlushLatin()
        {
            if (latin.Length == 0) return;
            parts.Add((latin.ToString().ToLowerInvariant(), false));
            latin.Clear();
        }

        foreach (var ch in text)
        {
            if (ch >= '\u4E00' && ch <= '\u9FFF')
            {
                FlushLatin();
                parts.Add((ch.ToString(), true));
            }
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
            {
                latin.Append(ch);
            }
            else if (ch >= '0' && ch <= '9')
            {
                // 数字在编码字符集之外，丢弃
            }
            else
            {
                // 空格、标点等一律丢弃
            }
        }
        FlushLatin();
        return parts;
    }

    private static List<string[]> ExpandCombinations(List<string[]> readingsPerPart)
    {
        var combos = new List<string[]> { Array.Empty<string>() };
        foreach (var readings in readingsPerPart)
        {
            if (readings.Length == 0) continue;

            var allowed = readings.Take(MaxReadingsPerChar).ToArray();
            var next = new List<string[]>();
            foreach (var c in combos)
                foreach (var r in allowed)
                    next.Add(c.Concat(new[] { r }).ToArray());

            combos = next;
            if (combos.Count > MaxCombinations)
                combos = combos.Take(MaxCombinations).ToList();
        }
        return combos;
    }
}
