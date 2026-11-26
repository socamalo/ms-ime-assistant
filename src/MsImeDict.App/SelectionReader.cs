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
        // 1) 从焦点元素往上找 TextPattern
        var fromFocus = ReadFrom(GetFocusedElement());
        if (!string.IsNullOrWhiteSpace(fromFocus))
        {
            Log.Write("capture: uia path=focused");
            return fromFocus;
        }

        // 2) 从窗口根节点全树找 TextPattern
        //    Electron / Chromium 的选区挂在 Document 元素上，不在焦点元素本身，
        //    只往上走找不到，必须从窗口根往下扫。
        var fromWindow = ReadFrom(GetForegroundRoot());
        if (!string.IsNullOrWhiteSpace(fromWindow))
        {
            Log.Write("capture: uia path=window-scan");
            return fromWindow;
        }

        return null;
    }

    private static AutomationElement? GetForegroundRoot()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;
            return AutomationElement.FromHandle(fg);
        }
        catch { return null; }
    }

    /// <summary>先在传入元素上找，再往上找父级，最后在整棵子树里找第一个有选区的 TextPattern。</summary>
    private static string? ReadFrom(AutomationElement? element)
    {
        if (element is null) return null;

        for (var e = element; e is not null; e = WalkUp(e))
        {
            var text = ReadSelection(e);
            if (text is not null) return text;
        }

        // 子树扫描：限制数量，避免在大页面上卡住
        try
        {
            var all = element.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            var limit = Math.Min(all.Count, 200);
            for (var i = 0; i < limit; i++)
            {
                var text = ReadSelection(all[i]);
                if (text is not null) return text;
            }
        }
        catch { }

        return null;
    }

    private static string? ReadSelection(AutomationElement e)
    {
        try
        {
            if (e.Current.IsPassword) return null;
        }
        catch { }

        try
        {
            if (e.GetCurrentPattern(TextPattern.Pattern) is not TextPattern tp) return null;
            var ranges = tp.GetSelection();
            if (ranges.Length == 0) return null;

            var sb = new StringBuilder();
            foreach (var r in ranges) sb.Append(r.GetText(-1));
            var text = sb.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
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
