using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsImeDict.Core;

/// <summary>
/// 可跨设备同步的设置子集。
///
/// 只同步「换机器也希望一致」的项：快捷键、是否自动写入输入法。
/// 不同步：仓库地址（本来就是同一个）、工作副本路径、开机自启（每台机器各自的注册表）。
///
/// 候选位不在这里：用户自己加的词候选位恒为 1，没有可配的余地。
/// 它曾经是可配项，被同步到另一台设备后把新词压到了第 2 位。
/// </summary>
public sealed class PortableSettings
{
    [JsonPropertyName("hotkey")] public string Hotkey { get; set; } = "Ctrl+Q";
    [JsonPropertyName("autoDeploy")] public bool AutoDeploy { get; set; } = true;

    /// <summary>Unix 秒。用于判断哪一侧更新，决定谁覆盖谁。</summary>
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }

    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static PortableSettings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PortableSettings>(File.ReadAllText(path), Json);
        }
        catch { return null; }
    }

    public void Write(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    public PortableSettings Clone() => new()
    {
        Hotkey = Hotkey,
        AutoDeploy = AutoDeploy,
        UpdatedAt = UpdatedAt
    };
}
