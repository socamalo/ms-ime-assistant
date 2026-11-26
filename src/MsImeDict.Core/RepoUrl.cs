namespace MsImeDict.Core;

/// <summary>
/// 把用户填的仓库地址归一化成 git 能用的 clone 地址。
///
/// 用户会贴各种形式，都要认：
///   https://github.com/me/repo          （网页地址，最常见）
///   https://github.com/me/repo/         （带尾斜杠）
///   https://github.com/me/repo.git
///   me/repo                             （简写）
///   git@github.com:me/repo.git          （SSH，原样保留）
///   file:///C:/x.git 或 \\server\share  （本地路径，原样保留）
/// </summary>
public static class RepoUrl
{
    public static string Normalize(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0) return s;

        // SSH 与本地路径原样返回
        if (s.StartsWith("git@", StringComparison.OrdinalIgnoreCase)) return s;
        if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return s.TrimEnd('/');
        if (s.StartsWith(@"\\", StringComparison.Ordinal)) return s.TrimEnd('\\');

        // owner/repo 简写
        if (!s.Contains("://") && !s.Contains('\\'))
        {
            var slashes = s.Count(c => c == '/');
            if (slashes == 1) s = "https://github.com/" + s;
        }

        s = s.TrimEnd('/');

        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s += ".git";
        }

        return s;
    }

    /// <summary>粗略判断是否像一个仓库地址，用于设置界面的即时校验。</summary>
    public static bool LooksValid(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0) return false;
        if (s.StartsWith("git@", StringComparison.OrdinalIgnoreCase)) return s.Contains(':');
        if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return s.Contains('/');
        return s.Count(c => c == '/') == 1;   // owner/repo
    }
}
