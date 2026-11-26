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
    private readonly HotkeyForm _hotkey = new();
    private readonly MouseHook _mouse = new();
    private readonly QuickBar _bar = new();
    private readonly Toast _toast = new();

    private PhraseStore? _store;
    private Control? _ui;
    private string _lastShown = "";
    private KeyboardHook? _keyHook;
    private ContextMenuStrip? _menu;
    private DateTime _lastHotkeyAt = DateTime.MinValue;

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
        menu.Items.Add("管理词库…", null, (_, _) => OpenManager());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("同步到私有仓库", null, (_, _) => SyncNow());
        menu.Items.Add("词库目录", null, (_, _) => OpenFolder(Paths.AppDataDir));
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;

        _tray.DoubleClick += (_, _) => OpenManager();
    }

    public void Initialize()
    {
        // 隐藏主窗口，用托盘消息驱动
        var form = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false, Opacity = 0 };
        _context.MainForm = form;
        form.Show();
        form.Hide();
        _ui = form;

        // 热键承载窗口：真实 Form，屏幕外 1x1
        _hotkey.EnsureHandle();
        _hotkey.Triggered += combo => FireHotkey(combo);

        // 第二条通道：低级键盘钩子自己认组合，绕开 RegisterHotKey 的消息投递
        _keyHook = new KeyboardHook();
        _keyHook.Triggered += () =>
        {
            Log.Write($"keyboard hook fired ({_settings.Hotkey})");
            _keyHook.Reset();
            FireHotkey("hook");
        };
        var kbHookOk = _keyHook.Start();
        Log.Write($"keyboard hook started={kbHookOk}");

        RegisterHotkeys();

        _bar.AddRequested += text => AddPhrase(text, openDialog: false);
        _bar.MoreRequested += ShowMoreMenu;

        _mouse.DragSelected += OnDragSelected;
        _mouse.CtrlRightClicked += OnCtrlRightClicked;
        _mouse.AnyMouseDown += pt =>
        {
            if (_bar.Visible && !_bar.Bounds.Contains(pt)) _bar.HideBar();
            if (_menu is not null && _menu.Visible && !_menu.Bounds.Contains(pt)) CloseMenu();
        };
        AccessibilityEnabler.Enable();

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
        CloseMenu();

        var menu = new ContextMenuStrip();
        menu.Items.Add($"「{Shorten(text)}」", null, (_, _) => { });
        ((ToolStripMenuItem)menu.Items[0]!).Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("加入个人词典", null, (_, _) => { CloseMenu(); AddPhrase(text, openDialog: false); });
        menu.Items.Add("加入并指定编码…", null, (_, _) => { CloseMenu(); AddPhrase(text, openDialog: true); });
        menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) _menu = null; };

        _menu = menu;
        menu.Show(at);
    }

    /// <summary>
    /// 关闭当前菜单。
    ///
    /// 菜单的「点外部关闭」依赖 WinForms 的鼠标捕获，会被全局低级鼠标钩子干扰，
    /// 实测出现「打开后关不掉」。这里改为自己管生命周期：任何鼠标按下、Esc、
    /// 或再次打开菜单时都显式关掉。
    /// </summary>
    private void CloseMenu()
    {
        if (_menu is null) return;
        try { _menu.Close(); } catch { }
        _menu = null;
    }

    private bool IsOwnWindow()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ---------- 取词 ----------

    /// <summary>热键触发入口。RegisterHotKey 与键盘钩子两条通道都走这里，用冷却去重。</summary>
    private void FireHotkey(string source)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHotkeyAt).TotalMilliseconds < 500) return;
        _lastHotkeyAt = now;
        Log.Write($"hotkey -> capture ({source})");
        Post(CaptureSelection);
    }

    private void CaptureSelection()
    {
        var text = StaRunner.Run(() => SelectionReader.Read(), 2500);
        Log.Write("capture: length=" + (text?.Length ?? -1));

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify("没有选中文字", "先在目标窗口里选中文字，再按热键", ToolTipIcon.Info);
            return;
        }

        // 热键是「一步到位」：读到什么就直接加入，不弹对话框
        AddPhrase(text.Trim(), openDialog: false);
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
            {
                Log.Write($"added: {word} -> {string.Join(",", codes)}");
                Post(() => _toast.ShowToast($"「{Shorten(word)}」已加入词典"));
                if (_settings.SyncEnabled) SyncInBackground();
            }
            else
            {
                Notify("写入失败", result.Message, ToolTipIcon.Error);
            }
        }
        catch (Exception ex)
        {
            Notify("出错了", ex.Message, ToolTipIcon.Error);
        }
    }

    // ---------- 菜单动作 ----------

    private void OpenManager()
    {
        try
        {
            _store ??= new PhraseStore();
            using var form = new PhraseManagerForm(_store);
            form.ShowDialog();
        }
        catch (Exception ex) { Notify("打开词库失败", ex.Message, ToolTipIcon.Error); }
    }

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

    private int _syncing;

    /// <summary>后台同步：失败只写日志，不打扰用户；同一时刻只跑一个。</summary>
    private void SyncInBackground()
    {
        if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0) return;
        Task.Run(() =>
        {
            try
            {
                if (_store is null) return;
                var result = new SyncAgent(_store).Sync(new Core.SyncSettings
                {
                    Enabled = _settings.SyncEnabled,
                    RepoPath = _settings.RepoPath,
                    RemoteUrl = _settings.RemoteUrl,
                    Branch = _settings.Branch
                });
                Log.Write($"background sync: ok={result.Ok} pushed={result.Pushed} pulled={result.Pulled} {result.Message}");
            }
            catch (Exception ex) { Log.Write("background sync failed: " + ex.Message); }
            finally { Interlocked.Exchange(ref _syncing, 0); }
        });
    }

    private void OpenSettings()
    {
        try
        {
            using var form = new SettingsForm(_settings);
            if (form.ShowDialog() != DialogResult.OK) return;
            ApplyHotkeySettings();
            if (form.SyncRequested) SyncNow();
            else Notify("设置已保存", "快捷键：" + _settings.Hotkey, ToolTipIcon.Info);
        }
        catch (Exception ex) { Notify("保存设置失败", ex.Message, ToolTipIcon.Error); }
    }

    /// <summary>按当前设置注册热键。设置里改过热键后会重新调用。</summary>
    private void RegisterHotkeys()
    {
        if (HotkeyForm.TryParse(_settings.Hotkey, out var mods, out var vk))
        {
            _keyHook!.TargetVk = vk;
            _keyHook.NeedCtrl = (mods & 0x0002) != 0;
            _keyHook.NeedAlt = (mods & 0x0001) != 0;
            _keyHook.NeedShift = (mods & 0x0004) != 0;
            _keyHook.NeedWin = (mods & 0x0008) != 0;
        }

        var combos = new List<string> { _settings.Hotkey };
        foreach (var extra in new[] { "Ctrl+Alt+F9", "Ctrl+Shift+D" })
            if (!combos.Contains(extra, StringComparer.OrdinalIgnoreCase)) combos.Add(extra);

        var (okList, failedList) = _hotkey.RegisterAll(combos);
        Log.Write($"hotkey register ok=[{string.Join(",", okList)}] failed=[{string.Join(",", failedList)}]");
        if (okList.Count == 0)
            Notify("热键注册失败", "这个组合被占用了：" + string.Join(",", failedList), ToolTipIcon.Warning);
    }

    /// <summary>设置里改了热键后重新注册。</summary>
    private void ApplyHotkeySettings()
    {
        RegisterHotkeys();
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
        _hotkey.Close();
        _keyHook?.Shutdown();
        AccessibilityEnabler.Restore();
        _tray.Visible = false;
        _tray.Dispose();
        _store?.Dispose();
        _context.ExitThread();
    }
}
