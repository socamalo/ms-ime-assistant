using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace MsImeDict.App;

/// <summary>
/// 取词。两条互不干扰的路径，**都不写剪贴板、不发按键**：
///
///   1 UI Automation 读当前选区 —— 给浮条用（用户只是选中，没有复制）
///   2 只读系统剪贴板里的文字   —— 给热键用（用户先 Ctrl+C 复制）
///
/// 绝不做的事：SetText / SetDataObject / SendInput / WM_GETOBJECT / 改系统设置。
/// 因此不会破坏剪贴板内容，也不会干预其他程序。
/// </summary>
public static class SelectionReader
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>上一次取词的来源：uia / clipboard / none。</summary>
    public static string LastSource { get; private set; } = "none";

    /// <summary>
    /// 读内容。
    /// preferClipboard = true（热键）：先读剪贴板，没字再看选区。
    /// preferClipboard = false（浮条）：先看选区，没有再看剪贴板。
    /// </summary>
    public static string? Read(bool preferClipboard = false)
        => preferClipboard ? ClipboardFirst() : SelectionFirst();

    private static string? ClipboardFirst()
    {
        var fromClipboard = ReadClipboardText();
        if (!string.IsNullOrWhiteSpace(fromClipboard))
        {
            LastSource = "clipboard";
            return fromClipboard;
        }

        var fromSelection = ReadSelection();
        if (!string.IsNullOrWhiteSpace(fromSelection))
        {
            LastSource = "uia";
            return fromSelection;
        }

        LastSource = "none";
        return null;
    }

    private static string? SelectionFirst()
    {
        var fromSelection = ReadSelection();
        if (!string.IsNullOrWhiteSpace(fromSelection))
        {
            LastSource = "uia";
            return fromSelection;
        }

        var fromClipboard = ReadClipboardText();
        if (!string.IsNullOrWhiteSpace(fromClipboard))
        {
            LastSource = "clipboard";
            return fromClipboard;
        }

        LastSource = "none";
        return null;
    }

    // ---------- 只读剪贴板 ----------

    /// <summary>上一次读剪贴板失败的原因，用于给用户看得懂的提示。</summary>
    public static string LastClipboardReason { get; private set; } = "";

    private static string? ReadClipboardText()
    {
        var text = ClipboardText.Read(out var reason);
        LastClipboardReason = reason;
        return text;
    }

    // ---------- UI Automation 读选区 ----------

    private static string? ReadSelection()
    {
        var fromFocus = ReadFromElementChain(GetFocusedElement());
        if (!string.IsNullOrWhiteSpace(fromFocus)) return fromFocus;

        return ReadFromDocument(GetForegroundRoot());
    }

    private static string? ReadFromElementChain(AutomationElement? element)
    {
        for (var e = element; e is not null; e = WalkUp(e))
        {
            var text = ReadSelectionOf(e);
            if (text is not null) return text;
        }
        return null;
    }

    /// <summary>按控件类型直接找 Document/Edit，比全树扫描快一个数量级。</summary>
    private static string? ReadFromDocument(AutomationElement? root)
    {
        if (root is null) return null;
        foreach (var type in new[] { ControlType.Document, ControlType.Edit })
        {
            try
            {
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
                var found = root.FindFirst(TreeScope.Descendants, cond);
                if (found is null) continue;
                var text = ReadSelectionOf(found);
                if (text is not null) return text;
            }
            catch { }
        }
        return null;
    }

    private static string? ReadSelectionOf(AutomationElement e)
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

        return GetForegroundRoot();
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

    private static AutomationElement? WalkUp(AutomationElement e)
    {
        try { return TreeWalker.ControlViewWalker.GetParent(e); }
        catch { return null; }
    }
}
