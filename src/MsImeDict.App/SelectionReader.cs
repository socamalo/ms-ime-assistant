using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace MsImeDict.App;

/// <summary>
/// 取词：UI Automation 优先，剪贴板兜底。
///
/// 只在用户显式触发（热键 / 菜单）时才发 Ctrl+C，且会尽量还原剪贴板。
/// </summary>
public static class SelectionReader
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>读当前选区。失败返回 null。</summary>
    public static string? Read()
    {
        var viaUia = ReadViaUia();
        if (!string.IsNullOrWhiteSpace(viaUia)) return viaUia;
        return ReadViaClipboard();
    }

    private static string? ReadViaUia()
    {
        try
        {
            var element = GetFocusedElement();
            if (element is null) return null;

            // 密码框直接放弃
            try
            {
                if (element.Current.IsPassword) return null;
            }
            catch { /* 属性不可用就忽略 */ }

            for (var e = element; e is not null; e = WalkUp(e))
            {
                try
                {
                    if (e.GetCurrentPattern(TextPattern.Pattern) is TextPattern tp)
                    {
                        var ranges = tp.GetSelection();
                        if (ranges.Length == 0) return null;
                        var sb = new StringBuilder();
                        foreach (var r in ranges) sb.Append(r.GetText(-1));
                        var text = sb.ToString();
                        return string.IsNullOrWhiteSpace(text) ? null : text;
                    }
                }
                catch (Exception) { /* 换父节点继续找 */ }
            }
        }
        catch (Exception) { }
        return null;
    }

    private static AutomationElement? GetFocusedElement()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is not null) return focused;
        }
        catch { }

        // 回退：前台窗口的焦点元素
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;
            return AutomationElement.FromHandle(fg);
        }
        catch { return null; }
    }

    private static AutomationElement? WalkUp(AutomationElement e)
    {
        try
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(e);
            return parent;
        }
        catch { return null; }
    }

    /// <summary>剪贴板兜底：保存 -> Ctrl+C -> 读 -> 还原。</summary>
    private static string? ReadViaClipboard()
    {
        IDataObject? saved = null;
        try { saved = Clipboard.GetDataObject(); } catch { }

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;

        string? result = null;
        try
        {
            SendCtrlC(fg);
            System.Threading.Thread.Sleep(180);
            var text = Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(text)) result = text;
        }
        catch (Exception) { }
        finally
        {
            if (saved is not null)
            {
                try { Clipboard.SetDataObject(saved, true); } catch { }
            }
        }
        return result;
    }

    private static void SendCtrlC(IntPtr foreground)
    {
        const uint KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_CONTROL = 0x11;
        const ushort VK_C = 0x43;

        var inputs = new[]
        {
            NewInput(KEYEVENTF_KEYUP, 0, VK_CONTROL),
            NewInput(0, 0, VK_CONTROL),
            NewInput(0, 0, VK_C),
            NewInput(KEYEVENTF_KEYUP, 0, VK_C),
            NewInput(KEYEVENTF_KEYUP, 0, VK_CONTROL)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT NewInput(uint flags, uint scan, ushort vk) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)scan, dwFlags = flags } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
