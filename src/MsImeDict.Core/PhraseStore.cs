using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MsImeDict.Core;

/// <summary>母库里的一条词条。</summary>
public sealed class Phrase
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Code { get; set; } = "";
    public int Rank { get; set; } = 2;
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
  code        TEXT NOT NULL,
  rank        INTEGER NOT NULL DEFAULT 2,
  enabled     INTEGER NOT NULL DEFAULT 1,
  source      TEXT NOT NULL DEFAULT 'manual',
  created_at  INTEGER NOT NULL,
  updated_at  INTEGER NOT NULL,
  rev         INTEGER NOT NULL DEFAULT 1,
  deleted     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_phrases_code ON phrases(code);
CREATE INDEX IF NOT EXISTS idx_phrases_text ON phrases(text);
";
        cmd.ExecuteNonQuery();
    }

    // ---------- 读 ----------

    public List<Phrase> List(bool includeDeleted = false)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = includeDeleted
            ? "SELECT id,text,code,rank,enabled,source,created_at,updated_at,rev,deleted FROM phrases ORDER BY created_at"
            : "SELECT id,text,code,rank,enabled,source,created_at,updated_at,rev,deleted FROM phrases WHERE deleted=0 ORDER BY created_at";
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
                Deleted = r.GetInt32(9) != 0
            });
        return list;
    }

    public Phrase? FindByCode(string code)
        => List().FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.Ordinal));

    public Phrase? FindByText(string text)
        => List().FirstOrDefault(p => string.Equals(p.Text, text, StringComparison.Ordinal));

    // ---------- 写 ----------

    /// <summary>新增词条并落日志。返回落库的词条。</summary>
    public Phrase Add(string text, string code, int rank = 2, string source = "manual", bool enabled = true)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var phrase = new Phrase
        {
            Id = NewId(),
            Text = text,
            Code = code,
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
INSERT INTO phrases(id,text,code,rank,enabled,source,created_at,updated_at,rev,deleted)
VALUES($id,$text,$code,$rank,$enabled,$source,$ts,$ts,$rev,$del)
ON CONFLICT(id) DO UPDATE SET
  text=excluded.text, code=excluded.code, rank=excluded.rank,
  enabled=excluded.enabled, source=excluded.source,
  updated_at=excluded.updated_at, rev=excluded.rev, deleted=excluded.deleted";
        up.Parameters.AddWithValue("$id", c.Id);
        up.Parameters.AddWithValue("$text", c.Text);
        up.Parameters.AddWithValue("$code", c.Code);
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
INSERT INTO phrases(id,text,code,rank,enabled,source,created_at,updated_at,rev,deleted)
VALUES($id,$text,$code,$rank,$enabled,$source,$created,$updated,$rev,$del)
ON CONFLICT(id) DO UPDATE SET
  text=excluded.text, code=excluded.code, rank=excluded.rank,
  enabled=excluded.enabled, source=excluded.source,
  updated_at=excluded.updated_at, rev=excluded.rev, deleted=excluded.deleted";
        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.Parameters.AddWithValue("$text", p.Text);
        cmd.Parameters.AddWithValue("$code", p.Code);
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

    /// <summary>把母库投影成候选条目：可用的词条 + 编码合法 + 词面长度合法。</summary>
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
        }
        return outp;
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
