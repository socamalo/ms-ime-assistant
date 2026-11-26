using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace MsImeDict.App;

/// <summary>
/// 取词：只用 UI Automation 读取选区。
///
/// 本程序**完全不碰剪贴板**：不发 Ctrl+C、不写剪贴板、不做快照还原。
/// 剪贴板属于用户，任何"先占位再还原"的做法都可能把内容弄丢
/// （实测事故：Win+Shift+S 的截图被冲掉）。
///
/// 读不到就是读不到，交给上层提示，不做猜测性补救。
/// 必须在 STA 线程上调用（见 StaRunner）。
/// </summary>
public static class SelectionReader
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>读当前选区。UIA 读不到返回 null，不做任何兜底动作。</summary>
    public static string? Read()
    {
        var text = ReadViaUia();
        if (string.IsNullOrWhiteSpace(text))
        {
            Log.Write("capture: uia miss (clipboard is never touched)");
            return null;
        }
        Log.Write($"capture: uia hit len={text.Length}");
        return text;
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
                    if (e.GetCurrentPattern(TextPattern.Pattern) is not TextPattern tp) continue;

                    var ranges = tp.GetSelection();
                    if (ranges.Length == 0) return null;

                    var sb = new StringBuilder();
                    foreach (var r in ranges) sb.Append(r.GetText(-1));
                    var text = sb.ToString();
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
                catch (Exception)
                {
                    // 换父节点继续找
                }
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
}
