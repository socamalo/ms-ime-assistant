using System.Drawing;
using System.Windows.Forms;
using MsImeDict.Core;

namespace MsImeDict.App;

/// <summary>
/// 设置界面。
///
/// 快捷键不用自由输入：勾选修饰键 + 下拉选主键，杜绝五花八门的输入把解析搞崩。
/// </summary>
public sealed class SettingsForm : Form
{
    private static readonly string[] Keys =
        "A B C D E F G H I J K L M N O P Q R S T U V W X Y Z 0 1 2 3 4 5 6 7 8 9 F1 F2 F3 F4 F5 F6 F7 F8 F9 F10 F11 F12"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private readonly AppSettings _settings;
    private readonly CheckBox _ctrl = new() { Text = "Ctrl", Width = 58 };
    private readonly CheckBox _alt = new() { Text = "Alt", Width = 52 };
    private readonly CheckBox _shift = new() { Text = "Shift", Width = 60 };
    private readonly CheckBox _win = new() { Text = "Win", Width = 56 };
    private readonly ComboBox _keyBox = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _preview = new() { AutoSize = true, ForeColor = Color.FromArgb(30, 60, 110) };

    private readonly CheckBox _syncEnabled;
    private readonly CheckBox _autoStart;
    private readonly TextBox _remoteUrl;
    private readonly TextBox _repoPath;

    /// <summary>用户点了「保存并同步」。</summary>
    public bool SyncRequested { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Text = "设置 - 微软输入法助手";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(600, 420);
        Font = new Font("Microsoft YaHei UI", 9f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16, 14, 16, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ---- 快捷键 ----
        layout.Controls.Add(MakeLabel("快捷键"), 0, 0);

        var picker = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Height = 32,
            Margin = new Padding(0)
        };
        foreach (var cb in new[] { _ctrl, _alt, _shift, _win })
        {
            cb.Margin = new Padding(0, 6, 4, 0);
            picker.Controls.Add(cb);
        }
        foreach (var k in Keys) _keyBox.Items.Add(k);
        _keyBox.Margin = new Padding(8, 4, 8, 0);
        picker.Controls.Add(_keyBox);

        _preview.Margin = new Padding(0, 10, 0, 0);
        picker.Controls.Add(_preview);
        layout.Controls.Add(picker, 1, 0);

        layout.Controls.Add(MakeHint("勾选修饰键，再选一个主键。例如 Ctrl + Q。至少要有一个修饰键。"), 1, 1);

        // 回填现有设置
        FillFrom(settings.Hotkey);

        _ctrl.CheckedChanged += (_, _) => UpdatePreview();
        _alt.CheckedChanged += (_, _) => UpdatePreview();
        _shift.CheckedChanged += (_, _) => UpdatePreview();
        _win.CheckedChanged += (_, _) => UpdatePreview();
        _keyBox.SelectedIndexChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        // ---- 同步 ----
        layout.Controls.Add(MakeLabel("同步"), 0, 2);
        _syncEnabled = new CheckBox
        {
            Text = "同步到私有仓库",
            Checked = settings.SyncEnabled,
            Dock = DockStyle.Fill,
            Height = 26
        };
        layout.Controls.Add(_syncEnabled, 1, 2);

        _autoStart = new CheckBox
        {
            Text = "开机自动启动",
            Checked = settings.AutoStart,
            Dock = DockStyle.Fill,
            Height = 26
        };
        layout.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 0, 3);
        layout.Controls.Add(_autoStart, 1, 3);

        layout.Controls.Add(MakeLabel("仓库地址"), 0, 4);
        _remoteUrl = new TextBox
        {
            Text = settings.RemoteUrl,
            Dock = DockStyle.Fill,
            PlaceholderText = "https://github.com/用户名/仓库名.git"
        };
        layout.Controls.Add(_remoteUrl, 1, 4);
        layout.Controls.Add(MakeHint("贴一个空的私有仓库地址。首次同步会自己初始化、推送，之后双向合并。"), 1, 5);

        layout.Controls.Add(MakeLabel("工作目录"), 0, 6);
        _repoPath = new TextBox { Text = settings.RepoPath, Dock = DockStyle.Fill };
        layout.Controls.Add(_repoPath, 1, 6);
        layout.Controls.Add(MakeHint("本机 git 工作副本。留空用 %LOCALAPPDATA%\\MsImeDict\\sync-repo。"), 1, 7);

        var footer = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            Padding = new Padding(16, 2, 16, 0),
            ForeColor = Color.Gray,
            Text = "备份目录：" + Paths.BackupDir
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 12, 0)
        };
        var save = new Button { Text = "保存", Width = 92, Height = 30 };
        var cancel = new Button { Text = "取消", Width = 92, Height = 30, DialogResult = DialogResult.Cancel };
        var saveSync = new Button { Text = "保存并同步", Width = 110, Height = 30 };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(saveSync);

        save.Click += (_, _) => { if (Commit()) { DialogResult = DialogResult.OK; Close(); } };
        saveSync.Click += (_, _) =>
        {
            if (!Commit()) return;
            SyncRequested = true;
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.Add(layout);
        Controls.Add(footer);
        Controls.Add(buttons);

        AcceptButton = save;
        CancelButton = cancel;
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill,
        Height = 28
    };

    private static Label MakeHint(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Height = 32,
        ForeColor = Color.Gray,
        AutoSize = false
    };

    /// <summary>把 "Ctrl+Alt+J" 这类字符串回填到控件上；解析不了就用默认 Ctrl+Q。</summary>
    private void FillFrom(string hotkey)
    {
        var parts = (hotkey ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": _ctrl.Checked = true; break;
                case "alt": _alt.Checked = true; break;
                case "shift": _shift.Checked = true; break;
                case "win" or "windows": _win.Checked = true; break;
                default:
                    var idx = Array.IndexOf(Keys, p.ToUpperInvariant());
                    if (idx >= 0) _keyBox.SelectedIndex = idx;
                    break;
            }
        }

        if (!_ctrl.Checked && !_alt.Checked && !_shift.Checked && !_win.Checked) _ctrl.Checked = true;
        if (_keyBox.SelectedIndex < 0) _keyBox.SelectedItem = "Q";
    }

    private string BuildHotkey()
    {
        var parts = new List<string>();
        if (_ctrl.Checked) parts.Add("Ctrl");
        if (_alt.Checked) parts.Add("Alt");
        if (_shift.Checked) parts.Add("Shift");
        if (_win.Checked) parts.Add("Win");
        if (_keyBox.SelectedItem is string key) parts.Add(key);
        return string.Join("+", parts);
    }

    private void UpdatePreview()
    {
        _preview.Text = string.IsNullOrEmpty(BuildHotkey()) ? "" : "= " + BuildHotkey();
    }

    private bool Commit()
    {
        if (!_ctrl.Checked && !_alt.Checked && !_shift.Checked && !_win.Checked)
        {
            MessageBox.Show(this, "至少要勾选一个修饰键（Ctrl / Alt / Shift / Win）。",
                "快捷键不完整", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (_keyBox.SelectedItem is not string)
        {
            MessageBox.Show(this, "请选择一个主键。", "快捷键不完整", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _keyBox.Focus();
            return false;
        }

        var hotkey = BuildHotkey();
        if (!HotkeyForm.TryParse(hotkey, out _, out _))
        {
            MessageBox.Show(this, $"快捷键 {hotkey} 无法使用，请换一个组合。",
                "快捷键无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var url = _remoteUrl.Text.Trim();
        if (_syncEnabled.Checked && url.Length > 0 && !LooksLikeRepoUrl(url))
        {
            MessageBox.Show(this, "仓库地址看起来不对。示例：https://github.com/用户名/仓库名.git",
                "地址无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _remoteUrl.Focus();
            return false;
        }

        _settings.Hotkey = hotkey;
        _settings.SyncEnabled = _syncEnabled.Checked;
        _settings.AutoStart = _autoStart.Checked;
        _settings.RemoteUrl = url;
        _settings.RepoPath = _repoPath.Text.Trim();
        _settings.Save();

        AutoStart.Apply(_settings.AutoStart);
        return true;
    }

    private static bool LooksLikeRepoUrl(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith(@"\\", StringComparison.Ordinal);
}
