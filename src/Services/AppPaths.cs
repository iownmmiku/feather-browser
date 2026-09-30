namespace FeatherBrowser.Services;

/// <summary>
/// 应用的数据目录。
///
/// <p>所有可写数据都放在 %LOCALAPPDATA%\FeatherBrowser 下，避免写到 Program Files
/// 引发权限问题；卸载时只需删掉这一个目录。
/// </summary>
public static class AppPaths
{
    private static string _rootOverride;

    /// <summary>
    /// 覆盖数据根目录。必须在第一次访问 <see cref="Root"/> 之前调用。
    /// 用途：让命令行导入 / 自检可以写到一个隔离目录，不动用户的真实数据。
    /// </summary>
    public static void OverrideRoot(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _rootOverride = Path.GetFullPath(path);
        }
    }

    public static string Root => _rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FeatherBrowser");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string BookmarksFile => Path.Combine(Root, "bookmarks.json");

    public static string HistoryFile => Path.Combine(Root, "history.json");

    /// <summary>密码库。里面每条密码都是 DPAPI 保护的密文，不含明文。</summary>
    public static string PasswordsFile => Path.Combine(Root, "passwords.json");

    public static string LogFile => Path.Combine(Root, "feather.log");

    /// <summary>WebView2 的用户数据目录，缓存与 Cookie 都在这里。</summary>
    public static string WebViewDataFolder => Path.Combine(Root, "WebView2");

    /// <summary>用户自定义拦截规则，一行一个域名。</summary>
    public static string UserBlockListFile => Path.Combine(Root, "user_blocklist.txt");

    public static void EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(Root);
        }
        catch
        {
            // 极端情况下（磁盘只读）忽略，后续写入会各自失败并记录。
        }
    }
}

/// <summary>
/// 极简日志。只写文件，不弹窗；文件超过 256KB 自动截断，避免无限增长。
/// </summary>
public static class Log
{
    private const long MaxSize = 256 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception ex = null) =>
        Write("ERROR", ex == null ? message : message + " -> " + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.EnsureCreated();
                string path = AppPaths.LogFile;
                if (File.Exists(path) && new FileInfo(path).Length > MaxSize)
                {
                    File.Delete(path);
                }
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}
