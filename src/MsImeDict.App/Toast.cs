using System.Drawing;
using System.Windows.Forms;

namespace MsImeDict.App;

/// <summary>
/// 轻提示：在鼠标附近浮一条消息，1.2 秒后自动消失。
/// 不抢焦点、不进任务栏，比系统气泡更可靠（气泡会被「专注助手」静音）。
/// </summary>
public sealed class Toast : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _timer;

    public Toast()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(38, 38, 38);
        Padding = new Padding(14, 9, 14, 9);
        Opacity = 0.96;

        _label = new Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10f),
            BackColor = Color.Transparent
        };
        Controls.Add(_label);

        _timer = new System.Windows.Forms.Timer { Interval = 1200 };
        _timer.Tick += (_, _) => { _timer.Stop(); Hide(); };
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

    public void ShowToast(string message, Point? near = null)
    {
        _label.Text = message;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var anchor = near ?? Cursor.Position;
        var screen = Screen.FromPoint(anchor).WorkingArea;
        var x = anchor.X + 14;
        var y = anchor.Y + 20;
        if (x + Width > screen.Right) x = Math.Max(screen.Left, screen.Right - Width - 10);
        if (y + Height > screen.Bottom) y = Math.Max(screen.Top, anchor.Y - Height - 14);
        Location = new Point(x, y);

        if (!Visible) Show();
        BringToTopWithoutActivation();

        _timer.Stop();
        _timer.Start();
    }

    private void BringToTopWithoutActivation()
    {
        // Show() 之后再设置一次位置，避免首次显示时尺寸未计算导致越界
        var w = Width;
        var h = Height;
        var screen = Screen.FromPoint(Location).WorkingArea;
        if (Location.X + w > screen.Right) Location = new Point(screen.Right - w - 10, Location.Y);
        if (Location.Y + h > screen.Bottom) Location = new Point(Location.X, screen.Bottom - h - 10);
    }
}
