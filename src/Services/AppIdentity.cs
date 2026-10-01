using System.Runtime.InteropServices;

namespace FeatherBrowser.Services;

/// <summary>
/// 让 Windows 把这个程序当成一个「正式应用」而不是一个匿名进程。
///
/// <para>做法有三步，缺一不可：
/// <list type="number">
///   <item>exe 的版本资源里要有 Product / Company（在 csproj 里设置）；</item>
///   <item>注册表 <c>HKCU\Software\Classes\AppUserModelId\&lt;AUMID&gt;</c> 下登记
///         <c>DisplayName</c> 与 <c>IconUri</c>，AUMID 的格式必须是
///         <c>Company.Product.SubProduct.Version</c>，且 Company/Product 与版本资源一致；</item>
///   <item>启动时调用 <c>SetCurrentProcessExplicitAppUserModelID</c>，
///         并把同一个 AUMID 作为内核参数传给 WebView2 子进程。</item>
/// </list>
/// </para>
///
/// <para>为什么要管这个：程序的网页内容跑在 <c>msedgewebview2.exe</c> 子进程里，
/// 任务管理器默认按可执行文件名显示，用户看到的会是一堆「WebView2」。
/// 登记 AUMID 之后，这些子进程会归到本程序名下，任务栏也不再和 Edge 混在一起。</para>
/// </summary>
internal static class AppIdentity
{
    /// <summary>与 csproj 里的 Company / Product 必须一致，否则登记无效。</summary>
    private const string Company = "FeatherBrowser";

    private const string Product = "FeatherBrowser";

    private const string SubProduct = "Browser";

    /// <summary>
    /// 版本段。直接从程序集元数据读，而不是写死 ——
    /// 写死的话每次升版本都会忘记改，AUMID 就和 exe 的版本资源对不上，
    /// 任务栏分组随之失效，而且这种问题很难往这里想。
    /// </summary>
    private static string VersionTag
    {
        get
        {
            try
            {
                Version version = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetName().Version;
                if (version != null && version.Major > 0)
                {
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }
            }
            catch
            {
                // 取不到就用个稳定值，功能不受影响
            }
            return "1";
        }
    }

    /// <summary>Windows 要求的 AUMID 格式：Company.Product.SubProduct.Version。</summary>
    public static string AppUserModelId =>
        $"{Company}.{Product}.{SubProduct}.{VersionTag}";

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
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
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

        regKey.SetValue("DisplayName", Product, Microsoft.Win32.RegistryValueKind.String);

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
