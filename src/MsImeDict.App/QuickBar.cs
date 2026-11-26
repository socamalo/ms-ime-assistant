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
    private readonly Button _menu;
    private string _text = "";

    public event Action<string>? AddRequested;
    public event Action<string, Point>? MoreRequested;

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

        _add = MakeButton("加入词典");
        _add.Click += (_, _) => { var t = _text; Hide(); AddRequested?.Invoke(t); };

        _menu = MakeButton("…");
        _menu.Width = 30;
        _menu.Click += (_, _) =>
        {
            var t = _text;
            var at = Location;
            Hide();
            MoreRequested?.Invoke(t, at);
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
        flow.Controls.Add(_menu);
        Controls.Add(flow);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        Height = 30,
        Width = 74,
        BackColor = Color.FromArgb(240, 244, 250),
        ForeColor = Color.FromArgb(30, 60, 110),
        FlatAppearance = { BorderSize = 0 },
        Cursor = Cursors.Hand,
        TabStop = false
    };

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

    /// <summary>在屏幕坐标 anchor 附近显示。</summary>
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

        if (!Visible) Show();
        else { Location = new Point(x, y); }
    }

    private static string Shorten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 20 ? s : s[..20] + "…";
    }

    public void HideBar()
    {
        if (Visible) Hide();
    }
}
