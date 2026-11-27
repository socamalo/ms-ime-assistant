using System.Runtime.InteropServices;

namespace MsImeDict.App;

/// <summary>
/// 低级键盘钩子，自己识别热键组合。
///
/// 作为 RegisterHotKey 的兜底：注册成功但收不到 WM_HOTKEY 时
/// （例如别的程序装了键盘钩子），这条通道仍然有效。
/// 只观察不拦截，永远 CallNextHookEx。
/// </summary>
public sealed class KeyboardHook : NativeWindow
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_SHIFT = 0x10;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

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

    private readonly HookProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private readonly HashSet<uint> _down = new();

    public uint TargetVk { get; set; }
    public bool NeedCtrl { get; set; } = true;
    public bool NeedAlt { get; set; } = true;
    public bool NeedShift { get; set; }
    public bool NeedWin { get; set; }

    public event Action? Triggered;

    public KeyboardHook()
    {
        _proc = Callback;
        var cp = new CreateParams { Caption = "MsImeDictKeyboardHook", Parent = new IntPtr(-3) };
        CreateHandle(cp);
    }

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
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

        // 松开时把按键从按下集合里移除。
        // 不处理 keyup 的话，按过一次 Q 之后 Q 会永远留在集合里，
        // 之后所有 Q 键的 keydown 都被当成「长按重复」忽略 —— 热键从此失效。
        if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
        {
            var up = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            _down.Remove(up.vkCode);
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var vk = data.vkCode;

        // 自行维护按下集合，避免长按重复触发
        if (!_down.Add(vk)) return CallNextHookEx(_hook, nCode, wParam, lParam);

        if (vk == TargetVk && TargetVk != 0 && ModifiersMatch())
        {
            Task.Run(() => Triggered?.Invoke());
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // 注意：_down 只在 keydown 时增加，没有 keyup 会一直累积。
    // 用一个轻量清理：每次触发或定时由外部调用 Reset()。
    public void Reset() => _down.Clear();

    private bool ModifiersMatch()
    {
        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        bool win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
        return ctrl == NeedCtrl && alt == NeedAlt && shift == NeedShift && win == NeedWin;
    }
}
