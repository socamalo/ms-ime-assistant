using System.Diagnostics;
using System.Text;

namespace MsImeDict.Core;

/// <summary>同步设置。</summary>
public sealed class SyncSettings
{
    /// <summary>
    /// 私有仓库的本机工作副本。固定在运行时目录下（`%LOCALAPPDATA%\MsImeDict\sync-repo`），
    /// 由程序自己 clone / init / commit / push，不开放配置。
    /// </summary>
    public string RepoPath => Path.Combine(Paths.AppDataDir, "sync-repo");

    /// <summary>私有仓库远端地址（git URL）。</summary>
    public string RemoteUrl { get; set; } = "";
    public string Branch { get; set; } = "main";
    public bool Enabled { get; set; }

    /// <summary>本机的可同步设置。为空时不同步设置。</summary>
    public PortableSettings? LocalSettings { get; set; }
}

/// <summary>同步结果。</summary>
public sealed class SyncResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public int Pushed { get; set; }
    public int Pulled { get; set; }
    public List<string> Steps { get; set; } = new();

    /// <summary>远端设置有更新，需要调用方应用到本机。</summary>
    public PortableSettings? RemoteSettings { get; set; }

    /// <summary>本次把本机设置推到了远端。</summary>
    public bool SettingsPushed { get; set; }
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
        if (!settings.Enabled)
        {
            result.Ok = false;
            result.Message = "同步未启用";
            return result;
        }

        var repo = settings.RepoPath;
        if (string.IsNullOrWhiteSpace(settings.RemoteUrl))
        {
            result.Ok = false;
            result.Message = "没有填写私有仓库地址";
            return result;
        }

        // 用户可能贴的是网页地址（不带 .git），统一归一化
        settings.RemoteUrl = RepoUrl.Normalize(settings.RemoteUrl);

        if (!EnsureRepo(repo, settings.RemoteUrl, settings.Branch, result))
        {
            result.Ok = false;
            result.Message = "无法准备本地工作副本";
            return result;
        }

        // 1) 先拉取。远端还没有这个分支时（首次推送）跳过。
        //    先拉再改，工作区是干净的，不需要 commit 之后再 rebase。
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

        // 2) 设置同步：只同步可携带的几项，按 updatedAt 取新
        var repoSettingsPath = Path.Combine(repo, PortableSettings.FileName);
        if (settings.LocalSettings is not null)
        {
            var remote = PortableSettings.TryRead(repoSettingsPath);
            if (remote is not null && remote.UpdatedAt > settings.LocalSettings.UpdatedAt)
            {
                result.RemoteSettings = remote;
                result.Steps.Add($"远端设置更新于本机，待应用（{remote.Hotkey}）");
            }
            else
            {
                settings.LocalSettings.Write(repoSettingsPath);
                result.SettingsPushed = true;
                result.Steps.Add("本机设置写入仓库");
            }
        }

        // 3) 本地变更 -> 仓库
        var repoChanges = Path.Combine(repo, "changes");
        Directory.CreateDirectory(repoChanges);
        int pushed = 0;
        foreach (var file in Directory.EnumerateFiles(Paths.ChangeDir, "*.json"))
        {
            var target = Path.Combine(repoChanges, Path.GetFileName(file));
            if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(file)))
            {
                File.Copy(file, target, overwrite: true);
                pushed++;
            }
        }
        result.Pushed = pushed;
        result.Steps.Add($"本地变更上传 {pushed} 个文件");

        // 4) 提交
        Run(repo, "git add -A");
        var commit = Run(repo, "git commit -m \"sync: 更新个人词库\"", ignoreError: true);
        result.Steps.Add(commit.Contains("nothing to commit") ? "无新变更" : "已提交本地变更");

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
        if (push.Contains("fatal:") || push.Contains("rejected") || push.Contains("error:"))
        {
            result.Ok = false;
            result.Message = $"推送失败：{FirstLine(push)}";
            return result;
        }
        result.Steps.Add("已推送");

        result.Message = "同步完成";
        return result;
    }

    /// <summary>
    /// 准备本地工作副本：有远程就克隆，克隆不了就地初始化；
    /// 已存在则只校正 origin 地址。返回是否可用。
    /// </summary>
    private static bool EnsureRepo(string repo, string remoteUrl, string branch, SyncResult result)
    {
        var gitDir = Path.Combine(repo, ".git");

        if (!Directory.Exists(gitDir))
        {
            var existed = Directory.Exists(repo);
            var empty = !existed || Directory.GetFileSystemEntries(repo).Length == 0;

            if (empty)
            {
                var parent = Path.GetDirectoryName(repo.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                Run(parent ?? repo, $"git clone \"{remoteUrl}\" \"{repo}\"", ignoreError: true);
            }

            if (!Directory.Exists(gitDir))
            {
                Directory.CreateDirectory(repo);
                Run(repo, "git init");
                Run(repo, $"git checkout -b {branch}", ignoreError: true);
                result.Steps.Add("初始化本地工作副本");
            }
            else
            {
                result.Steps.Add("从远端克隆工作副本");
            }

            Run(repo, "git config user.name \"ms-ime-dict\"");
            Run(repo, "git config user.email \"ms-ime-dict@localhost\"");
        }

        Run(repo, $"git remote add origin \"{remoteUrl}\"", ignoreError: true);
        Run(repo, $"git remote set-url origin \"{remoteUrl}\"", ignoreError: true);
        return Directory.Exists(gitDir);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? text;
        return line.Trim();
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
