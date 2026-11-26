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
        // 1) 焦点元素与其祖先链（最快，覆盖记事本/Word/资源管理器）
        var fromFocus = ReadFrom(GetFocusedElement());
        if (!string.IsNullOrWhiteSpace(fromFocus))
        {
            Log.Write("capture: uia path=focused");
            return fromFocus;
        }

        // 2) 窗口根节点 -> Document/Edit（Chromium/Electron 的选区挂在 Document 上）
        //    先激活 Chromium 的无障碍树，否则页面内容在 UIA 里根本不存在
        var fg = GetForegroundWindow();
        var activated = ChromiumAccessibility.Activate(fg);
        if (activated)
        {
            Log.Write("capture: chromium accessibility activated");
            System.Threading.Thread.Sleep(120);
        }

        var root = GetForegroundRoot();
        var fromDocument = ReadFromDocument(root);
        if (!string.IsNullOrWhiteSpace(fromDocument))
        {
            Log.Write("capture: uia path=document");
            return fromDocument;
        }

        // 3) 有界全树扫描（最后手段，限时 600ms，避免动辄超时）
        var fromScan = ScanBounded(root, TimeSpan.FromMilliseconds(600));
        if (!string.IsNullOrWhiteSpace(fromScan))
        {
            Log.Write("capture: uia path=scan");
            return fromScan;
        }

        return null;
    }

    /// <summary>直接找 Document / Edit / Text 容器，比全树扫描快一个数量级。</summary>
    private static string? ReadFromDocument(AutomationElement? root)
    {
        if (root is null) return null;
        foreach (var type in new[]
                 {
                     ControlType.Document, ControlType.Edit, ControlType.Text, ControlType.Pane, ControlType.Group
                 })
        {
            try
            {
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
                var found = root.FindFirst(TreeScope.Descendants, cond);
                if (found is null) continue;
                var text = ReadSelection(found);
                if (text is not null) return text;
            }
            catch { }
        }
        return null;
    }

    private static string? ScanBounded(AutomationElement? root, TimeSpan budget)
    {
        if (root is null) return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var cond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
            var all = root.FindAll(TreeScope.Descendants, cond);
            var limit = Math.Min(all.Count, 300);
            for (var i = 0; i < limit; i++)
            {
                if (sw.Elapsed > budget) break;
                var text = ReadSelection(all[i]);
                if (text is not null) return text;
            }
        }
        catch { }
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
