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
    public int DefaultRank { get; set; } = 2;
    public bool AutoDeploy { get; set; } = true;

    /// <summary>开机自动启动（写 HKCU 的 Run 键）。</summary>
    public bool AutoStart { get; set; }
    public bool SyncEnabled { get; set; }
    public string RepoPath { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
    public string Branch { get; set; } = "main";

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
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.SettingsFile)!);
            File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
