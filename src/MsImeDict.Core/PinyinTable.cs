using System.Reflection;
using System.Text;

namespace MsImeDict.Core;

/// <summary>
/// 汉字 -> 读音表。内嵌 pinyin-data 的 pinyin.txt（MIT）。
/// 每字可能有多个读音，全拼时按顺序尝试。
/// </summary>
public sealed class PinyinTable
{
    private static readonly Lazy<Dictionary<char, string[]>> _map = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyDictionary<char, string[]> Map => _map.Value;

    public static string[] Readings(char c)
        => _map.Value.TryGetValue(c, out var r) ? r : Array.Empty<string>();

    public static bool Has(char c) => _map.Value.ContainsKey(c);

    private static Dictionary<char, string[]> Load()
    {
        var map = new Dictionary<char, string[]>(18000);
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("MsImeDict.Core.Resources.pinyin.txt");
        if (stream is null) return map;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var codePoint = line[..colon].Trim();
            if (!codePoint.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(codePoint.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int cp))
                continue;

            var rest = line[(colon + 1)..];
            int hash = rest.IndexOf('#');
            if (hash >= 0) rest = rest[..hash];
            rest = rest.Trim();
            if (rest.Length == 0) continue;

            var readings = rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Select(StripTone)
                              .Where(s => s.Length > 0)
                              .Distinct(StringComparer.Ordinal)
                              .ToArray();
            if (readings.Length == 0) continue;
            map[(char)cp] = readings;
        }
        return map;
    }

    private static readonly Dictionary<char, string> ToneMap = new()
    {
        ['ā'] = "a", ['á'] = "a", ['ǎ'] = "a", ['à'] = "a",
        ['ē'] = "e", ['é'] = "e", ['ě'] = "e", ['è'] = "e",
        ['ī'] = "i", ['í'] = "i", ['ǐ'] = "i", ['ì'] = "i",
        ['ō'] = "o", ['ó'] = "o", ['ǒ'] = "o", ['ò'] = "o",
        ['ū'] = "u", ['ú'] = "u", ['ǔ'] = "u", ['ù'] = "u",
        ['ǖ'] = "v", ['ǘ'] = "v", ['ǚ'] = "v", ['ǜ'] = "v",
        ['ü'] = "v",
        ['ń'] = "n", ['ň'] = "n", ['ǹ'] = "n",
        ['ḿ'] = "m",
        ['ê'] = "e",
        ['ɑ'] = "a", ['ɔ'] = "o",
    };

    /// <summary>去声调，并把 ü 折成 v（微软拼音用 v 表示 ü）。</summary>
    public static string StripTone(string pinyin)
    {
        var sb = new StringBuilder(pinyin.Length);
        foreach (var ch in pinyin)
            sb.Append(ToneMap.TryGetValue(ch, out var r) ? r : ch.ToString());
        return sb.ToString();
    }
}
