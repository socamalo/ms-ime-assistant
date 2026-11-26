using System.Diagnostics;
using System.Text;

namespace MsImeDict.Core;

/// <summary>同步设置。</summary>
public sealed class SyncSettings
{
    /// <summary>私有仓库的本地克隆目录。</summary>
    public string RepoPath { get; set; } = "";
    /// <summary>私有仓库远端地址（git URL）。</summary>
    public string RemoteUrl { get; set; } = "";
    public string Branch { get; set; } = "main";
    public bool Enabled { get; set; }
}

/// <summary>同步结果。</summary>
public sealed class SyncResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public int Pushed { get; set; }
    public int Pulled { get; set; }
    public List<string> Steps { get; set; } = new();
}

/// <summary>
/// 跨设备同步：私有 Git 仓库 + 每次变更一个不可变文件的合并模型。
///
/// 仓库结构：
///   changes/&lt;id&gt;.&lt;rev&gt;.json    每条变更一个文件
///
/// 多设备同时追加只会新增文件，不会改同一文件内容，所以 git 不需要做内容合并；
/// 合并规则退化为「同 id 取 rev 大者，删除用 tombstone」。
/// </summary>
public sealed class SyncAgent
{
    private readonly PhraseStore _store;

    public SyncAgent(PhraseStore store) => _store = store;

    public SyncResult Sync(SyncSettings settings)
    {
        var result = new SyncResult { Ok = true };
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.RepoPath))
        {
            result.Ok = false;
            result.Message = "同步未启用或未配置仓库路径";
            return result;
        }

        var repo = settings.RepoPath;
        Directory.CreateDirectory(repo);

        // 1) 初始化仓库
        if (!Directory.Exists(Path.Combine(repo, ".git")))
        {
            Run(repo, "git init");
            Run(repo, $"git checkout -b {settings.Branch}", ignoreError: true);
            Run(repo, "git config user.name \"ms-ime-dict\"");
            Run(repo, "git config user.email \"ms-ime-dict@localhost\"");
            result.Steps.Add("初始化仓库");
        }
        if (!string.IsNullOrWhiteSpace(settings.RemoteUrl))
            Run(repo, $"git remote add origin \"{settings.RemoteUrl}\"", ignoreError: true);
        else
            Run(repo, "git remote add origin \"https://github.com/CHANGE-ME/private.git\"", ignoreError: true);

        // 2) 本地变更 -> 仓库
        var repoChanges = Path.Combine(repo, "changes");
        Directory.CreateDirectory(repoChanges);
        int pushed = 0;
        foreach (var file in Directory.EnumerateFiles(Paths.ChangeDir, "*.json"))
        {
            var target = Path.Combine(repoChanges, Path.GetFileName(file));
            if (!File.Exists(target) ||
                !File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(file)))
            {
                File.Copy(file, target, overwrite: true);
                pushed++;
            }
        }
        result.Pushed = pushed;
        result.Steps.Add($"本地变更上传 {pushed} 个文件");

        // 3) 提交
        Run(repo, "git add -A");
        var commit = Run(repo, "git commit -m \"sync: 更新个人词库\"", ignoreError: true);
        if (commit.Contains("nothing to commit"))
            result.Steps.Add("无新变更");
        else
            result.Steps.Add("已提交");

        // 4) 拉取。远端还没有这个分支时（首次推送）跳过。
        var remoteHasBranch = Run(repo, $"git ls-remote --heads origin {settings.Branch}", ignoreError: true)
            .Contains($"refs/heads/{settings.Branch}", StringComparison.Ordinal);

        if (!remoteHasBranch)
        {
            result.Steps.Add("远端尚无该分支，跳过拉取");
        }
        else
        {
            var pull = Run(repo, $"git pull --rebase origin {settings.Branch}", ignoreError: true);
            if (pull.Contains("CONFLICT") || pull.Contains("fatal:"))
            {
                result.Ok = false;
                result.Message = $"拉取失败：{pull.Trim()}";
                Run(repo, "git rebase --abort", ignoreError: true);
                return result;
            }
            result.Steps.Add("已拉取远端");
        }

        // 5) 远端变更 -> 本地，并重放到母库
        int pulled = 0;
        foreach (var file in Directory.EnumerateFiles(repoChanges, "*.json"))
        {
            var target = Path.Combine(Paths.ChangeDir, Path.GetFileName(file));
            if (!File.Exists(target))
            {
                File.Copy(file, target, overwrite: true);
                pulled++;
            }
        }
        result.Pulled = pulled;
        result.Steps.Add($"远端变更下载 {pulled} 个文件");

        foreach (var rec in PhraseStore.ReadLog()) _store.ApplyChange(rec);
        result.Steps.Add("已重放到母库");

        // 6) 推送
        var push = Run(repo, $"git push origin {settings.Branch}", ignoreError: true);
        if (push.Contains("fatal:") || push.Contains("rejected"))
        {
            result.Ok = false;
            result.Message = $"推送失败：{push.Trim()}";
            return result;
        }
        result.Steps.Add("已推送");

        result.Message = result.Ok ? "同步完成" : result.Message;
        return result;
    }

    private static string Run(string workingDir, string command, bool ignoreError = false)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var all = stdout + "\n" + stderr;
            if (p.ExitCode != 0 && !ignoreError)
                throw new InvalidOperationException($"命令失败 ({p.ExitCode}): {command}\n{all.Trim()}");
            return all;
        }
        catch (Exception ex) when (ignoreError)
        {
            return ex.Message;
        }
    }
}
