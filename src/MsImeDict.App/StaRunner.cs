using System.Threading;

namespace MsImeDict.App;

/// <summary>
/// 在专用 STA 线程上执行取词。
///
/// 原因：剪贴板回退路径用到 System.Windows.Forms.Clipboard，它要求 STA；
/// 而鼠标钩子回调跑在线程池（MTA）线程上，直接调用会失败并静默返回空，
/// 表现为「Electron 应用里选词没有浮条」。
/// UI Automation 也在 STA 上更稳。
/// </summary>
public static class StaRunner
{
    public static T? Run<T>(Func<T?> func, int timeoutMs = 4000)
    {
        T? result = default;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        })
        {
            IsBackground = true,
            Name = "msimdict-capture"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeoutMs))
            Log.Write("capture: STA thread timeout");

        if (error is not null) Log.Write("capture: STA exception " + error.Message);
        return result;
    }
}
