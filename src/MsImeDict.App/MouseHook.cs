using System.Runtime.InteropServices;

namespace MsImeDict.App;

/// <summary>
/// 全局鼠标监听，用于「划词后弹出浮条」。
///
/// 用 WH_MOUSE_LL，只观察不拦截（永远 CallNextHookEx），
/// 不注入进程、不改别人窗口、不发按键。
/// 回调里只判断坐标，读词放到后台线程，避免拖慢鼠标。
/// </summary>
public sealed class MouseHook : NativeWindow
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int DragThreshold = 6;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private readonly HookProc _proc;   // 必须持有引用，否则会被 GC 回收导致回调崩溃
    private IntPtr _hook = IntPtr.Zero;
    private POINT _downPoint;
    private bool _dragging;

    /// <summary>拖选结束，参数为松开位置（屏幕坐标）。</summary>
    public event Action<Point>? DragSelected;
    /// <summary>任意鼠标按下，用于隐藏浮条。</summary>
    public event Action<Point>? AnyMouseDown;

    public MouseHook()
    {
        _proc = Callback;
        var cp = new CreateParams { Caption = "MsImeDictMouseHook", Parent = new IntPtr(-3) };
        CreateHandle(cp);
    }

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    public void Shutdown()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        var pt = new Point(data.pt.X, data.pt.Y);

        switch (msg)
        {
            case WM_LBUTTONDOWN:
                _downPoint = data.pt;
                _dragging = false;
                AnyMouseDown?.Invoke(pt);
                break;

            case WM_MOUSEMOVE:
                if (!_dragging)
                {
                    var dx = Math.Abs(data.pt.X - _downPoint.X);
                    var dy = Math.Abs(data.pt.Y - _downPoint.Y);
                    if (dx > DragThreshold || dy > DragThreshold) _dragging = true;
                }
                break;

            case WM_LBUTTONUP:
                if (_dragging)
                {
                    _dragging = false;
                    var end = pt;
                    Task.Run(() => DragSelected?.Invoke(end));
                }
                break;
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
