using System.Drawing;
using System.Windows.Forms;
using MsImeDict.Core;

namespace MsImeDict.App;

/// <summary>
/// 设置界面：热键、同步开关、私有仓库地址。
/// 用户不需要碰 JSON，改完立即生效。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _hotkey;
    private readonly CheckBox _syncEnabled;
    private readonly TextBox _remoteUrl;
    private readonly TextBox _repoPath;

    /// <summary>用户点了「保存并同步」。</summary>
    public bool SyncRequested { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Text = "设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(580, 350);
        Font = new Font("Microsoft YaHei UI", 9f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16, 14, 16, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(MakeLabel("快捷键"), 0, 0);
        _hotkey = new TextBox { Text = settings.Hotkey, Dock = DockStyle.Fill };
        layout.Controls.Add(_hotkey, 1, 0);
        layout.Controls.Add(MakeHint("例如 Ctrl+Q、Ctrl+Alt+J。按下它就把选中的文字加入词典。"), 1, 1);

        layout.Controls.Add(MakeLabel("同步"), 0, 2);
        _syncEnabled = new CheckBox
        {
            Text = "同步到私有仓库",
            Checked = settings.SyncEnabled,
            Dock = DockStyle.Fill,
            Height = 26
        };
        layout.Controls.Add(_syncEnabled, 1, 2);

        layout.Controls.Add(MakeLabel("仓库地址"), 0, 3);
        _remoteUrl = new TextBox
        {
            Text = settings.RemoteUrl,
            Dock = DockStyle.Fill,
            PlaceholderText = "https://github.com/用户名/仓库名.git"
        };
        layout.Controls.Add(_remoteUrl, 1, 3);
        layout.Controls.Add(MakeHint("贴一个空的私有仓库地址。首次同步会自己初始化、推送，之后双向合并。"), 1, 4);

        layout.Controls.Add(MakeLabel("工作目录"), 0, 5);
        _repoPath = new TextBox { Text = settings.RepoPath, Dock = DockStyle.Fill };
        layout.Controls.Add(_repoPath, 1, 5);
        layout.Controls.Add(MakeHint("本机的 git 工作副本。留空则用 %LOCALAPPDATA%\\MsImeDict\\sync-repo。"), 1, 6);

        var footer = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 40,
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

    private bool Commit()
    {
        var hotkey = _hotkey.Text.Trim();
        if (!HotkeyForm.TryParse(hotkey, out _, out _))
        {
            MessageBox.Show(this, "快捷键格式不对。示例：Ctrl+Q、Ctrl+Alt+J、Ctrl+Shift+F9。",
                "快捷键无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _hotkey.Focus();
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
        _settings.RemoteUrl = url;
        _settings.RepoPath = _repoPath.Text.Trim();
        _settings.Save();
        return true;
    }

    private static bool LooksLikeRepoUrl(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith(@"\\", StringComparison.Ordinal);
}
