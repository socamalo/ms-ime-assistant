using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MsImeDict.App;

/// <summary>
/// 选区旁的浮条。不抢焦点（WS_EX_NOACTIVATE），点按钮不会打断用户正在操作的窗口。
///
/// 生命周期自己管，四条退出条件（任一条先到就收起）：
/// 1. 鼠标离开浮条 1.2 秒
/// 2. 从显示算起满 5 秒——鼠标一直压在浮条上也一样，它盖在网页上是挡路的
/// 3. 在别处按下鼠标、或滚动页面（由 <see cref="MouseHook"/> 通知）
/// 4. 前台窗口换了（Alt+Tab、点到别的窗口）
/// </summary>
public sealed class QuickBar : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>鼠标离开浮条后停留多久。</summary>
    private const int LingerMs = 1200;

    /// <summary>从显示算起的硬上限，鼠标停在上面也不会超过这个时间。</summary>
    private const int MaxVisibleMs = 5000;

    private const int TickMs = 200;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly Label _preview;
    private readonly Button _add;
    private readonly System.Windows.Forms.Timer _lifeTimer;

    public event Action<string>? AddRequested;

    private string _text = "";
    private DateTime _shownAt;
    private DateTime? _leftAt;
    private IntPtr _owner;

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
            Dismiss();
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

        // 浮条自己管生命周期：不靠全局鼠标钩子做界面判断，
        // 钩子回调必须极快，只负责通知「别处被点了」。
        _lifeTimer = new System.Windows.Forms.Timer { Interval = TickMs };
        _lifeTimer.Tick += (_, _) => Tick();
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

    private void Tick()
    {
        if (!Visible) { _lifeTimer.Stop(); return; }

        var now = DateTime.UtcNow;

        // 硬上限：鼠标一直停在浮条上也到点收走，避免盖住网页内容
        if ((now - _shownAt).TotalMilliseconds >= MaxVisibleMs) { Dismiss("timeout"); return; }

        // 前台窗口换了就收：用户已经去看别的窗口了
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && _owner != IntPtr.Zero && fg != _owner) { Dismiss("foreground changed"); return; }

        if (Bounds.Contains(Cursor.Position)) { _leftAt = null; return; }

        _leftAt ??= now;
        if ((now - _leftAt.Value).TotalMilliseconds >= LingerMs) Dismiss("mouse left");
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

        _owner = GetForegroundWindow();
        _shownAt = DateTime.UtcNow;
        _leftAt = null;

        if (!Visible)
        {
            Show();
            Log.Write($"quickbar shown at {x},{y}（5 秒内必收）");
        }
        else Location = new Point(x, y);
        _lifeTimer.Start();
    }

    /// <summary>这个屏幕坐标是否落在浮条上。别处点击时用来判断该不该收。</summary>
    public bool ContainsPoint(Point screenPoint) => Visible && Bounds.Contains(screenPoint);

    /// <summary>收起浮条。鼠标在别处按下、滚动页面、切窗口都走这里。</summary>
    public void Dismiss(string reason = "explicit")
    {
        _lifeTimer.Stop();
        _leftAt = null;
        _owner = IntPtr.Zero;
        if (!Visible) return;
        Log.Write($"quickbar dismissed ({reason})");
        Hide();
    }

    private static string Shorten(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 20 ? s : s[..20] + "…";
    }
}
