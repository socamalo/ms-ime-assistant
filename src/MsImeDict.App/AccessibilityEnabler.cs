using System.Runtime.InteropServices;

namespace MsImeDict.App;

/// <summary>
/// 告诉系统「有辅助技术（读屏软件）在运行」。
///
/// 这是微软给辅助技术预留的正式开关：<c>SPI_SETSCREENREADER</c>。
/// Chromium / Electron 会检测这个标志，一旦为真就构建无障碍树，
/// UI Automation 才能看到页面里的文本与选区。
/// 默认是关的——这就是 Electron 应用里读不到选区的根因。
///
/// 退出时还原为原值，不留系统级副作用。
/// </summary>
public static class AccessibilityEnabler
{
    private const uint SPI_GETSCREENREADER = 0x0046;
    private const uint SPI_SETSCREENREADER = 0x0047;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    private static bool _hadToSet;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    public static bool IsOn()
    {
        var buf = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buf, 0);
            if (!SystemParametersInfo(SPI_GETSCREENREADER, 0, buf, 0)) return false;
            return Marshal.ReadInt32(buf) != 0;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>开启。若原本已开则不动，退出时也不还原。</summary>
    public static void Enable()
    {
        try
        {
            if (IsOn())
            {
                _hadToSet = false;
                Log.Write("screen reader flag already on");
                return;
            }

            var ok = SystemParametersInfo(SPI_SETSCREENREADER, 1, IntPtr.Zero,
                                          SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            _hadToSet = ok;
            Log.Write($"screen reader flag set={ok}");
        }
        catch (Exception ex)
        {
            Log.Write("screen reader enable failed: " + ex.Message);
        }
    }

    /// <summary>只还原我们自己打开的那个开关。</summary>
    public static void Restore()
    {
        if (!_hadToSet) return;
        try
        {
            SystemParametersInfo(SPI_SETSCREENREADER, 0, IntPtr.Zero,
                                 SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            _hadToSet = false;
            Log.Write("screen reader flag restored");
        }
        catch { }
    }
}
