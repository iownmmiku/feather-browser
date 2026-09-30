using System.Runtime.InteropServices;

namespace FeatherBrowser.UI;

/// <summary>
/// 让 Windows 把非客户区（标题栏、边框）也画成深色。
///
/// <p>Windows 10 1809 起支持 <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>，但默认只跟随应用自己声明；
/// 我们自己控制主题，就必须显式告诉 DWM，否则深色界面顶上会顶一条白色标题栏。
/// 属性编号在 20 与 19 之间变动过（不同 Windows 版本），这里两个都试一次。
/// </summary>
internal static class WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int value, int size);

    /// <summary>按当前主题设置窗口边框颜色。控件句柄创建后调用即可。</summary>
    public static void ApplyDarkTitleBar(Form form, bool dark)
    {
        if (form == null || !form.IsHandleCreated)
        {
            return;
        }
        try
        {
            int value = dark ? 1 : 0;
            int result = DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode,
                ref value, sizeof(int));
            if (result != 0)
            {
                // 旧版 Windows 用的是 19 号属性
                DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkModeLegacy,
                    ref value, sizeof(int));
            }
        }
        catch
        {
            // 系统不支持时忽略，只是标题栏颜色不跟随，不影响功能
        }
    }
}
