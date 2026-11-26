using System.Windows.Forms;

namespace MsImeDict.App;

/// <summary>
/// 取词：只读系统剪贴板。
///
/// 本程序**不查询任何应用**：不用 UI Automation、不发 WM_GETOBJECT、
/// 不发 Ctrl+C、不写剪贴板、不改系统设置。只做一件事：
/// 把用户刚复制的文字读出来。
///
/// 用法：选中文字 → Ctrl+C → 按热键。
/// 这样对所有应用一视同仁，Electron / 自绘界面 / 终端都能用。
/// </summary>
public static class SelectionReader
{
    /// <summary>上一次取词的来源，仅用于提示。</summary>
    public static string LastSource { get; private set; } = "none";

    /// <summary>读剪贴板里的文字。没有就返回 null。</summary>
    public static string? Read()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    LastSource = "none";
                    return null;
                }

                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text))
                {
                    LastSource = "none";
                    return null;
                }

                LastSource = "clipboard";
                return text;
            }
            catch
            {
                // 剪贴板正被别的进程占用，稍等重试
                System.Threading.Thread.Sleep(60);
            }
        }

        LastSource = "none";
        return null;
    }
}
