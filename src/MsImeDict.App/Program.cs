using System.Diagnostics;
using System.IO;
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

/// <summary>
/// 托盘程序。
///
/// 取词只读系统剪贴板，不查询任何应用（不用 UIA、不发按键、不改系统设置），
/// 所以对 Electron、自绘界面、终端、浏览器一视同仁。
/// </summary>
public sealed class TrayHost
{
    private readonly ApplicationContext _context;
    private readonly NotifyIcon _tray;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HotkeyForm _hotkey = new();
    private readonly Toast _toast = new();
    private readonly KeyboardHook _keyHook = new();
    private readonly MouseHook _mouse = new();
    private readonly QuickBar _bar = new();

    private PhraseStore? _store;
    private Control? _ui;
    private DateTime _lastHotkeyAt = DateTime.MinValue;
    private string _lastShown = "";
    private int _syncing;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public TrayHost(ApplicationContext context)
    {
        _context = context;
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "微软输入法助手",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("管理词库…", null, (_, _) => OpenManager());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("同步到私有仓库", null, (_, _) => SyncNow());
        menu.Items.Add("词库目录", null, (_, _) => OpenFolder(Paths.AppDataDir));
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("关于", null, (_, _) => ShowAbout());
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;

        _tray.DoubleClick += (_, _) => OpenManager();
    }

    public void Initialize()
    {
        var form = new Form { WindowState = FormWindowState.Minimized, ShowInTaskbar = false, Opacity = 0 };
        _context.MainForm = form;
        form.Show();
        form.Hide();
        _ui = form;

        _hotkey.EnsureHandle();
        _hotkey.Triggered += combo => FireHotkey(combo);

        _keyHook.Triggered += () =>
        {
            Log.Write($"keyboard hook fired ({_settings.Hotkey})");
            _keyHook.Reset();
            FireHotkey("hook");
        };
        Log.Write($"keyboard hook started={_keyHook.Start()}");

        RegisterHotkeys();
        Log.Write($"start: hotkey={_settings.Hotkey}, 取词=选区(UIA)+剪贴板(只读)");

        // 划词浮条：只读选区，不干预任何应用
        _bar.AddRequested += text => AddPhrase(text, openDialog: false);
        _mouse.DragSelected += OnDragSelected;
        Log.Write($"mouse hook started={_mouse.Start()}");
    }

    /// <summary>拖选结束后读选区，在光标旁弹出浮条。</summary>
    private void OnDragSelected(Point anchor)
    {
        if (IsOwnWindow()) return;

        // 等目标应用把选区落到 UI Automation 上；某些窗口会卡，超时收紧
        System.Threading.Thread.Sleep(160);
        var text = StaRunner.Run(() => SelectionReader.Read(preferClipboard: false), 900);
        if (string.IsNullOrWhiteSpace(text)) return;

        text = text.Trim();
        if (text.Length == 0 || text.Length > 300) return;

        Post(() =>
        {
            if (text == _lastShown && _bar.Visible) return;
            _lastShown = text;
            _bar.ShowAt(text, anchor);
        });
    }

    private bool IsOwnWindow()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ---------- 热键 ----------

    private void RegisterHotkeys()
    {
        if (HotkeyForm.TryParse(_settings.Hotkey, out var mods, out var vk))
        {
            _keyHook.TargetVk = vk;
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

    /// <summary>两条通道都走这里，用冷却去重。</summary>
    private void FireHotkey(string source)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHotkeyAt).TotalMilliseconds < 500) return;
        _lastHotkeyAt = now;
        Log.Write($"hotkey trigged ({source})");
        Post(CaptureClipboard);
    }

    // ---------- 取词 ----------

    /// <summary>读剪贴板 -> 入库 -> 写入输入法 -> 提示。</summary>
    private void CaptureClipboard()
    {
        var text = StaRunner.Run(() => SelectionReader.Read(), 2000);
        Log.Write($"capture: length={text?.Length ?? -1} source={SelectionReader.LastSource}");

        if (string.IsNullOrWhiteSpace(text))
        {
            var reason = SelectionReader.LastClipboardReason;
            Notify("没有取到文字",
                string.IsNullOrEmpty(reason)
                    ? "先选中文字按 Ctrl+C 复制，再按一次热键"
                    : reason + "。先按 Ctrl+C 复制要加入的词，再按热键",
                ToolTipIcon.Info);
            return;
        }

        AddPhrase(text.Trim(), openDialog: false);
    }

    // ---------- 加词 ----------

    private void AddPhrase(string text, bool openDialog)
    {
        try
        {
            _store ??= new PhraseStore();

            // 加词前先查一遍：同一个词不管从哪个入口进来，都不重复入库
            var existing = _store.FindByText(text);
            if (existing is not null)
            {
                Log.Write($"skip duplicate: {text}");
                var abbrText = existing.Abbr.Length == 0 ? "（无）" : existing.Abbr;
                if (openDialog)
                    MessageBox.Show($"「{text}」已经在词典里了。\n\n编码：{existing.Code}\n简拼：{abbrText}",
                        "已在词典中", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    Post(() => _toast.ShowToast($"「{Shorten(text)}」已在词典中"));
                return;
            }

            var encoded = new PinyinEncoder().Encode(text);
            var info = new EncodedInfo
            {
                Codes = encoded.CandidateCodes.ToList(),
                Notes = encoded.Notes.ToList()
            };

            string word;
            string code;
            string abbr;
            int rank;

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
                code = form.Code;
                abbr = form.Abbr;
                rank = form.Rank;
            }
            else
            {
                word = text;
                code = info.Codes.FirstOrDefault() ?? "";
                abbr = PinyinEncoder.AbbreviationOf(word);
                rank = _settings.DefaultRank;
            }

            if (code.Length == 0)
            {
                Notify("无法生成编码", "这段内容里没有可编码的汉字或字母", ToolTipIcon.Warning);
                return;
            }

            var phrase = _store.Add(word, code, rank, source: "clipboard", abbr: abbr);

            var result = new ImeDeployer().Deploy(_store.Project());
            if (!result.Ok)
            {
                Notify("写入输入法失败", result.Message, ToolTipIcon.Error);
                return;
            }

            var abbrShown = phrase.Abbr.Length == 0 ? "无简拼" : phrase.Abbr;
            Log.Write($"added: {word} -> {code} / {abbrShown}");
            Post(() => _toast.ShowToast($"「{Shorten(word)}」已加入词典"));
            if (_settings.SyncEnabled) SyncInBackground();
        }
        catch (Exception ex)
        {
            Notify("出错了", ex.Message, ToolTipIcon.Error);
        }
    }

    // ---------- 菜单 ----------

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

    private void OpenSettings()
    {
        try
        {
            using var form = new SettingsForm(_settings);
            if (form.ShowDialog() != DialogResult.OK) return;
            RegisterHotkeys();
            if (form.SyncRequested) SyncNow();
            else Notify("设置已保存", "快捷键：" + _settings.Hotkey, ToolTipIcon.Info);
        }
        catch (Exception ex) { Notify("保存设置失败", ex.Message, ToolTipIcon.Error); }
    }

    private void ShowAbout()
    {
        var version = AppInfo.Version;
        var wordCount = _store?.List().Count ?? 0;
        var text = string.Join(Environment.NewLine, new[]
        {
            "微软输入法助手",
            $"版本 {version}",
            "",
            "作者：Daniel Ding",
            "联系方式：flaming.ding@gmail.com",
            "",
            "取词方式：只读系统剪贴板（先 Ctrl+C 复制，再按热键）",
            "词条数据仓库：socamalo/ms-ime-dict-data（私有）",
            $"本地词库：{Paths.DatabaseFile}（{wordCount} 个词）",
            $"输入法文件：{Paths.LexFile}"
        });
        MessageBox.Show(text, "关于 微软输入法助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>远端设置更新时应用它：写盘 + 重新注册热键。</summary>
    private void ApplyRemoteSettings(SyncResult result)
    {
        if (result.RemoteSettings is null) return;
        try
        {
            _settings.ApplyPortable(result.RemoteSettings);
            _settings.Save();
            Post(() =>
            {
                RegisterHotkeys();
                _toast.ShowToast($"已应用另一台设备的设置（{_settings.Hotkey}）");
            });
            Log.Write($"settings applied from remote: {_settings.Hotkey}");
        }
        catch (Exception ex) { Log.Write("apply remote settings failed: " + ex.Message); }
    }

    private void SyncNow()
    {
        // 必须在后台线程跑：git 联网要几秒到几十秒，
        // 而这个线程挂着鼠标/键盘的低级钩子，阻塞它会让全系统输入卡住。
        if (Interlocked.CompareExchange(ref _syncing, 1, 0) != 0)
        {
            Notify("正在同步", "上一轮同步还没结束", ToolTipIcon.Info);
            return;
        }

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
                    Branch = _settings.Branch,
                    LocalSettings = _settings.ToPortable()
                });

                ApplyRemoteSettings(result);

                if (result.Ok && result.Pulled > 0)
                {
                    var deploy = new ImeDeployer().Deploy(_store.Project());
                    Log.Write($"sync: pulled={result.Pulled}, deploy ok={deploy.Ok} entries={deploy.EntryCount}");
                    Post(() => Notify(deploy.Ok ? "同步完成" : "同步成功，写入失败",
                        deploy.Ok ? $"拉取 {result.Pulled} 条，已写入输入法（共 {deploy.EntryCount} 条）" : deploy.Message,
                        deploy.Ok ? ToolTipIcon.Info : ToolTipIcon.Error));
                }
                else
                {
                    Log.Write($"sync: ok={result.Ok} pushed={result.Pushed} pulled={result.Pulled} {result.Message}");
                    Post(() => Notify(result.Ok ? "同步完成" : "同步失败", result.Message,
                        result.Ok ? ToolTipIcon.Info : ToolTipIcon.Error));
                }
            }
            catch (Exception ex)
            {
                Log.Write("sync failed: " + ex.Message);
                Post(() => Notify("同步出错", ex.Message, ToolTipIcon.Error));
            }
            finally { Interlocked.Exchange(ref _syncing, 0); }
        });
    }

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
                    Branch = _settings.Branch,
                    LocalSettings = _settings.ToPortable()
                });

                ApplyRemoteSettings(result);

                if (result.Ok && result.Pulled > 0)
                {
                    var deploy = new ImeDeployer().Deploy(_store.Project());
                    Log.Write($"background sync: pulled={result.Pulled}, deploy ok={deploy.Ok} entries={deploy.EntryCount}");
                    Post(() => _toast.ShowToast($"已同步 {result.Pulled} 条并写入输入法"));
                }
                else
                {
                    Log.Write($"background sync: ok={result.Ok} pushed={result.Pushed} pulled={result.Pulled} {result.Message}");
                }
            }
            catch (Exception ex) { Log.Write("background sync failed: " + ex.Message); }
            finally { Interlocked.Exchange(ref _syncing, 0); }
        });
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
        _keyHook.Shutdown();
        _hotkey.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _store?.Dispose();
        _context.ExitThread();
    }
}
