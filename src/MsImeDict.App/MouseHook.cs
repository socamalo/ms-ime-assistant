using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MsImeDict.App;

/// <summary>
/// 全局鼠标监听。用 WH_MOUSE_LL，不注入任何进程。
///
/// 只在两种情况下通知上层：
///   1 左键拖选结束（按下到抬起位移超过阈值）
///   2 Ctrl + 右键按下（唤起我们的菜单，不干扰普通右键）
/// </summary>
public sealed class MouseHook : NativeWindow
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int VK_CONTROL = 0x11;
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
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

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

    private readonly HookProc _proc;      // 必须持有引用，否则会被 GC 回收导致回调崩溃
    private IntPtr _hook = IntPtr.Zero;
    private POINT _downPoint;
    private bool _dragging;

    /// <summary>拖选结束，参数为选区终点（屏幕坐标）。</summary>
    public event Action<Point>? DragSelected;
    /// <summary>Ctrl + 右键按下，参数为按下位置（屏幕坐标）。</summary>
    public event Action<Point>? CtrlRightClicked;
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

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public void Shutdown()
    {
        Stop();
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
                    // 交给上层做延迟读取，回调里不能耗时
                    Task.Run(() => DragSelected?.Invoke(end));
                }
                break;

            case WM_RBUTTONDOWN:
                if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
                {
                    var at = pt;
                    Task.Run(() => CtrlRightClicked?.Invoke(at));
                }
                break;
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
