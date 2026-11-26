using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        var tray = new TrayHost(context);
        tray.Initialize();

        Application.Run(context);
    }
}

/// <summary>托盘图标、热键、漂浮条、取词与部署。</summary>
public sealed class TrayHost
{
    private const int MaxQuickBarTextLength = 300;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private readonly ApplicationContext _context;
    private readonly NotifyIcon _tray;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HotkeyHost _hotkey = new();
    private readonly MouseHook _mouse = new();
    private readonly QuickBar _bar = new();

    private PhraseStore? _store;
    private Control? _ui;
    private string _lastShown = "";

    public TrayHost(ApplicationContext context)
    {
        _context = context;
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
        // 隐藏主窗口，用托盘消息驱动
        var form = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false, Opacity = 0 };
        _context.MainForm = form;
        form.Show();
        form.Hide();
        _ui = form;

        _hotkey.Triggered += () =>
        {
            Log.Write("hotkey fired");
            CaptureSelection();
        };
        var hotkeyOk = _hotkey.Register(_settings.Hotkey);
        Log.Write($"start: hotkey={_settings.Hotkey} registered={hotkeyOk}");
        if (!hotkeyOk)
            Notify("热键注册失败", $"无法注册 {_settings.Hotkey}，可能被其他程序占用", ToolTipIcon.Warning);

        _bar.AddRequested += text => AddPhrase(text, openDialog: false);
        _bar.MoreRequested += ShowMoreMenu;

        _mouse.DragSelected += OnDragSelected;
        _mouse.CtrlRightClicked += OnCtrlRightClicked;
        _mouse.AnyMouseDown += pt =>
        {
            if (_bar.Visible && !_bar.Bounds.Contains(pt)) _bar.HideBar();
        };
        var hookOk = _mouse.Start();
        Log.Write($"mouse hook started={hookOk}");
        if (!hookOk)
            Notify("鼠标监听未启用", "选区浮条不可用，热键仍然有效", ToolTipIcon.Warning);
    }

    // ---------- 选区浮条 ----------

    private void OnDragSelected(Point anchor)
    {
        if (IsOwnWindow()) return;

        // 等目标应用把选区落到 UI Automation 上
        System.Threading.Thread.Sleep(160);
        var text = StaRunner.Run(() => SelectionReader.Read(), 2500);
        if (string.IsNullOrWhiteSpace(text)) return;

        text = text.Trim();
        if (text.Length > MaxQuickBarTextLength) return;

        Post(() =>
        {
            if (text == _lastShown && _bar.Visible) return;
            _lastShown = text;
            _bar.ShowAt(text, anchor);
        });
    }

    private void OnCtrlRightClicked(Point at)
    {
        if (IsOwnWindow()) return;
        System.Threading.Thread.Sleep(120);
        var text = StaRunner.Run(() => SelectionReader.Read(), 2500);
        if (string.IsNullOrWhiteSpace(text)) return;
        Post(() => ShowMoreMenu(text.Trim(), at));
    }

    private void ShowMoreMenu(string text, Point at)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add($"「{Shorten(text)}」", null, (_, _) => { });
        ((ToolStripMenuItem)menu.Items[0]!).Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("加入个人词典", null, (_, _) => AddPhrase(text, openDialog: false));
        menu.Items.Add("加入并指定编码…", null, (_, _) => AddPhrase(text, openDialog: true));
        menu.Show(at);
    }

    private bool IsOwnWindow()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ---------- 取词 ----------

    private void CaptureSelection()
    {
        var text = StaRunner.Run(() => SelectionReader.Read(), 2500);
        Log.Write("capture: length=" + (text?.Length ?? -1));
        if (string.IsNullOrWhiteSpace(text))
        {
            // 取不到选区时也打开对话框，让用户直接粘贴或手输
            AddPhrase("", openDialog: true);
            return;
        }
        AddPhrase(text.Trim(), openDialog: true);
    }

    // ---------- 加词 ----------

    private void AddPhrase(string text, bool openDialog)
    {
        try
        {
            _store ??= new PhraseStore();
            var encoded = new PinyinEncoder().Encode(text);
            var info = new EncodedInfo
            {
                Codes = encoded.CandidateCodes.ToList(),
                Notes = encoded.Notes.ToList()
            };

            List<string> codes;
            int rank;
            var word = text;

            if (openDialog)
            {
                using var form = new AddPhraseForm(text, info, _settings.DefaultRank);
                if (form.ShowDialog() != DialogResult.OK) return;

                word = form.Word;
                if (string.IsNullOrWhiteSpace(word))
                {
                    Notify("词条为空", "没有可加入的内容", ToolTipIcon.Warning);
                    return;
                }
                rank = form.Rank;

                if (!string.Equals(word, text, StringComparison.Ordinal))
                {
                    // 用户改了词面，编码要按新词面重算
                    var again = new PinyinEncoder().Encode(word);
                    codes = again.CandidateCodes.ToList();
                    Log.Write($"re-encoded after edit: {word} -> {string.Join(",", codes)}");
                }
                else
                {
                    codes = form.ResolveCodes();
                }
            }
            else
            {
                codes = info.Codes;
                rank = _settings.DefaultRank;
            }

            if (codes.Count == 0)
            {
                Notify("没有可用编码", "请在「加入并指定编码…」里手工填写", ToolTipIcon.Warning);
                return;
            }

            foreach (var code in codes) _store.Add(word, code, rank, source: "selection");

            if (!_settings.AutoDeploy)
            {
                Notify("已加入词库", $"「{Shorten(word)}」已入库，点托盘菜单「立即写入输入法」生效", ToolTipIcon.Info);
                return;
            }

            var result = new ImeDeployer().Deploy(_store.Project());
            if (result.Ok)
                Notify("已加入个人词典", $"「{Shorten(word)}」已写入输入法，直接打字即可", ToolTipIcon.Info);
            else
                Notify("写入失败", result.Message, ToolTipIcon.Error);
        }
        catch (Exception ex)
        {
            Notify("出错了", ex.Message, ToolTipIcon.Error);
        }
    }

    // ---------- 菜单动作 ----------

    private void DeployNow()
    {
        try
        {
            _store ??= new PhraseStore();
            var result = new ImeDeployer().Deploy(_store.Project());
            Notify(result.Ok ? "已写入输入法" : "写入失败",
                result.Ok ? $"{result.EntryCount} 条已生效，直接打字即可" : result.Message,
                result.Ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        }
        catch (Exception ex) { Notify("出错了", ex.Message, ToolTipIcon.Error); }
    }

    private void SyncNow()
    {
        try
        {
            _store ??= new PhraseStore();
            var result = new SyncAgent(_store).Sync(new Core.SyncSettings
            {
                Enabled = _settings.SyncEnabled,
                RepoPath = _settings.RepoPath,
                RemoteUrl = _settings.RemoteUrl,
                Branch = _settings.Branch
            });
            Notify(result.Ok ? "同步完成" : "同步失败", result.Message,
                result.Ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        }
        catch (Exception ex) { Notify("同步出错", ex.Message, ToolTipIcon.Error); }
    }

    private void EditSettings()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile)) _settings.Save();
            Process.Start("notepad.exe", Paths.SettingsFile);
        }
        catch { }
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start("explorer.exe", path); } catch { }
    }

    // ---------- 工具 ----------

    private void Post(Action action)
    {
        if (_ui is null || !_ui.IsHandleCreated) { action(); return; }
        if (_ui.InvokeRequired) _ui.BeginInvoke(action);
        else action();
    }

    private void Notify(string title, string message, ToolTipIcon icon)
        => _tray.ShowBalloonTip(5000, title, message, icon);

    private static string Shorten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 16 ? s : s[..16] + "…";
    }

    private void Shutdown()
    {
        _mouse.Shutdown();
        _hotkey.Shutdown();
        _tray.Visible = false;
        _tray.Dispose();
        _store?.Dispose();
        _context.ExitThread();
    }
}
