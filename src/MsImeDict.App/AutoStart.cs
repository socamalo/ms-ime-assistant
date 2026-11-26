using Microsoft.Win32;

namespace MsImeDict.App;

/// <summary>
/// 开机自启：写 HKCU 的 Run 键，不需要管理员权限，卸载时删掉即可。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MsImeAssistant";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static void Enable(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key?.SetValue(ValueName, $"\"{exePath}\"");
            Log.Write("autostart enabled: " + exePath);
        }
        catch (Exception ex) { Log.Write("autostart enable failed: " + ex.Message); }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Write("autostart disabled");
        }
        catch (Exception ex) { Log.Write("autostart disable failed: " + ex.Message); }
    }

    public static void Apply(bool enabled)
    {
        if (enabled) Enable(CurrentExe());
        else Disable();
    }

    private static string CurrentExe()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) ? Application.ExecutablePath : path;
    }
}
