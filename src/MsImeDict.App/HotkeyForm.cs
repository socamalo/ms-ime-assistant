using System.Runtime.InteropServices;

namespace MsImeDict.App;

/// <summary>
/// 承载全局热键的隐藏窗体。
///
/// 用真实的 WinForms Form 而不是 NativeWindow / 消息专用窗口：
/// 后者在实测中注册成功但收不到 WM_HOTKEY。
/// 窗体放在屏幕外、1×1、不进任务栏，用户看不到。
/// </summary>
public sealed class HotkeyForm : Form
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly Dictionary<int, string> _byId = new();
    private int _nextId = 0xB001;

    /// <summary>触发时传回组合字符串，便于日志区分。</summary>
    public event Action<string>? Triggered;

    public HotkeyForm()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        Opacity = 0;
        ShowIcon = false;
        Text = "MsImeDictHotkey";
    }

    /// <summary>确保句柄已创建（不显示窗口也能收消息）。</summary>
    public void EnsureHandle()
    {
        _ = Handle;
    }

    /// <summary>依次尝试注册，返回成功与失败的组合。</summary>
    public (List<string> Ok, List<string> Failed) RegisterAll(IEnumerable<string> combos)
    {
        UnregisterAll();
        var ok = new List<string>();
        var failed = new List<string>();
        foreach (var combo in combos)
        {
            if (!TryParse(combo, out var mods, out var vk)) { failed.Add(combo + "(解析失败)"); continue; }
            var id = _nextId++;
            if (RegisterHotKey(Handle, id, mods | MOD_NOREPEAT, vk))
            {
                _byId[id] = combo;
                ok.Add(combo);
            }
            else
            {
                failed.Add($"{combo}(err={Marshal.GetLastWin32Error()})");
            }
        }
        return (ok, failed);
    }

    /// <summary>注销全部已注册组合（改热键时先清空）。</summary>
    public void UnregisterAll()
    {
        foreach (var id in _byId.Keys) UnregisterHotKey(Handle, id);
        _byId.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            var id = m.WParam.ToInt32();
            if (_byId.TryGetValue(id, out var combo))
            {
                Log.Write($"hotkey fired: {combo}");
                Triggered?.Invoke(combo);
            }
            else
            {
                Log.Write($"hotkey fired: unknown id={id}");
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        foreach (var id in _byId.Keys) UnregisterHotKey(Handle, id);
        _byId.Clear();
        base.OnFormClosing(e);
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
        if (key.Length == 1 && char.IsLetter(key[0])) vk = (uint)char.ToUpperInvariant(key[0]);
        else if (key.Length == 1 && char.IsDigit(key[0])) vk = (uint)key[0];
        else if (key.StartsWith('F') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 12)
            vk = (uint)(0x70 + f - 1);
        else return false;

        return vk != 0;
    }
}
