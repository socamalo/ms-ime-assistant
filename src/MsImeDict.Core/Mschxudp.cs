using System.Text;

namespace MsImeDict.Core;

/// <summary>
/// 一条「用户自定义短语」：编码 -> 词面，出现在候选框的指定位置。
/// </summary>
public sealed class PhraseEntry
{
    public string Code { get; set; } = "";
    public string Word { get; set; } = "";
    /// <summary>候选位置，1..9。</summary>
    public int Order { get; set; } = 1;

    public PhraseEntry() { }

    public PhraseEntry(string code, string word, int order = 1)
    {
        Code = code;
        Word = word;
        Order = order;
    }

    public override string ToString() => $"#{Order} {Code} -> {Word}";
}

/// <summary>
/// 微软拼音「用户自定义短语」文件 ChsPinyinEUDPv1.lex 的编解码器。
///
/// 格式细节见 docs/ime-format.md。要点：
///   - 魔数 mschxudp，0x40 字节头 + uint32 偏移表 + 顺序记录区
///   - 记录必须按编码升序排列，否则输入法匹配不到（实测）
///   - 编码长度由 +4 处的长度字段决定，不能靠扫描 00 00 找边界
///     （UTF-16LE 下 ASCII 高字节是 0x00，会提前命中终止符）
/// </summary>
public static class Mschxudp
{
    public const string Magic = "mschxudp";
    public const int HeaderSize = 0x40;
    public const int MaxCodeLength = 32;
    public const int MaxWordLength = 64;
    public const int MinOrder = 1;
    public const int MaxOrder = 9;

    private const uint RecordMagic = 0x00100010;
    private const byte FlagByte = 0x06;

    public static void Validate(PhraseEntry e)
    {
        if (string.IsNullOrEmpty(e.Code)) throw new FormatException("编码为空");
        if (string.IsNullOrEmpty(e.Word)) throw new FormatException($"编码 '{e.Code}' 的词面为空");
        if (e.Code.Length > MaxCodeLength)
            throw new FormatException($"编码 '{e.Code}' 超过 {MaxCodeLength} 字符");
        if (e.Word.Length > MaxWordLength)
            throw new FormatException($"词面 '{e.Word}' 超过 {MaxWordLength} 字符");
        if (e.Order < MinOrder || e.Order > MaxOrder)
            throw new FormatException($"候选位 {e.Order} 超出 {MinOrder}..{MaxOrder}");
        foreach (var ch in e.Code)
            if (!((ch >= 'a' && ch <= 'z') || ch == '\''))
                throw new FormatException($"编码 '{e.Code}' 含非法字符 '{ch}'（只允许 a-z 与 '）");
    }

    /// <summary>序列化为文件字节。写入前按编码升序排序。</summary>
    public static byte[] Serialize(IEnumerable<PhraseEntry> entries, uint? timestamp = null)
    {
        var list = entries.ToList();
        foreach (var e in list) Validate(e);

        var dup = list.GroupBy(e => e.Code, StringComparer.Ordinal)
                      .FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            throw new FormatException($"编码 '{dup.Key}' 重复（{dup.Count()} 条），同一编码只能有一条");

        // 输入法要求按编码升序，否则候选顺序会错乱、条目匹配不到
        list = list.OrderBy(e => e.Code, StringComparer.Ordinal).ToList();

        uint ts = timestamp ?? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var records = new List<byte[]>(list.Count);
        foreach (var e in list)
        {
            var codeBytes = Encoding.Unicode.GetBytes(e.Code);
            var wordBytes = Encoding.Unicode.GetBytes(e.Word);
            var head = new byte[16];
            WriteU32(head, 0, RecordMagic);
            WriteU16(head, 4, (ushort)(codeBytes.Length + 18));
            head[6] = (byte)e.Order;
            head[7] = FlagByte;
            WriteU32(head, 8, 0);
            WriteU32(head, 12, ts);
            records.Add(Concat(head, codeBytes, new byte[] { 0, 0 }, wordBytes, new byte[] { 0, 0 }));
        }

        int n = records.Count;
        int phraseStart = HeaderSize + 4 * n;

        var head2 = new byte[HeaderSize];
        Encoding.ASCII.GetBytes(Magic).CopyTo(head2, 0);
        head2[8] = 0x02; head2[9] = 0x00; head2[10] = 0x60; head2[11] = 0x00;
        head2[12] = 0x01; head2[13] = 0x00; head2[14] = 0x00; head2[15] = 0x00;
        WriteU32(head2, 0x10, HeaderSize);
        WriteU32(head2, 0x14, (uint)phraseStart);
        WriteU32(head2, 0x1C, (uint)n);
        WriteU32(head2, 0x20, ts);

        var table = new byte[4 * n];
        uint acc = 0;
        for (int i = 0; i < n; i++)
        {
            WriteU32(table, i * 4, acc);
            acc += (uint)records[i].Length;
        }

        var file = Concat(head2, table, Concat(records.ToArray()));
        WriteU32(file, 0x18, (uint)file.Length);
        return file;
    }

    /// <summary>解析文件字节。任何结构不一致都抛 FormatException。</summary>
    public static List<PhraseEntry> Parse(byte[] data)
    {
        if (data.Length < HeaderSize) throw new FormatException($"文件过短（{data.Length} 字节）");
        var magic = Encoding.ASCII.GetString(data, 0, 8);
        if (magic != Magic) throw new FormatException($"魔数不符：'{magic}'，期望 '{Magic}'");

        uint offsetStart = ReadU32(data, 0x10);
        uint phraseStart = ReadU32(data, 0x14);
        uint phraseEnd = ReadU32(data, 0x18);
        uint count = ReadU32(data, 0x1C);

        if (offsetStart + 4 * count != phraseStart)
            throw new FormatException(
                $"偏移表长度与记录区起点不符：0x{offsetStart:X} + 4×{count} != 0x{phraseStart:X}");
        if (phraseEnd != (uint)data.Length)
            throw new FormatException($"文件总长字段 {phraseEnd} 与实际长度 {data.Length} 不符");
        if (phraseStart + (phraseEnd - phraseStart) > (uint)data.Length)
            throw new FormatException("记录区越界");

        var result = new List<PhraseEntry>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint rel = ReadU32(data, (int)offsetStart + (int)(4 * i));
            int o = (int)(phraseStart + rel);
            uint nextRel = i + 1 < count ? ReadU32(data, (int)offsetStart + (int)(4 * (i + 1)))
                                          : phraseEnd - phraseStart;
            if (o + 16 > data.Length) throw new FormatException($"记录 {i} 越界");

            if (ReadU32(data, o) != RecordMagic)
                throw new FormatException($"记录 {i} 魔数不符");

            ushort lenField = ReadU16(data, o + 4);
            int codeBytes = lenField - 18;
            if (codeBytes < 0 || o + 16 + codeBytes + 2 > data.Length)
                throw new FormatException($"记录 {i} 编码长度字段非法：{lenField}");

            int termAt = o + 16 + codeBytes;
            if (data[termAt] != 0 || data[termAt + 1] != 0)
                throw new FormatException($"记录 {i} 编码未以 00 00 终止");

            var code = Encoding.Unicode.GetString(data, o + 16, codeBytes);

            // 词面边界：按 2 字节步进扫描终止符
            int w = termAt + 2;
            int e = w;
            while (e + 1 < data.Length && !(data[e] == 0 && data[e + 1] == 0)) e += 2;
            if (e + 1 >= data.Length) throw new FormatException($"记录 {i} 词面未终止");
            var word = Encoding.Unicode.GetString(data, w, e - w);

            int recLen = 16 + codeBytes + 2 + (e - w) + 2;
            if (recLen != (int)(nextRel - rel))
                throw new FormatException(
                    $"记录 {i} 长度与偏移表不符：实际 {recLen}，偏移表 {nextRel - rel}");

            result.Add(new PhraseEntry(code, word, data[o + 6]));
        }
        return result;
    }

    /// <summary>结构体检：解析 + 逐条校验 + 排序检查。返回问题列表，空列表表示健康。</summary>
    public static List<string> Validate(byte[] data)
    {
        var problems = new List<string>();
        List<PhraseEntry> entries;
        try { entries = Parse(data); }
        catch (FormatException ex) { return new List<string> { ex.Message }; }

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Code.Length > MaxCodeLength) problems.Add($"[{i}] 编码超长：{e.Code}");
            if (e.Word.Length > MaxWordLength) problems.Add($"[{i}] 词面超长：{e.Word}");
            if (e.Order < MinOrder || e.Order > MaxOrder) problems.Add($"[{i}] 候选位越界：{e.Order}");
            foreach (var ch in e.Code)
                if (!((ch >= 'a' && ch <= 'z') || ch == '\''))
                    problems.Add($"[{i}] 编码含非法字符 '{ch}'");
        }

        for (int i = 1; i < entries.Count; i++)
            if (string.CompareOrdinal(entries[i - 1].Code, entries[i].Code) > 0)
                problems.Add($"记录未按编码升序：[{i - 1}] '{entries[i - 1].Code}' > [{i}] '{entries[i].Code}'");

        var dup = entries.GroupBy(e => e.Code, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) problems.Add($"编码重复：'{dup.Key}'");

        return problems;
    }

    // ---- 小工具 ----
    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }
    private static void WriteU16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
    }
    private static uint ReadU32(byte[] b, int o)
        => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static byte[] Concat(params byte[][] parts)
    {
        int len = 0;
        foreach (var p in parts) len += p.Length;
        var r = new byte[len];
        int off = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, off, p.Length); off += p.Length; }
        return r;
    }
}
