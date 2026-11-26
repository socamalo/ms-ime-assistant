using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace MsImeDict.App;

/// <summary>
/// 激活 Chromium / Electron 的无障碍树。
///
/// Chromium 默认不构建无障碍树，UI Automation 里看不到页面内容。
/// 社区（AutoHotkey UIA-v2 的 ElementFromChromium）的做法是给渲染子窗口
/// <c>Chrome_RenderWidgetHostHWND1</c> 发送 <c>WM_GETOBJECT</c>。
///
/// 两个必须注意的实现细节：
/// 1. 用 <c>SendMessageTimeout</c> 而不是 <c>SendMessage</c>：
///    Chromium 建树时不会立即返回，同步发送会把调用线程挂死（实测 STA 线程超时）。
/// 2. 激活一次就够了，按窗口句柄缓存 60 秒，避免每次取词都去戳它。
/// </summary>
public static class ChromiumAccessibility
{
    private const uint WM_GETOBJECT = 0x003D;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SMTO_BLOCK = 0x0001;
    private const int TimeoutMs = 250;
    private const string RenderHostClass = "Chrome_RenderWidgetHostHWND1";

    private static IntPtr _lastHost = IntPtr.Zero;
    private static DateTime _lastActivated = DateTime.MinValue;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] buffer, int maxCount);

    /// <summary>在给定窗口下找渲染宿主并激活。返回是否找到宿主窗口。</summary>
    public static bool Activate(IntPtr topLevel)
    {
        if (topLevel == IntPtr.Zero) return false;

        var host = FindRenderHost(topLevel, 0);
        if (host == IntPtr.Zero) return false;

        // 同一窗口 60 秒内只激活一次
        if (host == _lastHost && (DateTime.UtcNow - _lastActivated).TotalSeconds < 60) return true;

        try
        {
            SendMessageTimeout(host, WM_GETOBJECT, IntPtr.Zero, (IntPtr)1,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, TimeoutMs, out _);
            _lastHost = host;
            _lastActivated = DateTime.UtcNow;
            Log.Write($"chromium: WM_GETOBJECT sent to 0x{host.ToInt64():X}");
        }
        catch (Exception ex)
        {
            Log.Write("chromium: WM_GETOBJECT failed " + ex.Message);
        }

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

            if (string.Equals(GetClassNameOf(child), RenderHostClass, StringComparison.Ordinal))
                return child;

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
}
