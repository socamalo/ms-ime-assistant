using System.Text.Json;

namespace MsImeDict.Core;

/// <summary>
/// 读取 settings.json 里的同步配置。
/// 用 JsonDocument 松散解析，避免 Core 依赖 App 的设置类。
/// </summary>
public static class ConfigFile
{
    public static SyncSettings LoadSync()
    {
        var settings = new SyncSettings();
        try
        {
            if (!File.Exists(Paths.SettingsFile)) return settings;
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.SettingsFile));
            var root = doc.RootElement;

            settings.Enabled = GetBool(root, "SyncEnabled");
            settings.RemoteUrl = GetString(root, "RemoteUrl");
            var branch = GetString(root, "Branch");
            if (!string.IsNullOrWhiteSpace(branch)) settings.Branch = branch;
        }
        catch { }
        return settings;
    }

    private static bool GetBool(JsonElement root, string name)
    {
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.True;
        return false;
    }

    private static string GetString(JsonElement root, string name)
    {
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "";
        return "";
    }
}
