using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MsImeDict.Core;

namespace MsImeDict.App;

/// <summary>应用设置。</summary>
public sealed class AppSettings
{
    /// <summary>主热键。Ctrl+单字母会在系统层劫持该组合，选一个各软件少用的。</summary>
    public string Hotkey { get; set; } = "Ctrl+Q";
    public bool AutoDeploy { get; set; } = true;

    /// <summary>开机自动启动（写 HKCU 的 Run 键）。</summary>
    public bool AutoStart { get; set; }
    public bool SyncEnabled { get; set; }

    /// <summary>私有仓库远端地址。本机工作副本固定放在 AppDataDir\sync-repo，不开放配置。</summary>
    public string RemoteUrl { get; set; } = "";
    public string Branch { get; set; } = "main";

    /// <summary>设置最后修改时间（Unix 秒）。跨设备同步时用它判断谁更新。</summary>
    public long UpdatedAt { get; set; }

    /// <summary>导出可跨设备同步的子集（快捷键、自动部署）。</summary>
    public PortableSettings ToPortable() => new()
    {
        Hotkey = Hotkey,
        AutoDeploy = AutoDeploy,
        UpdatedAt = UpdatedAt
    };

    /// <summary>应用远端来的设置子集。仓库地址与开机自启属于本机，不覆盖。</summary>
    public void ApplyPortable(PortableSettings p)
    {
        Hotkey = p.Hotkey;
        AutoDeploy = p.AutoDeploy;
        UpdatedAt = p.UpdatedAt;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Json) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.SettingsFile)!);
            File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
