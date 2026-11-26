namespace MsImeDict.Core;

/// <summary>本工具用到的全部路径。</summary>
public static class Paths
{
    /// <summary>微软拼音用户数据目录。</summary>
    public static string ImeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "InputMethod", "Chs");

    /// <summary>用户自定义短语库。本工具唯一会写的系统文件。</summary>
    public static string LexFile => Path.Combine(ImeDir, "ChsPinyinEUDPv1.lex");

    /// <summary>自学习词库。只读，不写。</summary>
    public static string SelfLearningFile => Path.Combine(ImeDir, "ChsPinyinUDL.dat");

    /// <summary>运行时状态（sqlite、日志、备份）。</summary>
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MsImeDict");

    public static string DatabaseFile => Path.Combine(AppDataDir, "phrases.db");

    /// <summary>变更文件目录。每次变更一个文件（&lt;id&gt;.&lt;rev&gt;.json），git 天然无冲突。</summary>
    public static string ChangeDir => Path.Combine(AppDataDir, "changes");

    public static string BackupDir => Path.Combine(AppDataDir, "backups");
    public static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MsImeDict", "settings.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(ChangeDir);
        Directory.CreateDirectory(BackupDir);
    }
}
