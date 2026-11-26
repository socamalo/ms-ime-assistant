using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace MsImeDict.App;

/// <summary>
/// 取词：UI Automation 优先，剪贴板兜底。
///
/// 必须在 STA 线程上调用（剪贴板要求），见 StaRunner。
/// 只在用户显式触发（拖选/热键/菜单）时才发 Ctrl+C，且会还原剪贴板。
/// </summary>
public static class SelectionReader
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const string Sentinel = "__MSIMDICT_CLIP_SENTINEL__";

    /// <summary>读当前选区。失败返回 null。</summary>
    public static string? Read()
    {
        var viaUia = ReadViaUia();
        if (!string.IsNullOrWhiteSpace(viaUia))
        {
            Log.Write($"capture: uia hit len={viaUia.Length}");
            return viaUia;
        }

        var viaClip = ReadViaClipboard();
        if (!string.IsNullOrWhiteSpace(viaClip))
        {
            Log.Write($"capture: clipboard hit len={viaClip.Length}");
            return viaClip;
        }

        Log.Write("capture: both paths empty");
        return null;
    }

    private static string? ReadViaUia()
    {
        try
        {
            var element = GetFocusedElement();
            if (element is null) return null;

            try
            {
                if (element.Current.IsPassword) return null;
            }
            catch { /* 属性不可用时忽略 */ }

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
        try { return TreeWalker.ControlViewWalker.GetParent(e); }
        catch { return null; }
    }

    /// <summary>剪贴板兜底：留哨兵 -> Ctrl+C -> 轮询等待变化 -> 读文本 -> 还原剪贴板。</summary>
    private static string? ReadViaClipboard()
    {
        IDataObject? saved = null;
        try { saved = Clipboard.GetDataObject(); } catch { }

        try { Clipboard.SetText(Sentinel); } catch { }

        try { SendCtrlC(); }
        catch { return null; }

        string? result = null;
        for (var i = 0; i < 15; i++)
        {
            System.Threading.Thread.Sleep(60);
            try
            {
                if (!Clipboard.ContainsText()) continue;
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text) && text != Sentinel)
                {
                    result = text;
                    break;
                }
            }
            catch { /* 剪贴板被占用时重试 */ }
        }

        if (saved is not null)
        {
            try { Clipboard.SetDataObject(saved, true); } catch { }
        }
        return result;
    }

    private static void SendCtrlC()
    {
        const uint KEYEVENTF_KEYUP = 0x0002;
        const ushort VK_CONTROL = 0x11;
        const ushort VK_C = 0x43;

        var inputs = new[]
        {
            NewInput(0, VK_CONTROL),
            NewInput(0, VK_C),
            NewInput(KEYEVENTF_KEYUP, VK_C),
            NewInput(KEYEVENTF_KEYUP, VK_CONTROL)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT NewInput(uint flags, ushort vk) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = flags } }
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
