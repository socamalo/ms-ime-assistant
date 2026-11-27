using System.Text;
using MsImeDict.Core;

namespace MsImeDict.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return 0;
        }

        var cmd = args[0];
        var rest = args.Skip(1).ToArray();

        try
        {
            return cmd switch
            {
                "dump" => Dump(),
                "verify" => Verify(),
                "encode" => Encode(rest),
                "list" => ListCmd(),
                "add" => Add(rest),
                "remove" => Remove(rest),
                "preview" => Preview(),
                "deploy" => Deploy(),
                "export" => Export(rest),
                "backup" => Backup(),
                "rollback" => Rollback(rest),
                "clean" => Clean(),
                "sync" => Sync(),
                _ => Unknown(cmd)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令：{cmd}");
        Usage();
        return 1;
    }

    private static void Usage()
    {
        Console.WriteLine("""
        ms-ime-dict —— 微软拼音个人词库工具

          dump                      列出输入法当前的自定义短语
          verify                    对自定义短语文件做结构体检
          encode <文本>              只看会生成哪些编码
          list                      列出母库词条
          add <文本> [选项]           加入词库并部署
                 --code <编码>       指定编码（不给就自动生成）
                 --rank <1-9>       指定候选位，默认 2
                 --no-deploy         只入库，不写输入法
          remove <id>               删除词条并部署
          preview                   预览将要写入的内容
          deploy                    把母库投影写入输入法
          export <路径>              导出 .dat（可手动导入输入法）
          backup                    备份当前自定义短语文件
          rollback [备份路径]        回滚到指定备份（不给就用最近一份）
          clean                     删除自定义短语文件，回到「从没有短语」的状态
          sync                      把变更同步到私有仓库
        """);
    }

    // ---------- 查看 ----------

    private static int Dump()
    {
        var path = Paths.LexFile;
        if (!File.Exists(path))
        {
            Console.WriteLine($"文件不存在：{path}");
            Console.WriteLine("（该文件在第一次添加自定义短语时才会生成）");
            return 0;
        }
        var entries = Mschxudp.Parse(File.ReadAllBytes(path));
        Console.WriteLine($"{path}  共 {entries.Count} 条");
        Console.WriteLine();
        foreach (var e in entries)
            Console.WriteLine($"  候选位{e.Order}  {e.Code,-32} -> {e.Word}");
        return 0;
    }

    private static int Verify()
    {
        var path = Paths.LexFile;
        if (!File.Exists(path))
        {
            Console.WriteLine($"文件不存在：{path}");
            return 0;
        }
        var data = File.ReadAllBytes(path);
        var problems = Mschxudp.Validate(data);
        Console.WriteLine($"文件：{path}");
        Console.WriteLine($"大小：{data.Length} 字节   sha256：{ImeDeployer.Sha256(data)}");
        if (problems.Count == 0)
        {
            Console.WriteLine("体检结果：全部通过");
            return 0;
        }
        Console.WriteLine($"体检结果：{problems.Count} 个问题");
        foreach (var p in problems) Console.WriteLine("  - " + p);
        return 1;
    }

    private static int Encode(string[] rest)
    {
        if (rest.Length == 0) { Console.Error.WriteLine("用法：encode <文本>"); return 1; }
        var text = string.Join(' ', rest);
        var enc = new PinyinEncoder().Encode(text);
        Console.WriteLine($"文本：{text}");
        Console.WriteLine($"全拼：{enc.Full}");
        Console.WriteLine($"简拼：{enc.Abbr}");
        Console.WriteLine($"可用编码：{(enc.CandidateCodes.Count == 0 ? "（无）" : string.Join(", ", enc.CandidateCodes))}");
        foreach (var n in enc.Notes) Console.WriteLine("  ! " + n);
        return 0;
    }

    private static int ListCmd()
    {
        using var store = new PhraseStore();
        var list = store.List();
        Console.WriteLine($"母库：{store.DatabaseFile}  共 {list.Count} 条");
        Console.WriteLine();
        foreach (var p in list)
            Console.WriteLine($"  {p.Id}  候选位{p.Rank}  {p.Code,-24} -> {p.Text}   [{(p.Enabled ? "启用" : "停用")}]");
        return 0;
    }

    // ---------- 增删 ----------

    private static int Add(string[] rest)
    {
        var opts = ParseOptions(rest, out var positional);
        if (positional.Count == 0) { Console.Error.WriteLine("用法：add <文本> [--code x] [--rank n] [--no-deploy]"); return 1; }
        var text = string.Join(' ', positional);

        using var store = new PhraseStore();

        // 加词前先查一遍：同一个词不重复入库；显式给了 --rank 时顺手修正候选位
        var existing = store.FindByText(text);
        if (existing is not null)
        {
            if (opts.TryGetValue("--rank", out var wanted) && int.TryParse(wanted, out var newRank)
                && newRank != existing.Rank)
            {
                existing.Rank = Math.Clamp(newRank, Mschxudp.MinOrder, Mschxudp.MaxOrder);
                existing.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                existing.Rev++;
                store.ApplyChange(new ChangeRecord
                {
                    Id = existing.Id, Rev = existing.Rev, Op = "upsert", Text = existing.Text,
                    Code = existing.Code, Rank = existing.Rank, Enabled = existing.Enabled,
                    Source = existing.Source, Ts = existing.UpdatedAt, Dev = DeviceId.Value
                });
                Console.WriteLine($"「{text}」已在词典中，候选位改为 {existing.Rank}");
                return DoDeploy(store);
            }

            var existingCodes = string.Join("、", store.List()
                .Where(p => PhraseStore.NormalizeText(p.Text) == PhraseStore.NormalizeText(text))
                .Select(p => p.Code));
            Console.WriteLine($"「{text}」已在词典中，编码：{existingCodes}");
            return 0;
        }

        var codes = new List<string>();
        if (opts.TryGetValue("--code", out var explicitCode) && !string.IsNullOrWhiteSpace(explicitCode))
        {
            var code = explicitCode.Trim().ToLowerInvariant();
            if (!PhraseStore.IsValidCode(code))
            {
                Console.Error.WriteLine($"编码 '{code}' 不合法：只允许 a-z 和 '，长度 1..32，且不能以 u/v 开头");
                return 1;
            }
            codes.Add(code);
        }
        else
        {
            var enc = new PinyinEncoder().Encode(text);
            codes.AddRange(enc.CandidateCodes);
            foreach (var n in enc.Notes) Console.WriteLine("  ! " + n);
        }

        int rank = 1;
        if (opts.TryGetValue("--rank", out var rankText) && int.TryParse(rankText, out var r)) rank = r;

        var added = new List<Phrase>();
        foreach (var code in codes)
        {
            var sameCode = store.FindByCode(code);
            if (sameCode is not null)
            {
                Console.WriteLine($"  已存在同编码条目 {code} -> {sameCode.Text}，更新");
                sameCode.Text = text;
                sameCode.Rank = rank;
                sameCode.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                sameCode.Rev++;
                store.ApplyChange(new ChangeRecord
                {
                    Id = sameCode.Id, Rev = sameCode.Rev, Op = "upsert", Text = sameCode.Text,
                    Code = sameCode.Code, Rank = sameCode.Rank, Enabled = sameCode.Enabled,
                    Source = sameCode.Source, Ts = sameCode.UpdatedAt, Dev = DeviceId.Value
                });
                added.Add(sameCode);
                continue;
            }
            added.Add(store.Add(text, code, rank, source: "cli"));
            Console.WriteLine($"  已入库 {code} -> {text}  候选位{rank}");
        }

        if (opts.ContainsKey("--no-deploy")) return 0;
        return DoDeploy(store);
    }

    private static int Remove(string[] rest)
    {
        if (rest.Length == 0) { Console.Error.WriteLine("用法：remove <id>"); return 1; }
        using var store = new PhraseStore();
        if (!store.Remove(rest[0])) { Console.Error.WriteLine($"找不到词条 {rest[0]}"); return 1; }
        Console.WriteLine($"已删除 {rest[0]}");
        return DoDeploy(store);
    }

    // ---------- 部署 ----------

    private static int Preview()
    {
        using var store = new PhraseStore();
        var deployer = new ImeDeployer();
        var (entries, result) = deployer.Preview(store.Project());
        if (!result.Ok)
        {
            Console.Error.WriteLine(result.Message);
            foreach (var p in result.Problems) Console.Error.WriteLine("  - " + p);
            return 1;
        }
        Console.WriteLine($"将要写入 {entries.Count} 条（母库 {result.KeptCount} 条 + 原有 {entries.Count - result.KeptCount} 条）");
        foreach (var e in entries)
            Console.WriteLine($"  候选位{e.Order}  {e.Code,-32} -> {e.Word}");
        return 0;
    }

    private static int Deploy() => DoDeploy(new PhraseStore());

    private static int DoDeploy(PhraseStore store)
    {
        var deployer = new ImeDeployer();
        var result = deployer.Deploy(store.Project());
        Console.WriteLine(result.Message);
        foreach (var p in result.Problems) Console.Error.WriteLine("  - " + p);
        if (result.BackupPath is not null) Console.WriteLine($"备份：{result.BackupPath}");
        return result.Ok ? 0 : 1;
    }

    // ---------- 导出 / 备份 / 回滚 ----------

    private static int Export(string[] rest)
    {
        if (rest.Length == 0) { Console.Error.WriteLine("用法：export <路径.dat>"); return 1; }
        using var store = new PhraseStore();
        var entries = store.Project();
        var bytes = Mschxudp.Serialize(entries);
        File.WriteAllBytes(rest[0], bytes);
        Console.WriteLine($"已导出 {entries.Count} 条 -> {rest[0]}  ({bytes.Length} 字节)");
        Console.WriteLine("导入方式：设置 -> 时间和语言 -> 语言 -> 中文 -> 微软拼音选项 -> 词库和自学习 -> 添加或编辑自定义短语 -> 导入");
        return 0;
    }

    private static int Backup()
    {
        var path = Paths.LexFile;
        if (!File.Exists(path)) { Console.WriteLine("没有可备份的文件（自定义短语库尚不存在）"); return 0; }
        Directory.CreateDirectory(Paths.BackupDir);
        var dst = Path.Combine(Paths.BackupDir, $"ChsPinyinEUDPv1.lex.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
        File.Copy(path, dst, overwrite: true);
        Console.WriteLine($"已备份 -> {dst}");
        return 0;
    }

    private static int Rollback(string[] rest)
    {
        var deployer = new ImeDeployer();
        string? backup = rest.FirstOrDefault();
        if (backup is null || !File.Exists(backup))
        {
            if (!Directory.Exists(Paths.BackupDir)) { Console.Error.WriteLine("没有可用备份"); return 1; }
            backup = Directory.EnumerateFiles(Paths.BackupDir, "ChsPinyinEUDPv1.lex.*.bak")
                               .OrderByDescending(f => f).FirstOrDefault();
        }
        if (backup is null || !File.Exists(backup)) { Console.Error.WriteLine("没有可用备份"); return 1; }
        if (!deployer.Rollback(backup)) { Console.Error.WriteLine("回滚失败"); return 1; }
        Console.WriteLine($"已回滚到 {backup}");
        return 0;
    }

    private static int Clean()
    {
        var deployer = new ImeDeployer();
        if (deployer.RemoveCompletely())
        {
            Console.WriteLine("已删除自定义短语文件，输入法回到「从没有自定义短语」的状态");
            return 0;
        }
        Console.WriteLine("没有可删除的自定义短语文件");
        return 0;
    }

    private static int Sync()
    {
        var settings = ConfigFile.LoadSync();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.RepoPath))
        {
            Console.Error.WriteLine("同步未配置。在 settings.json 里设置 SyncEnabled=true、RepoPath、RemoteUrl。");
            Console.Error.WriteLine("settings 路径：" + Paths.SettingsFile);
            return 1;
        }

        using var store = new PhraseStore();
        var agent = new SyncAgent(store);
        var result = agent.Sync(settings);
        Console.WriteLine(result.Message);
        foreach (var step in result.Steps) Console.WriteLine("  - " + step);
        if (result.Pushed > 0 || result.Pulled > 0)
            Console.WriteLine($"  上传 {result.Pushed} 个变更文件，下载 {result.Pulled} 个");

        // 拉回来的词条要重新投影进输入法
        if (result.Ok && result.Pulled > 0)
        {
            var deploy = new ImeDeployer().Deploy(store.Project());
            Console.WriteLine(deploy.Ok
                ? $"  已把 {deploy.EntryCount} 条写入输入法"
                : $"  写入输入法失败：{deploy.Message}");
            foreach (var p in deploy.Problems) Console.WriteLine("    - " + p);
            if (!deploy.Ok) return 1;
        }

        return result.Ok ? 0 : 1;
    }

    // ---------- 参数解析 ----------

    private static Dictionary<string, string> ParseOptions(string[] args, out List<string> positional)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    opts[args[i]] = args[i + 1];
                    i++;
                }
                else opts[args[i]] = "true";
            }
            else positional.Add(args[i]);
        }
        return opts;
    }
}
