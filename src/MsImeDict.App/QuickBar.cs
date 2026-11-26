using System.Drawing;
using System.Windows.Forms;

namespace MsImeDict.App;

/// <summary>
/// 选区旁的浮条。不抢焦点（WS_EX_NOACTIVATE），点按钮不会打断用户正在操作的窗口。
/// </summary>
public sealed class QuickBar : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private readonly Label _preview;
    private readonly Button _add;
    private readonly System.Windows.Forms.Timer _lifeTimer;

    public event Action<string>? AddRequested;

    private string _text = "";
    private DateTime _shownAt;

    public QuickBar()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(250, 250, 250);
        Padding = new Padding(6);

        _preview = new Label
        {
            AutoSize = false,
            Width = 168,
            Height = 34,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(60, 60, 60),
            AutoEllipsis = true
        };

        _add = new Button
        {
            Text = "加入词典",
            FlatStyle = FlatStyle.Flat,
            Height = 30,
            Width = 88,
            BackColor = Color.FromArgb(240, 244, 250),
            ForeColor = Color.FromArgb(30, 60, 110),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        _add.FlatAppearance.BorderSize = 0;
        _add.Click += (_, _) =>
        {
            var t = _text;
            Hide();
            AddRequested?.Invoke(t);
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };
        flow.Controls.Add(_preview);
        flow.Controls.Add(_add);
        Controls.Add(flow);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        // 浮条自己管生命周期：鼠标不悬停就 4 秒后自动消失。
        // 不靠全局鼠标钩子来关它——钩子回调必须极快，不能做界面判断。
        _lifeTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _lifeTimer.Tick += (_, _) =>
        {
            if (!Visible) { _lifeTimer.Stop(); return; }
            if (Bounds.Contains(Cursor.Position)) { _shownAt = DateTime.UtcNow; return; }
            if ((DateTime.UtcNow - _shownAt).TotalSeconds > 4) { Hide(); _lifeTimer.Stop(); }
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    public void ShowAt(string text, Point anchor)
    {
        _text = text;
        _preview.Text = Shorten(text);
        _preview.Width = Math.Max(60, Math.Min(240, TextRenderer.MeasureText(_preview.Text, Font).Width + 10));

        var screen = Screen.FromPoint(anchor).WorkingArea;
        var x = anchor.X + 12;
        var y = anchor.Y + 16;
        if (x + Width > screen.Right) x = Math.Max(screen.Left, screen.Right - Width - 8);
        if (y + Height > screen.Bottom) y = Math.Max(screen.Top, anchor.Y - Height - 12);
        Location = new Point(x, y);

        _shownAt = DateTime.UtcNow;
        if (!Visible) Show();
        else Location = new Point(x, y);
        _lifeTimer.Start();
    }

    private static string Shorten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 20 ? s : s[..20] + "…";
    }

    public void HideBar()
    {
        _lifeTimer.Stop();
        if (Visible) Hide();
    }
}
