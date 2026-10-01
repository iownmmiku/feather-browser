using System.Runtime.InteropServices;

namespace FeatherBrowser.Services;

/// <summary>
/// 登记稳定的 Windows 应用身份，供任务栏分组、固定图标和显示名称使用。
/// AppUserModelID 不随补丁版本变化，避免升级后原来固定的图标与窗口分成两组。
/// 任务管理器的具体分组样式由 Windows 决定；子进程名称由 EXE 提供。
/// </summary>
internal static class AppIdentity
{
    public const string DisplayName = "轻羽浏览器";

    public const string AppUserModelId = "FeatherBrowser.Browser";

    /// <summary>
    /// 登记 AUMID 并设为当前进程的身份。失败不影响功能，只是任务栏名称不好看。
    /// </summary>
    public static void Initialize(string iconPath)
    {
        try
        {
            RegisterInRegistry(iconPath);
        }
        catch (Exception ex)
        {
            Log.Warn("登记 AppUserModelID 失败（不影响使用）: " + ex.Message);
        }

        try
        {
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppUserModelId));
            Log.Info($"AppUserModelID = {AppUserModelId}");
        }
        catch (Exception ex)
        {
            Log.Warn("设置进程 AppUserModelID 失败（不影响使用）: " + ex.Message);
        }
    }

    /// <summary>
    /// 在 HKCU 下登记显示名与图标。只写当前用户，不需要管理员权限。
    /// </summary>
    private static void RegisterInRegistry(string iconPath)
    {
        string key = @"Software\Classes\AppUserModelId\" + AppUserModelId;
        using var regKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key);
        if (regKey == null)
        {
            return;
        }

        regKey.SetValue("DisplayName", DisplayName, Microsoft.Win32.RegistryValueKind.String);

        // 图标用 exe 本身即可（图标已嵌进 exe），这样不依赖外部文件位置
        string icon = string.IsNullOrEmpty(iconPath)
            ? $"{Environment.ProcessPath},0"
            : $"{iconPath},0";
        regKey.SetValue("IconUri", icon, Microsoft.Win32.RegistryValueKind.String);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);
}
