using FeatherBrowser.Core;


namespace FeatherBrowser.Services;

/// <summary>
/// 同一进程内所有浏览器窗口共享的东西。
///
/// <para>为什么必须共享：
/// <list type="bullet">
///   <item><b>CEF 环境</b> —— 环境对应一套浏览器进程与磁盘缓存。
///         每个窗口建一个环境，缓存与 Cookie 会分裂、内核进程也会翻倍。
///         官方推荐整个进程只建一次。无痕窗口使用独立的内存 Cookie 与缓存。</item>
///   <item><b>数据存储</b> —— 设置、书签、历史、密码库在一个进程里必须只有一份，
///         否则两个窗口各自持有副本，互相覆盖对方写入的内容。</item>
///   <item><b>广告拦截规则</b> —— 几百条域名，没必要每个窗口加载一遍。</item>
/// </list>
/// </para>
///
/// <para>用 <see cref="Lazy{T}"/> 是为了让创建时机确定（第一次用到时），
/// 又不至于每个窗口重复创建。</para>
/// </summary>
public sealed class BrowserContext
{    private static readonly Lazy<BrowserContext> Instance =
        new(() => new BrowserContext(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static BrowserContext Shared => Instance.Value;

    private readonly SemaphoreSlim _environmentGate = new(1, 1);

    internal BrowserContext(AppSettings settings = null)
    {
        Settings = settings ?? AppSettings.Load();
        AdBlock = new AdBlocker();
        AdBlock.Enabled = Settings.AdBlockEnabled;
        History = new HistoryStore();
        Bookmarks = new BookmarkStore();
        Passwords = new PasswordStore();
        Downloads = new DownloadStore();
    }

    public AppSettings Settings { get; }

    public AdBlocker AdBlock { get; }

    public HistoryStore History { get; }

    public BookmarkStore Bookmarks { get; }

    public PasswordStore Passwords { get; }

    /// <summary>下载记录。多个窗口共用一份。</summary>
    public DownloadStore Downloads { get; }

    /// <summary>普通窗口共用的 CEF 环境；第一个窗口打开时创建。</summary>
    private BrowserProfile _sharedEnvironment;

    /// <summary>
    /// 取共用环境，没有就创建。并发调用会被串行化，不会建出两个环境。
    /// </summary>
    /// <param name="forceDarkPages">网页配色由每个视图动态同步。</param>
    internal async Task<BrowserProfile> GetEnvironmentAsync(bool forceDarkPages)
    {
        if (_sharedEnvironment != null)
        {
            return _sharedEnvironment;
        }

        await _environmentGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_sharedEnvironment == null)
            {
                _sharedEnvironment = await TabManager.CreateEnvironmentAsync(
                    Path.Combine(AppPaths.BrowserDataFolder, "Default"), forceDarkPages).ConfigureAwait(true);
                Log.Info("共用 CEF 环境已创建，所有普通窗口共用");
            }
            return _sharedEnvironment;
        }
        finally
        {
            _environmentGate.Release();
        }
    }

    /// <summary>环境是否已经建好（没建好的话，新窗口需要等待）。</summary>
    internal void DisposeEnvironment() { _sharedEnvironment?.Dispose(); _sharedEnvironment = null; }

    public bool HasEnvironment => _sharedEnvironment != null;
}
