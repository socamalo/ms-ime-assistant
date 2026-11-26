using System.Threading;

namespace MsImeDict.App;

/// <summary>
/// 在专用 STA 线程上执行取词。
///
/// 原因：UI Automation 的 COM 调用在 STA 线程上更稳；
/// 鼠标钩子回调跑在线程池（MTA）线程上，直接在回调里调用容易失败。
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
