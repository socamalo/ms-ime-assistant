using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace MsImeDict.App;

/// <summary>
/// 激活 Chromium / Electron 的无障碍树。
///
/// Chromium 默认不构建无障碍树，UI Automation 里看不到页面内容。
/// 社区（AutoHotkey UIA-v2 的 ElementFromChromium、多位自动化作者）验证过的做法是：
/// 给渲染子窗口 <c>Chrome_RenderWidgetHostHWND1</c> 发送 <c>WM_GETOBJECT</c>，
/// 然后让 UIA 连一次，树才会建起来。
///
/// 只对 Chromium 系窗口生效，其他窗口快速返回，不产生开销。
/// </summary>
public static class ChromiumAccessibility
{
    private const uint WM_GETOBJECT = 0x003D;
    private const string RenderHostClass = "Chrome_RenderWidgetHostHWND1";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>在给定窗口下找渲染宿主窗口并激活无障碍。返回是否找到并激活。</summary>
    public static bool Activate(IntPtr topLevel)
    {
        if (topLevel == IntPtr.Zero) return false;

        var host = FindRenderHost(topLevel, 0);
        if (host == IntPtr.Zero) return false;

        try
        {
            SendMessage(host, WM_GETOBJECT, IntPtr.Zero, (IntPtr)1);
        }
        catch { }

        // 再让 UIA 连一次该窗口，确保无障碍树建立并缓存
        try { _ = AutomationElement.FromHandle(host); } catch { }

        return true;
    }

    private static IntPtr FindRenderHost(IntPtr parent, int depth)
    {
        if (depth > 4) return IntPtr.Zero;

        var child = IntPtr.Zero;
        while (true)
        {
            child = FindWindowEx(parent, child, null, null);
            if (child == IntPtr.Zero) break;

            var cls = GetClassNameOf(child);
            if (string.Equals(cls, RenderHostClass, StringComparison.Ordinal)) return child;

            var nested = FindRenderHost(child, depth + 1);
            if (nested != IntPtr.Zero) return nested;
        }
        return IntPtr.Zero;
    }

    private static string GetClassNameOf(IntPtr hWnd)
    {
        var buf = new char[256];
        var n = GetClassName(hWnd, buf, buf.Length);
        return n <= 0 ? "" : new string(buf, 0, n);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] buffer, int maxCount);
}
