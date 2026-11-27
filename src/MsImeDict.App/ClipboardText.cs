using System.Runtime.InteropServices;
using System.Text;

namespace MsImeDict.App;

/// <summary>
/// 读剪贴板里的纯文本（只读，永不写入）。
///
/// 不用 System.Windows.Forms.Clipboard：它走 OLE，别的程序占着剪贴板时会阻塞，
/// 实测能把取词线程挂死 2 秒以上（日志里的 STA thread timeout 就是它）。
/// Win32 的 OpenClipboard 拿不到锁会立刻返回 FALSE，可以带节奏重试，
/// 全程有上限，绝不会卡住调用方。
/// </summary>
public static class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;
    private const int MaxAttempts = 12;
    private const int RetryDelayMs = 40;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    /// <summary>
    /// 读文本。返回 null 表示没读到，reason 说明原因（用于给用户提示）。
    /// </summary>
    public static string? Read(out string reason)
    {
        reason = "";

        var opened = false;
        for (var i = 0; i < MaxAttempts; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) { opened = true; break; }
            System.Threading.Thread.Sleep(RetryDelayMs);
        }

        if (!opened)
        {
            reason = "剪贴板被其他程序占用，稍后再试";
            return null;
        }

        try
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                reason = "剪贴板里不是文字（可能是图片或文件）";
                return null;
            }

            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                reason = "读剪贴板数据失败";
                return null;
            }

            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero)
            {
                reason = "锁定剪贴板内存失败";
                return null;
            }

            try
            {
                var size = (long)GlobalSize(handle);
                if (size <= 0 || size > 8 * 1024 * 1024)
                {
                    reason = "剪贴板内容长度异常";
                    return null;
                }

                var text = Marshal.PtrToStringUni(ptr, (int)(size / 2))?.TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(text))
                {
                    reason = "剪贴板里的文字是空的";
                    return null;
                }

                return text;
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
}
