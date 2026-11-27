using System.Reflection;

namespace MsImeDict.Core;

/// <summary>版本号。唯一来源是仓库根目录的 Directory.Build.props。</summary>
public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string Product => "微软输入法助手";
}
