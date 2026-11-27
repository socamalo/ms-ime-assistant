using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MsImeDict.Core;

/// <summary>母库里的一条词条。一个词只有一条，全拼编码 + 首字母简拼都存在这一行。</summary>
public sealed class Phrase
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Code { get; set; } = "";

    /// <summary>首字母简拼，如「智睿瑞」→ zrr。空表示这个词不配简拼（单字词、u/v 开头等）。</summary>
    public string Abbr { get; set; } = "";

    public int Rank { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public string Source { get; set; } = "manual";
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public int Rev { get; set; } = 1;
    public bool Deleted { get; set; }
}

/// <summary>追加式变更日志的一行。</summary>
public sealed class ChangeRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("rev")] public int Rev { get; set; }
    [JsonPropertyName("op")] public string Op { get; set; } = "upsert";   // upsert | delete
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("abbr")] public string Abbr { get; set; } = "";
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("source")] public string Source { get; set; } = "manual";
    [JsonPropertyName("ts")] public long Ts { get; set; }
    [JsonPropertyName("dev")] public string Dev { get; set; } = "";
}

/// <summary>
/// 词条母库。sqlite 存运行时状态（从不进 git），
/// 同时写入追加式 JSONL 日志，用于推送到私有仓库实现跨设备同步。
/// </summary>
public sealed class PhraseStore : IDisposable
{
    private readonly SqliteConnection _conn;

    /// <summary>本库对应的 sqlite 文件路径。</summary>
    public string DatabaseFile { get; }

    public PhraseStore(string? dbPath = null)
    {
        Paths.EnsureDirectories();
        var path = dbPath ?? Paths.DatabaseFile;
        DatabaseFile = path;
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS phrases (
  id          TEXT PRIMARY KEY,
  text        TEXT NOT NULL,
  text_key    TEXT,
  code        TEXT NOT NULL,
  abbr        TEXT NOT NULL DEFAULT '',
  rank        INTEGER NOT NULL DEFAULT 1,
  enabled     INTEGER NOT NULL DEFAULT 1,
  source      TEXT NOT NULL DEFAULT 'manual',
  created_at  INTEGER NOT NULL,
  updated_at  INTEGER NOT NULL,
  rev         INTEGER NOT NULL DEFAULT 1,
  deleted     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_phrases_code ON phrases(code);
";
        cmd.ExecuteNonQuery();

        MigrateTextKey();
        MigrateAbbr();
    }

    /// <summary>
    /// 补 abbr 列并回填简拼。
    ///
    /// 老库只有全拼编码，输入法里打首字母命中不了自定义词，
    /// 这里给每条活记录算出首字母简拼补上。
    /// </summary>
    private void MigrateAbbr()
    {
        var hasColumn = false;
        using (var check = _conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(phrases)";
            using var r = check.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), "abbr", StringComparison.OrdinalIgnoreCase))
                    hasColumn = true;
        }

        if (!hasColumn)
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE phrases ADD COLUMN abbr TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
        }

        var needFill = new List<(string Id, string Text, string Code)>();
        using (var read = _conn.CreateCommand())
        {
            read.CommandText = "SELECT id, text, code FROM phrases WHERE deleted=0 AND COALESCE(abbr,'')=''";
            using var r = read.ExecuteReader();
            while (r.Read())
                needFill.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }

        foreach (var row in needFill)
        {
            var abbr = PinyinEncoder.AbbreviationOf(row.Text);
            if (abbr.Length == 0 || abbr == row.Code) continue;

            using var update = _conn.CreateCommand();
            update.CommandText = "UPDATE phrases SET abbr=$a WHERE id=$id";
            update.Parameters.AddWithValue("$a", abbr);
            update.Parameters.AddWithValue("$id", row.Id);
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 引入「词条唯一」约束。
    ///
    /// 词库是字典：一个词只有一条。原来用 (词条, 编码) 判重，
    /// 同一个词换个编码还能再进一条，导出到输入法就是两条候选。
    /// 这里补 text_key 列（归一化后的词条），回填历史数据、清掉重复，
    /// 再建唯一索引兜底。
    /// </summary>
    private void MigrateTextKey()
    {
        var hasColumn = false;
        using (var check = _conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(phrases)";
            using var r = check.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), "text_key", StringComparison.OrdinalIgnoreCase))
                    hasColumn = true;
        }

        if (!hasColumn)
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE phrases ADD COLUMN text_key TEXT";
            alter.ExecuteNonQuery();
        }

        // 只对「活着」的行做收敛。墓碑（deleted=1）不参与判断，
        // 否则墓碑里的旧编码更长，会把正常条目挤掉（踩过这个坑）。
        var rows = new List<(string Id, string Text, string Code, long CreatedAt)>();
        using (var read = _conn.CreateCommand())
        {
            read.CommandText = "SELECT id, text, code, created_at FROM phrases WHERE deleted=0";
            using var r = read.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
        }

        var keep = new Dictionary<string, (string Id, string Text, string Code, long CreatedAt)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = NormalizeText(row.Text);
            if (key.Length == 0) continue;

            if (!keep.TryGetValue(key, out var current))
            {
                keep[key] = row;
                continue;
            }

            var better = row.Code.Length > current.Code.Length
                         || (row.Code.Length == current.Code.Length && row.CreatedAt < current.CreatedAt);
            if (better) keep[key] = row;
        }

        var keepIds = keep.Values.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        var toDelete = rows.Where(r => !keepIds.Contains(r.Id)).Select(r => r.Id).ToList();

        // 先拆唯一索引再改数据。
        // 保留行的目标 key 可能正被「待删除的行」占着，先改 key 会撞唯一约束，
        // 表现为启动时报 UNIQUE constraint failed: phrases.text_key（踩过）。
        DropTextKeyIndex();

        using var tx = _conn.BeginTransaction();

        // 1) 先把所有活记录的 key 清空，腾出位置
        using (var clear = _conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "UPDATE phrases SET text_key=NULL WHERE deleted=0";
            clear.ExecuteNonQuery();
        }

        // 2) 删掉同一个词的多余行
        foreach (var id in toDelete)
        {
            using var del = _conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM phrases WHERE id=$id";
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
        }

        // 3) 给留下的行写回 key
        foreach (var kv in keep)
        {
            using var update = _conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE phrases SET text_key=$k WHERE id=$id";
            update.Parameters.AddWithValue("$k", kv.Key);
            update.Parameters.AddWithValue("$id", kv.Value.Id);
            update.ExecuteNonQuery();
        }
        tx.Commit();

        // 4) 重建唯一索引：只约束「活着」的行，
        //    墓碑可以和别的行同 key，否则删掉的词会挡住同名新词。
        try
        {
            using var idx = _conn.CreateCommand();
            idx.CommandText =
                "CREATE UNIQUE INDEX IF NOT EXISTS idx_phrases_text_key_live ON phrases(text_key) WHERE deleted=0";
            idx.ExecuteNonQuery();
        }
        catch { /* 建不了时唯一性仍由 Add 的检查保证，不阻断启动 */ }
    }

    private void DropTextKeyIndex()
    {
        foreach (var name in new[] { "idx_phrases_text_key", "idx_phrases_text_key_live" })
        {
            using var drop = _conn.CreateCommand();
            drop.CommandText = $"DROP INDEX IF EXISTS {name}";
            drop.ExecuteNonQuery();
        }
    }

    // ---------- 读 ----------

    public List<Phrase> List(bool includeDeleted = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = includeDeleted
            ? "SELECT id,text,code,rank,enabled,source,created_at,updated_at,rev,deleted,COALESCE(abbr,'') FROM phrases ORDER BY created_at"
            : "SELECT id,text,code,rank,enabled,source,created_at,updated_at,rev,deleted,COALESCE(abbr,'') FROM phrases WHERE deleted=0 ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        var list = new List<Phrase>();
        while (r.Read())
            list.Add(new Phrase
            {
                Id = r.GetString(0),
                Text = r.GetString(1),
                Code = r.GetString(2),
                Rank = r.GetInt32(3),
                Enabled = r.GetInt32(4) != 0,
                Source = r.GetString(5),
                CreatedAt = r.GetInt64(6),
                UpdatedAt = r.GetInt64(7),
                Rev = r.GetInt32(8),
                Deleted = r.GetInt32(9) != 0,
                Abbr = r.GetString(10)
            });
        return list;
    }

    public Phrase? FindByCode(string code)
        => List().FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.Ordinal));

    /// <summary>按词条查（归一化后比对）。词库里一个词只有一条。</summary>
    public Phrase? FindByText(string text)
        => FindByKey(NormalizeText(text));

    private Phrase? FindByKey(string key)
    {
        if (key.Length == 0) return null;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
SELECT id,text,code,rank,enabled,source,created_at,updated_at,rev,deleted,COALESCE(abbr,'')
FROM phrases WHERE text_key=$k AND deleted=0 LIMIT 1";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Phrase
        {
            Id = r.GetString(0),
            Text = r.GetString(1),
            Code = r.GetString(2),
            Rank = r.GetInt32(3),
            Enabled = r.GetInt32(4) != 0,
            Source = r.GetString(5),
            CreatedAt = r.GetInt64(6),
            UpdatedAt = r.GetInt64(7),
            Rev = r.GetInt32(8),
            Deleted = r.GetInt32(9) != 0,
            Abbr = r.GetString(10)
        };
    }

    /// <summary>按关键词搜索（词条或编码包含即可），用于加词前人工确认。</summary>
    public List<Phrase> Search(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return List();
        var k = keyword.Trim();
        return List().Where(p =>
            p.Text.Contains(k, StringComparison.OrdinalIgnoreCase) ||
            p.Code.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>查重用的文本归一化：去首尾空白、压缩内部空白、忽略大小写。</summary>
    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts).ToLowerInvariant();
    }

    /// <summary>编码合法性：a-z 与 '，长度 1..32，不以 u/v 开头（会撞输入法模式键）。</summary>
    public static bool IsValidCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > Mschxudp.MaxCodeLength) return false;
        foreach (var ch in code)
            if (!((ch >= 'a' && ch <= 'z') || ch == '\'')) return false;
        return code[0] != 'u' && code[0] != 'v';
    }

    // ---------- 写 ----------

    /// <summary>新增词条并落日志。已存在同 (词条, 编码) 时直接返回既有条目。</summary>
    public Phrase Add(string text, string code, int rank = 1, string source = "manual", bool enabled = true, string? abbr = null)
    {
        if (!IsValidCode(code))
            throw new ArgumentException($"编码 '{code}' 不合法：只允许 a-z 和 '，长度 1..32", nameof(code));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("词条不能为空", nameof(text));

        // 词库是字典：同一个词只允许一条，换个编码也不行
        var duplicate = FindByKey(NormalizeText(text));
        if (duplicate is not null) return duplicate;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var phrase = new Phrase
        {
            Id = NewId(),
            Text = text,
            Code = code,
            Abbr = abbr ?? PinyinEncoder.AbbreviationOf(text),
            Rank = Math.Clamp(rank, Mschxudp.MinOrder, Mschxudp.MaxOrder),
            Enabled = enabled,
            Source = source,
            CreatedAt = now,
            UpdatedAt = now,
            Rev = 1
        };
        Upsert(phrase);
        AppendLog(new ChangeRecord
        {
            Id = phrase.Id,
            Rev = phrase.Rev,
            Op = "upsert",
            Text = phrase.Text,
            Code = phrase.Code,
            Abbr = phrase.Abbr,
            Rank = phrase.Rank,
            Enabled = phrase.Enabled,
            Source = phrase.Source,
            Ts = now,
            Dev = DeviceId.Value
        });
        return phrase;
    }

    public bool Remove(string id)
    {
        var existing = List(true).FirstOrDefault(p => p.Id == id);
        if (existing is null) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        existing.Deleted = true;
        existing.UpdatedAt = now;
        existing.Rev++;
        Upsert(existing);
        AppendLog(new ChangeRecord
        {
            Id = existing.Id,
            Rev = existing.Rev,
            Op = "delete",
            Text = existing.Text,
            Code = existing.Code,
            Abbr = existing.Abbr,
            Rank = existing.Rank,
            Ts = now,
            Dev = DeviceId.Value
        });
        return true;
    }

    /// <summary>把一条变更套用到库中（同 id 取 rev 大者）。</summary>
    public void ApplyChange(ChangeRecord c)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT rev FROM phrases WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", c.Id);
        var existingRev = cmd.ExecuteScalar();
        if (existingRev is not null && Convert.ToInt32(existingRev) >= c.Rev) return;

        using var up = _conn.CreateCommand();
        up.CommandText = @"
INSERT INTO phrases(id,text,text_key,code,abbr,rank,enabled,source,created_at,updated_at,rev,deleted)
VALUES($id,$text,$tkey,$code,$abbr,$rank,$enabled,$source,$ts,$ts,$rev,$del)
ON CONFLICT(id) DO UPDATE SET
  text=excluded.text, text_key=excluded.text_key, code=excluded.code,
  abbr=excluded.abbr, rank=excluded.rank,
  enabled=excluded.enabled, source=excluded.source,
  updated_at=excluded.updated_at, rev=excluded.rev, deleted=excluded.deleted";
        up.Parameters.AddWithValue("$id", c.Id);
        up.Parameters.AddWithValue("$text", c.Text);
        up.Parameters.AddWithValue("$tkey", NormalizeText(c.Text));
        up.Parameters.AddWithValue("$code", c.Code);
        up.Parameters.AddWithValue("$abbr", c.Abbr ?? "");
        up.Parameters.AddWithValue("$rank", c.Rank);
        up.Parameters.AddWithValue("$enabled", c.Enabled ? 1 : 0);
        up.Parameters.AddWithValue("$source", c.Source);
        up.Parameters.AddWithValue("$ts", c.Ts);
        up.Parameters.AddWithValue("$rev", c.Rev);
        up.Parameters.AddWithValue("$del", c.Op == "delete" ? 1 : 0);
        up.ExecuteNonQuery();
    }

    private void Upsert(Phrase p)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO phrases(id,text,text_key,code,abbr,rank,enabled,source,created_at,updated_at,rev,deleted)
VALUES($id,$text,$tkey,$code,$abbr,$rank,$enabled,$source,$created,$updated,$rev,$del)
ON CONFLICT(id) DO UPDATE SET
  text=excluded.text, text_key=excluded.text_key, code=excluded.code,
  abbr=excluded.abbr, rank=excluded.rank,
  enabled=excluded.enabled, source=excluded.source,
  updated_at=excluded.updated_at, rev=excluded.rev, deleted=excluded.deleted";
        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.Parameters.AddWithValue("$text", p.Text);
        cmd.Parameters.AddWithValue("$tkey", NormalizeText(p.Text));
        cmd.Parameters.AddWithValue("$code", p.Code);
        cmd.Parameters.AddWithValue("$abbr", p.Abbr ?? "");
        cmd.Parameters.AddWithValue("$rank", p.Rank);
        cmd.Parameters.AddWithValue("$enabled", p.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$source", p.Source);
        cmd.Parameters.AddWithValue("$created", p.CreatedAt);
        cmd.Parameters.AddWithValue("$updated", p.UpdatedAt);
        cmd.Parameters.AddWithValue("$rev", p.Rev);
        cmd.Parameters.AddWithValue("$del", p.Deleted ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    // ---------- 变更日志 ----------

    /// <summary>
    /// 写一条变更。每条变更一个独立文件（&lt;id&gt;.&lt;rev&gt;.json），文件不可变，
    /// 这样多设备同时追加时 git 不会产生内容冲突，合并规则退化成「同 id 取 rev 大者」。
    /// </summary>
    public void AppendLog(ChangeRecord c)
    {
        Directory.CreateDirectory(Paths.ChangeDir);
        var path = Path.Combine(Paths.ChangeDir, $"{c.Id}.{c.Rev}.json");
        var json = JsonSerializer.Serialize(c, LogJson.Options);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public static List<ChangeRecord> ReadLog(string? dir = null)
    {
        var folder = dir ?? Paths.ChangeDir;
        var result = new List<ChangeRecord>();
        if (!Directory.Exists(folder)) return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<ChangeRecord>(File.ReadAllText(file), LogJson.Options);
                if (rec is not null) result.Add(rec);
            }
            catch (Exception) { /* 跳过坏文件 */ }
        }
        return result;
    }

    /// <summary>把日志重放到库里（用于首次拉取远端日志后重建）。</summary>
    public void Replay(IEnumerable<ChangeRecord> records)
    {
        foreach (var r in records) ApplyChange(r);
    }

    // ---------- 投影 ----------

    /// <summary>
    /// 把母库投影成要写入输入法的条目。
    ///
    /// 母库里一个词一条（全拼编码 + 简拼编码），投到输入法时展开成两条记录：
    /// 打全拼命中的一条、打首字母命中的一条。
    /// 输入法只认字面完全相同的编码，不会自己把 zhiruirui 映射到 zrr，所以两条都得写。
    /// </summary>
    public List<PhraseEntry> Project(int maxEntries = 2000)
    {
        var outp = new List<PhraseEntry>();
        foreach (var p in List())
        {
            if (!p.Enabled) continue;
            if (p.Code.Length > Mschxudp.MaxCodeLength) continue;
            if (p.Text.Length > Mschxudp.MaxWordLength) continue;
            if (p.Code.Length == 0) continue;

            outp.Add(new PhraseEntry(p.Code, p.Text, p.Rank));
            if (outp.Count >= maxEntries) break;

            var abbr = p.Abbr;
            if (abbr.Length > 0
                && abbr.Length <= Mschxudp.MaxCodeLength
                && !string.Equals(abbr, p.Code, StringComparison.Ordinal))
            {
                outp.Add(new PhraseEntry(abbr, p.Text, p.Rank, isAbbr: true));
                if (outp.Count >= maxEntries) break;
            }
        }
        return outp;
    }

    /// <summary>
    /// 按词条去重：一个词只保留一条，优先保留编码更长的那条（全拼比简拼长）。
    /// 返回删掉的条数。
    /// </summary>
    public int RemoveDuplicates()
    {
        var rows = List();
        var groups = rows.GroupBy(p => NormalizeText(p.Text))
                         .Where(g => g.Count() > 1)
                         .ToList();

        var removed = 0;
        foreach (var g in groups)
        {
            var keep = g.OrderByDescending(p => p.Code.Length)
                        .ThenBy(p => p.CreatedAt)
                        .First();
            foreach (var extra in g.Where(p => p.Id != keep.Id))
                if (Remove(extra.Id)) removed++;
        }
        return removed;
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..16];
    public void Dispose()
    {
        _conn.Dispose();
        // sqlite 连接池会继续持有文件句柄，释放后必须清池，否则测试/备份时删不掉文件
        SqliteConnection.ClearAllPools();
    }
}

internal static class LogJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

/// <summary>设备标识。只用于日志溯源，不含任何个人信息。</summary>
public static class DeviceId
{
    public static string Value { get; } = Compute();

    private static string Compute()
    {
        try
        {
            var path = Path.Combine(Paths.AppDataDir, "device-id");
            if (File.Exists(path)) return File.ReadAllText(path).Trim();
            Directory.CreateDirectory(Paths.AppDataDir);
            var id = Guid.NewGuid().ToString("N")[..12];
            File.WriteAllText(path, id);
            return id;
        }
        catch { return "unknown"; }
    }
}
