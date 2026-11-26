using System.Security.Cryptography;
using System.Text;

namespace MsImeDict.Core;

/// <summary>部署结果。</summary>
public sealed class DeployResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public int EntryCount { get; set; }
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int KeptCount { get; set; }
    public string? BackupPath { get; set; }
    public List<string> Problems { get; set; } = new();
}

/// <summary>
/// 把母库投影成微软拼音的自定义短语库并落盘。
///
/// 流程固定六步，任一步失败即回滚：
///   1 读现有文件并解析（解析失败直接中止，不碰系统文件）
///   2 与母库合并（母库优先，用户在输入法里手工加的条目保留）
///   3 按编码升序排序（输入法要求，否则条目匹配不到）
///   4 序列化到同目录临时文件
///   5 回读临时文件做结构体检
///   6 备份原文件 -> 原子替换 -> 校验落盘结果
/// </summary>
public sealed class ImeDeployer
{
    public int MaxEntries { get; init; } = 2000;

    public string LexPath { get; init; } = Paths.LexFile;

    /// <summary>
    /// 是否保留输入法里已有、但母库没有的条目。
    ///
    /// 默认 false：母库是真相层，lex 是它的投影。
    /// 保留外来条目会导致「从词库删除后，输入法里还在」——实测踩过。
    /// </summary>
    public bool KeepForeignEntries { get; init; }

    /// <summary>只读部署：算出将要写入的内容但不落盘。</summary>
    public (List<PhraseEntry> Entries, DeployResult Result) Preview(IEnumerable<PhraseEntry> fromStore)
    {
        var result = new DeployResult { Ok = true };
        var existing = ReadExisting(result);
        if (existing is null) { result.Ok = false; return (new List<PhraseEntry>(), result); }

        var store = fromStore.ToList();
        var storeCodes = store.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);

        // 同一编码只能有一条：母库优先，其次（可选的）保留原有
        var byCode = new Dictionary<string, PhraseEntry>(StringComparer.Ordinal);
        if (KeepForeignEntries)
            foreach (var e in existing) byCode[e.Code] = e;
        foreach (var e in store) byCode[e.Code] = e;

        result.RemovedCount = existing.Count(e => !storeCodes.Contains(e.Code));

        var final = new List<PhraseEntry>();
        foreach (var e in byCode.Values.OrderBy(x => x.Code, StringComparer.Ordinal))
        {
            if (final.Count >= MaxEntries)
            {
                result.Problems.Add($"超出条数上限 {MaxEntries}，其余条目未写入");
                break;
            }
            if (e.Code.Length == 0) { result.Problems.Add($"跳过：编码为空（{e.Word}）"); continue; }
            if (e.Code.Length > Mschxudp.MaxCodeLength)
            {
                result.Problems.Add($"跳过：编码超过 {Mschxudp.MaxCodeLength} 字符（{e.Code}）");
                continue;
            }
            if (e.Word.Length == 0) { result.Problems.Add($"跳过：词面为空（{e.Code}）"); continue; }
            if (e.Word.Length > Mschxudp.MaxWordLength)
            {
                result.Problems.Add($"跳过：词面超过 {Mschxudp.MaxWordLength} 字符（{e.Code}）");
                continue;
            }
            final.Add(e);
        }

        result.EntryCount = final.Count;
        result.KeptCount = final.Count(e => storeCodes.Contains(e.Code));
        result.AddedCount = final.Count - result.KeptCount;
        return (final, result);
    }

    /// <summary>真正部署。失败时回滚到备份。</summary>
    public DeployResult Deploy(IEnumerable<PhraseEntry> fromStore)
    {
        var (entries, result) = Preview(fromStore);
        if (!result.Ok) return result;

        Directory.CreateDirectory(Path.GetDirectoryName(LexPath)!);

        byte[] payload;
        try { payload = Mschxudp.Serialize(entries); }
        catch (FormatException ex) { result.Ok = false; result.Message = ex.Message; return result; }

        // 体检
        var problems = Mschxudp.Validate(payload);
        if (problems.Count > 0)
        {
            result.Ok = false;
            result.Problems = problems;
            result.Message = "生成结果未通过结构体检，已放弃写入";
            return result;
        }

        // 备份原文件
        string? backup = null;
        if (File.Exists(LexPath))
        {
            Directory.CreateDirectory(Paths.BackupDir);
            backup = Path.Combine(Paths.BackupDir,
                $"ChsPinyinEUDPv1.lex.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
            File.Copy(LexPath, backup, overwrite: true);
        }

        // 写临时文件 -> 原子替换
        var tmp = LexPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, payload);
            File.Move(tmp, LexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            result.Ok = false;
            result.Message = $"写入失败（文件可能正被输入法占用，或目标只读）：{ex.Message}";
            return result;
        }

        // 落盘后校验
        var after = File.ReadAllBytes(LexPath);
        var afterProblems = Mschxudp.Validate(after);
        if (afterProblems.Count > 0)
        {
            result.Ok = false;
            result.Problems = afterProblems;
            result.Message = "落盘后校验未通过";
            Rollback(backup);
            return result;
        }

        result.BackupPath = backup;
        result.Message = $"已写入 {entries.Count} 条（母库 {result.KeptCount} 条 + 原有 {entries.Count - result.KeptCount} 条）";
        return result;
    }

    /// <summary>从备份恢复。</summary>
    public bool Rollback(string? backupPath)
    {
        if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath)) return false;
        File.Copy(backupPath, LexPath, overwrite: true);
        return true;
    }

    /// <summary>删除我们创建的文件，把输入法恢复到「从没有自定义短语」的状态。</summary>
    public bool RemoveCompletely()
    {
        if (!File.Exists(LexPath)) return false;
        File.Delete(LexPath);
        return true;
    }

    public List<PhraseEntry>? ReadExisting(DeployResult result)
    {
        if (!File.Exists(LexPath)) return new List<PhraseEntry>();
        try
        {
            var data = File.ReadAllBytes(LexPath);
            // 读现有文件用于合并时不检查排序：输入法自己的文件不保证有序，写出前会重排
            var problems = Mschxudp.Validate(data, checkOrdering: false);
            if (problems.Count > 0)
            {
                result.Ok = false;
                result.Problems = problems;
                result.Message = "现有词库文件结构异常，已中止部署（不会写入系统文件）";
                return null;
            }
            return Mschxudp.Parse(data);
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Message = $"读取现有词库失败：{ex.Message}";
            return null;
        }
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
