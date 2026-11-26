using System.Runtime.InteropServices;

namespace MsImeDict.App;

/// <summary>
/// 全局热键注册。用消息专用窗口（HWND_MESSAGE）接收 WM_HOTKEY，
/// 不占屏也不出现在任务栏。
/// </summary>
public sealed class HotkeyHost : NativeWindow
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly int _id = 0xBEEF;
    private uint _modifiers;
    private uint _vk;

    public event Action? Triggered;

    public HotkeyHost()
    {
        var cp = new CreateParams
        {
            Caption = "MsImeDictHotkey",
            Parent = HWND_MESSAGE,
            Style = 0,
            ExStyle = 0
        };
        CreateHandle(cp);
    }

    /// <summary>解析并注册，形如 Ctrl+Alt+D / Ctrl+Shift+F5。</summary>
    public bool Register(string hotkey)
    {
        Unregister();
        if (!TryParse(hotkey, out _modifiers, out _vk)) return false;
        return RegisterHotKey(Handle, _id, _modifiers | MOD_NOREPEAT, _vk);
    }

    public void Unregister() => UnregisterHotKey(Handle, _id);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == _id) Triggered?.Invoke();
        base.WndProc(ref m);
    }

    /// <summary>释放：注销热键并销毁消息窗口。</summary>
    public void Shutdown()
    {
        Unregister();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static bool TryParse(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(hotkey)) return false;

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        foreach (var p in parts.Take(parts.Length - 1))
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win" or "windows": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }

        var key = parts[^1];
        if (key.Length == 1 && char.IsLetter(key[0]))
            vk = (uint)char.ToUpperInvariant(key[0]);
        else if (key.Length == 1 && char.IsDigit(key[0]))
            vk = (uint)key[0];
        else if (key.StartsWith('F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 12)
            vk = (uint)(0x70 + f - 1);
        else
            return false;

        return vk != 0;
    }
}
