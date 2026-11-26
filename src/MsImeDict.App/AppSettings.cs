using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsImeDict.App;

/// <summary>应用设置。</summary>
public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt+D";
    public int DefaultRank { get; set; } = 2;
    public bool AutoDeploy { get; set; } = true;
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
