using System.IO;
using System.Text;
using MsImeDict.Core;

namespace MsImeDict.App;

/// <summary>极简日志，只用于排查热键/钩子是否被触发。超过 1MB 自动轮转一次。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath =
        Path.Combine(Paths.AppDataDir, "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1024 * 1024)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
