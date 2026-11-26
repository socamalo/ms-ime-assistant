using System.Diagnostics;
using MsImeDict.Core;

namespace MsImeDict.App;

/// <summary>常驻托盘程序入口。</summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var context = new ApplicationContext();
        var hotkey = new HotkeyHost();

        var tray = new TrayHost(context, hotkey);
        tray.Initialize();

        Application.Run(context);
    }
}

/// <summary>托盘图标与菜单。</summary>
public sealed class TrayHost
{
    private readonly ApplicationContext _context;
    private readonly HotkeyHost _hotkey;
    private readonly NotifyIcon _tray;
    private readonly AppSettings _settings = AppSettings.Load();
    private PhraseStore? _store;

    public TrayHost(ApplicationContext context, HotkeyHost hotkey)
    {
        _context = context;
        _hotkey = hotkey;

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "微软拼音个人词库",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("加入选中的文字…", null, (_, _) => CaptureSelection());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("立即写入输入法", null, (_, _) => DeployNow());
        menu.Items.Add("同步到私有仓库", null, (_, _) => SyncNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("词库目录", null, (_, _) => OpenFolder(Paths.AppDataDir));
        menu.Items.Add("设置", null, (_, _) => EditSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;

        _tray.DoubleClick += (_, _) => CaptureSelection();
    }

    public void Initialize()
    {
        _hotkey.Triggered += CaptureSelection;
        if (!_hotkey.Register(_settings.Hotkey))
            _tray.ShowBalloonTip(5000, "热键注册失败",
                $"无法注册 {_settings.Hotkey}，可能被其他程序占用", ToolTipIcon.Warning);

        // 隐藏主窗口，用托盘消息驱动
        _context.MainForm = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false, Opacity = 0 };
        _context.MainForm.Show();
        _context.MainForm.Hide();
        _context.MainForm.Resize += (_, _) => { if (_context.MainForm!.WindowState != FormWindowState.Minimized) _context.MainForm.Hide(); };
    }

    private void CaptureSelection()
    {
        var text = SelectionReader.Read();
        if (string.IsNullOrWhiteSpace(text))
        {
            _tray.ShowBalloonTip(4000, "没取到选区",
                "请先在目标窗口里选中文字，再按热键（或右键托盘图标）", ToolTipIcon.Info);
            return;
        }

        text = text.Trim();
        var encoder = new PinyinEncoder();
        var encoded = encoder.Encode(text);
        var info = new EncodedInfo
        {
            Codes = encoded.CandidateCodes.ToList(),
            Notes = encoded.Notes.ToList()
        };

        using var form = new AddPhraseForm(text, info, _settings.DefaultRank);
        if (form.ShowDialog() != DialogResult.OK) return;

        try
        {
            _store ??= new PhraseStore();
            var codes = form.ResolveCodes();
            if (codes.Count == 0)
            {
                _tray.ShowBalloonTip(4000, "没有可用编码", "请在对话框里手工填写编码", ToolTipIcon.Warning);
                return;
            }
            foreach (var code in codes)
            {
                _store.Add(text, code, form.Rank, source: "selection");
            }

            if (_settings.AutoDeploy)
            {
                var result = new ImeDeployer().Deploy(_store.Project());
                if (result.Ok)
                    _tray.ShowBalloonTip(5000, "已加入个人词典",
                        $"「{text}」已写入，切换一次输入法即可看到", ToolTipIcon.Info);
                else
                    _tray.ShowBalloonTip(6000, "写入失败", result.Message, ToolTipIcon.Error);
            }
            else
            {
                _tray.ShowBalloonTip(4000, "已加入词库", "「" + text + "」已入库，点托盘菜单「立即写入输入法」生效", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(6000, "出错了", ex.Message, ToolTipIcon.Error);
        }
    }

    private void DeployNow()
    {
        try
        {
            _store ??= new PhraseStore();
            var result = new ImeDeployer().Deploy(_store.Project());
            _tray.ShowBalloonTip(5000, result.Ok ? "已写入输入法" : "写入失败",
                result.Ok ? $"{result.EntryCount} 条已生效，切换一次输入法即可看到" : result.Message,
                result.Ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(6000, "出错了", ex.Message, ToolTipIcon.Error);
        }
    }

    private void SyncNow()
    {
        try
        {
            _store ??= new PhraseStore();
            var agent = new SyncAgent(_store);
            var result = agent.Sync(new Core.SyncSettings
            {
                Enabled = _settings.SyncEnabled,
                RepoPath = _settings.RepoPath,
                RemoteUrl = _settings.RemoteUrl,
                Branch = _settings.Branch
            });
            _tray.ShowBalloonTip(5000, result.Ok ? "同步完成" : "同步失败", result.Message,
                result.Ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(6000, "同步出错", ex.Message, ToolTipIcon.Error);
        }
    }

    private void EditSettings()
    {
        _tray.ShowBalloonTip(3000, "设置", "设置文件：" + Paths.SettingsFile, ToolTipIcon.Info);
        try { Process.Start("notepad.exe", Paths.SettingsFile); } catch { }
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start("explorer.exe", path); } catch { }
    }

    private void Shutdown()
    {
        _hotkey.Shutdown();
        _tray.Visible = false;
        _tray.Dispose();
        _store?.Dispose();
        _context.ExitThread();
    }
}
