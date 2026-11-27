using System.Text;
using Microsoft.Data.Sqlite;
using MsImeDict.Core;

namespace MsImeDict.Tests;

/// <summary>零依赖的自检程序。任一用例失败则退出码非 0。</summary>
public static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();
    private static string _tempHome = "";

    public static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        // 全部用例跑在临时目录里：不碰真实词库、不往同步目录写变更文件
        _tempHome = Path.Combine(Path.GetTempPath(), $"msimdict-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("MSIMDICT_HOME", _tempHome);

        try
        {
            Run("格式往返", RoundTrip);
            Run("按编码升序排列", SortedOnWrite);
            Run("UTF-16 终止符陷阱", TerminatorTrap);
            Run("拒绝损坏文件", RejectsCorrupt);
            Run("长度与候选位边界", Boundaries);
            Run("全拼只产出一条编码", FullPinyinOnly);
            Run("简拼生成规则", AbbreviationRules);
            Run("词条唯一：换编码也拒绝", WordIsUnique);
            Run("取词来源如实记录", SourceRecorded);
            Run("设置同步不带候选位", PortableSettingsNoRank);
            Run("删除后同名词可重新添加", ReAddAfterDelete);
            Run("投影展开为全拼 + 简拼", ProjectExpands);
            Run("部署：同码冲突时全拼优先", DeployConflictPrefersFull);
            Run("部署：同一个词不出现两条", DeployNoDuplicateWord);
            Run("老库升级：收敛重复并回填简拼", UpgradeOldDatabase);
            Run("迁移幂等：连开两次不丢数据", MigrationIdempotent);
            Run("变更重放：rev 大者生效", ReplayWins);
            Run("跨设备同名词合并：只留更新时间新的", MergeSameWord);

            Console.WriteLine();
            Console.WriteLine($"通过 {_passed} 项，失败 {Failures.Count} 项");
            foreach (var f in Failures) Console.WriteLine("  ✗ " + f);
            return Failures.Count == 0 ? 0 : 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSIMDICT_HOME", null);
            try { if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, true); } catch { }
        }
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"  ✓ {name}");
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  ✗ {name} —— {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string TempDb() => Path.Combine(_tempHome, $"t-{Guid.NewGuid():N}.db");

    // ---------- 文件格式 ----------

    private static void RoundTrip()
    {
        var entries = new List<PhraseEntry>
        {
            new("skmy", "闪狂魔药", 2),
            new("dabingnang", "龘靐齉", 1),
            new("woceshi", "我测试", 1)
        };
        var bytes = Mschxudp.Serialize(entries);
        var back = Mschxudp.Parse(bytes);

        Assert(back.Count == 3, $"条数不符：{back.Count}");
        foreach (var e in entries)
        {
            var hit = back.FirstOrDefault(b => b.Code == e.Code);
            Assert(hit is not null, $"缺少 {e.Code}");
            Assert(hit!.Word == e.Word, $"{e.Code} 词面不符：{hit.Word} != {e.Word}");
            Assert(hit.Order == e.Order, $"{e.Code} 候选位不符：{hit.Order} != {e.Order}");
        }
        Assert(Mschxudp.Validate(bytes).Count == 0, "往返结果未通过体检");
    }

    private static void SortedOnWrite()
    {
        var entries = new List<PhraseEntry>
        {
            new("zzz", "甲", 1),
            new("aaa", "乙", 1),
            new("mmm", "丙", 1)
        };
        var back = Mschxudp.Parse(Mschxudp.Serialize(entries));
        var codes = back.Select(e => e.Code).ToList();
        Assert(codes.SequenceEqual(new[] { "aaa", "mmm", "zzz" }),
            "未按编码升序：" + string.Join(",", codes));
    }

    private static void TerminatorTrap()
    {
        // 编码以 ASCII 结尾时，其高字节 0x00 会与终止符 00 00 连成 00 00，
        // 扫描式解析会提前一个字节截断。这里验证按长度字段定位是正确的。
        var entries = new List<PhraseEntry>
        {
            new("skmy", "闪狂魔药", 2),
            new("wo", "我", 1)
        };
        var back = Mschxudp.Parse(Mschxudp.Serialize(entries));
        Assert(back.Any(e => e.Code == "skmy" && e.Word == "闪狂魔药"), "词面被截断");
        Assert(back.Any(e => e.Code == "wo" && e.Word == "我"), "编码被截断");
    }

    private static void RejectsCorrupt()
    {
        var good = Mschxudp.Serialize(new List<PhraseEntry> { new("abc", "甲乙", 1) });

        var badMagic = (byte[])good.Clone();
        badMagic[0] = (byte)'X';
        Assert(Mschxudp.Validate(badMagic).Count > 0, "魔数错误未被检出");

        var badCount = (byte[])good.Clone();
        badCount[0x1C] = 9;
        Assert(Mschxudp.Validate(badCount).Count > 0, "条目数错误未被检出");

        var truncated = good.Take(good.Length - 10).ToArray();
        Assert(Mschxudp.Validate(truncated).Count > 0, "截断文件未被检出");
    }

    private static void Boundaries()
    {
        var tooLongCode = new PhraseEntry(new string('a', 33), "甲", 1);
        Assert(Throws(() => Mschxudp.Serialize(new List<PhraseEntry> { tooLongCode })), "超长编码未被拒绝");

        var tooLongWord = new PhraseEntry("abc", new string('甲', 65), 1);
        Assert(Throws(() => Mschxudp.Serialize(new List<PhraseEntry> { tooLongWord })), "超长词面未被拒绝");

        var badOrder = new PhraseEntry("abc", "甲", 12);
        Assert(Throws(() => Mschxudp.Serialize(new List<PhraseEntry> { badOrder })), "越界候选位未被拒绝");

        var badChar = new PhraseEntry("ab1", "甲", 1);
        Assert(Throws(() => Mschxudp.Serialize(new List<PhraseEntry> { badChar })), "非法编码字符未被拒绝");

        var dup = new List<PhraseEntry> { new("abc", "甲", 1), new("abc", "乙", 2) };
        Assert(Throws(() => Mschxudp.Serialize(dup)), "重复编码未被拒绝");
    }

    // ---------- 编码 ----------

    private static void FullPinyinOnly()
    {
        var enc = new PinyinEncoder();

        var a = enc.Encode("闪狂魔药");
        Assert(a.Full == "shankuangmoyao", $"全拼错误：{a.Full}");
        Assert(a.CandidateCodes.Count == 1, $"应当只产出 1 条编码，实际 {a.CandidateCodes.Count}");
        Assert(a.CandidateCodes[0] == "shankuangmoyao", $"编码不是全拼：{a.CandidateCodes[0]}");

        var b = enc.Encode("我测试");
        Assert(b.Full == "woceshi", $"全拼错误：{b.Full}");
        Assert(b.CandidateCodes.Count == 1, "多字词应当只产出 1 条编码");

        var d = enc.Encode("Claude Code");
        Assert(d.Full == "claudecode", $"英文处理错误：{d.Full}");
    }

    private static void AbbreviationRules()
    {
        Assert(PinyinEncoder.AbbreviationOf("智睿瑞") == "zrr", "三字词简拼错误");
        Assert(PinyinEncoder.AbbreviationOf("飞智") == "fz", "两字词简拼错误");
        Assert(PinyinEncoder.AbbreviationOf("闪狂魔药") == "skmy", "四字词简拼错误");
        Assert(PinyinEncoder.AbbreviationOf("龘靐齉") == "dbn", "生僻字简拼错误");

        // 单字词的简拼只有一个字母，会把整个字母的候选表顶掉，不给
        Assert(PinyinEncoder.AbbreviationOf("我") == "", "单字词不该生成简拼");

        // 英文整串算一个部分，不生成简拼（中文才按字取首字母）
        Assert(PinyinEncoder.AbbreviationOf("Claude Code") == "", "英文词条不该生成简拼");

        // 乌 = wu，首字母是 w（拼音没有以 u 开头的音节）
        Assert(PinyinEncoder.AbbreviationOf("乌云") == "wy", "wu/yun 简拼错误");
    }

    // ---------- 母库 ----------

    private static void WordIsUnique()
    {
        var db = TempDb();
        using var store = new PhraseStore(db);

        var first = store.Add("智睿瑞", "zhiruirui", 1, "cli");
        var second = store.Add("智睿瑞", "zrr", 1, "cli");
        var third = store.Add(" 智睿瑞 ", "zrrr", 1, "cli");   // 前后空格归一化后是同一个词

        Assert(store.List().Count == 1, $"同一个词入库了 {store.List().Count} 条");
        Assert(second.Id == first.Id, "重复添加返回了不同的 id");
        Assert(third.Id == first.Id, "归一化后仍被当成新词");
        Assert(first.Abbr == "zrr", $"简拼未生成：'{first.Abbr}'");
    }

    /// <summary>划词加的词标成 selection，热键加的标成 clipboard，别混。</summary>
    private static void SourceRecorded()
    {
        var db = TempDb();
        using var store = new PhraseStore(db);

        store.Add("换电脑", "huandiannao", 1, "selection");
        store.Add("耗命", "haoming", 1, "clipboard");

        var list = store.List();
        Assert(list.Single(p => p.Text == "换电脑").Source == "selection", "划词来源被写成了别的");
        Assert(list.Single(p => p.Text == "耗命").Source == "clipboard", "热键来源被写成了别的");

        // 候选位恒为 1，投影到输入法也是 1
        var entries = store.Project();
        Assert(entries.All(e => e.Order == 1), "候选位不是 1：" +
            string.Join(",", entries.Select(e => $"{e.Code}={e.Order}")));
    }

    /// <summary>候选位不是可配置项，不能跟着设置同步到别的设备。</summary>
    private static void PortableSettingsNoRank()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new PortableSettings
        {
            Hotkey = "Ctrl+Q",
            AutoDeploy = true,
            UpdatedAt = 1
        });

        Assert(!json.Contains("defaultRank", StringComparison.OrdinalIgnoreCase),
            "可同步设置里还带着候选位：" + json);
        Assert(json.Contains("hotkey", StringComparison.OrdinalIgnoreCase), "快捷键丢了：" + json);
    }

    private static void ReAddAfterDelete()
    {
        var db = TempDb();
        using var store = new PhraseStore(db);

        var first = store.Add("耗命", "haoming", 1, "cli");
        Assert(store.Remove(first.Id), "删除失败");
        Assert(store.List().Count == 0, "删除后仍在列表里");

        // 墓碑不应挡住同名词重新加入
        var again = store.Add("耗命", "haoming", 1, "cli");
        Assert(again.Id != first.Id, "重新添加应当是一条新记录");
        Assert(store.List().Count == 1, $"重新添加后有 {store.List().Count} 条");
    }

    private static void ProjectExpands()
    {
        var db = TempDb();
        using var store = new PhraseStore(db);
        store.Add("智睿瑞", "zhiruirui", 1, "cli");

        var entries = store.Project();
        Assert(entries.Count == 2, $"应当展开成 2 条，实际 {entries.Count}");

        var full = entries.Single(e => e.Code == "zhiruirui");
        Assert(full.Word == "智睿瑞" && full.Order == 1 && !full.IsAbbr, "全拼条目不对");

        var abbr = entries.Single(e => e.Code == "zrr");
        Assert(abbr.Word == "智睿瑞" && abbr.Order == 1 && abbr.IsAbbr, "简拼条目不对");

        // 单字词不展开简拼
        store.Add("我", "wo", 1, "cli");
        var withSingle = store.Project();
        Assert(withSingle.Count == 3, $"单字词不应展开简拼，实际 {withSingle.Count} 条");
    }

    // ---------- 部署 ----------

    private static void DeployConflictPrefersFull()
    {
        var temp = Path.Combine(_tempHome, $"conflict-{Guid.NewGuid():N}.lex");
        try
        {
            var deployer = new ImeDeployer { LexPath = temp };

            // 「中国」的简拼 zg，与另一个词「紫光」的全拼 zg 撞车
            var entries = new List<PhraseEntry>
            {
                new("zg", "中国", 1, isAbbr: true),
                new("zg", "紫光", 1)
            };

            var (preview, result) = deployer.Preview(entries);
            Assert(result.Ok, "预览失败：" + result.Message);
            Assert(preview.Count == 1, $"同码应当只留一条，实际 {preview.Count}");
            Assert(preview[0].Word == "紫光", "全拼应当优先于简拼");
            Assert(result.Problems.Any(p => p.Contains("冲突")), "冲突未报告给用户");
        }
        finally { Delete(temp); }
    }

    private static void DeployNoDuplicateWord()
    {
        var temp = Path.Combine(_tempHome, $"nodup-{Guid.NewGuid():N}.lex");
        try
        {
            var db = TempDb();
            using var store = new PhraseStore(db);
            store.Add("智睿瑞", "zhiruirui", 1, "cli");
            store.Add("飞智", "feizhi", 1, "cli");

            var deployer = new ImeDeployer { LexPath = temp };
            var result = deployer.Deploy(store.Project());
            Assert(result.Ok, "部署失败：" + result.Message);

            var after = Mschxudp.Parse(File.ReadAllBytes(temp));
            Assert(after.Count == 4, $"应当写入 4 条（2 词 × 全拼 + 简拼），实际 {after.Count}");

            // 每条编码都唯一，同一个词不重复出现
            Assert(after.Select(e => e.Code).Distinct().Count() == after.Count, "存在重复编码");
            foreach (var word in new[] { "智睿瑞", "飞智" })
                Assert(after.Count(e => e.Word == word) == 2, $"「{word}」的编码条数不对");
        }
        finally { Delete(temp); }
    }

    // ---------- 升级 ----------

    /// <summary>按升级前的表结构造一个老库：没有 text_key / abbr，同一个词多行，还有墓碑。</summary>
    private static void UpgradeOldDatabase()
    {
        var db = TempDb();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE phrases (
  id TEXT PRIMARY KEY, text TEXT NOT NULL, code TEXT NOT NULL,
  rank INTEGER NOT NULL DEFAULT 2, enabled INTEGER NOT NULL DEFAULT 1,
  source TEXT NOT NULL DEFAULT 'manual', created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL, rev INTEGER NOT NULL DEFAULT 1,
  deleted INTEGER NOT NULL DEFAULT 0
);
INSERT INTO phrases VALUES('a1','智睿瑞','zhiruirui',2,1,'clipboard',100,100,1,0);
INSERT INTO phrases VALUES('a2','智睿瑞','zrr',2,1,'clipboard',101,101,1,0);
INSERT INTO phrases VALUES('a3','飞智','fz',2,1,'clipboard',102,102,1,0);
INSERT INTO phrases VALUES('a4','飞智','feizhi',2,1,'clipboard',103,103,1,0);
INSERT INTO phrases VALUES('a5','闪狂魔药','shankuangmoyao',2,1,'manual',104,104,2,1);
";
            cmd.ExecuteNonQuery();
        }

        using (var store = new PhraseStore(db))
        {
            var list = store.List();
            Assert(list.Count == 2, $"升级后应当剩 2 个词，实际 {list.Count}");
            Assert(list.All(p => p.Code == "zhiruirui" || p.Code == "feizhi"),
                "没有保留全拼编码：" + string.Join(",", list.Select(p => p.Code)));
            Assert(list.All(p => p.Abbr.Length > 0), "简拼没有回填");
            Assert(list.Single(p => p.Text == "智睿瑞").Abbr == "zrr", "智睿瑞的简拼不对");
            Assert(list.Single(p => p.Text == "飞智").Abbr == "fz", "飞智的简拼不对");
        }
    }

    private static void MigrationIdempotent()
    {
        var db = TempDb();

        string Snapshot()
        {
            using var store = new PhraseStore(db);
            return string.Join("|", store.List().OrderBy(p => p.Id)
                .Select(p => $"{p.Id}:{p.Text}:{p.Code}:{p.Abbr}:{p.Rank}"));
        }

        using (var store = new PhraseStore(db))
        {
            store.Add("智睿瑞", "zhiruirui", 1, "cli");
            store.Add("飞智", "feizhi", 1, "cli");
            store.Add("耗命", "haoming", 1, "cli");
        }

        var first = Snapshot();
        var second = Snapshot();
        var third = Snapshot();

        Assert(first == second && second == third, "多次打开数据库后数据发生了变化");
        Assert(first.Split('|').Length == 3, "词条数量不对");
    }

    // ---------- 同步 ----------

    private static void ReplayWins()
    {
        var db = TempDb();
        using var store = new PhraseStore(db);

        store.ApplyChange(new ChangeRecord
        {
            Id = "x1", Rev = 1, Op = "upsert", Text = "智睿瑞", Code = "zhiruirui",
            Abbr = "zrr", Rank = 1, Enabled = true, Ts = 100, Dev = "devA"
        });
        store.ApplyChange(new ChangeRecord
        {
            Id = "x1", Rev = 2, Op = "upsert", Text = "智睿瑞", Code = "zhiruirui",
            Abbr = "zrr", Rank = 3, Enabled = true, Ts = 200, Dev = "devB"
        });
        Assert(store.List().Single().Rank == 3, "新 rev 没有覆盖旧 rev");

        // 旧 rev 迟到，不能把新数据顶回去
        store.ApplyChange(new ChangeRecord
        {
            Id = "x1", Rev = 1, Op = "upsert", Text = "智睿瑞", Code = "zhiruirui",
            Abbr = "zrr", Rank = 1, Enabled = true, Ts = 100, Dev = "devA"
        });
        Assert(store.List().Single().Rank == 3, "旧 rev 覆盖了新数据");
    }

    /// <summary>
    /// 两台机器各自加了同一个词，同步时会出现两条不同 id 的同名词。
    /// 合并规则要确定：留更新时间新的那条，另一位埋掉，且不能撞唯一索引。
    /// </summary>
    private static void MergeSameWord()
    {
        var db = TempDb();
        using var store = new PhraseStore(db);

        // 旧的那条
        store.ApplyChange(new ChangeRecord
        {
            Id = "devA", Rev = 1, Op = "upsert", Text = "换电脑", Code = "huandiannao",
            Abbr = "hdn", Rank = 1, Enabled = true, Ts = 100, Dev = "A", Source = "selection"
        });

        // 另一台设备后加的同一个词，编码不同
        store.ApplyChange(new ChangeRecord
        {
            Id = "devB", Rev = 1, Op = "upsert", Text = "换电脑", Code = "huandiannao",
            Abbr = "hdn", Rank = 1, Enabled = true, Ts = 200, Dev = "B", Source = "clipboard"
        });

        var live = store.List();
        Assert(live.Count == 1, $"同名词合并后应当只剩 1 条，实际 {live.Count}");
        Assert(live[0].Id == "devB", "没有保留更新时间较新的那条");

        // 迟到的旧变更不能再顶回来
        store.ApplyChange(new ChangeRecord
        {
            Id = "devA", Rev = 2, Op = "upsert", Text = "换电脑", Code = "huandiannao",
            Abbr = "hdn", Rank = 1, Enabled = true, Ts = 150, Dev = "A", Source = "selection"
        });
        Assert(store.List().Count == 1, "旧变更把同名词又加回来了");
        Assert(store.List()[0].Id == "devB", "旧变更把新的那条挤掉了");
    }

    // ---------- 工具 ----------

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (FormatException) { return true; }
        catch (ArgumentException) { return true; }
    }
}
