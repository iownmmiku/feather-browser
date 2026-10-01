using FeatherBrowser.Core;
using Microsoft.Web.WebView2.Core;

namespace FeatherBrowser.Services;

/// <summary>
/// 同一进程内所有浏览器窗口共享的东西。
///
/// <para>为什么必须共享：
/// <list type="bullet">
///   <item><b>WebView2 环境</b> —— 环境对应一套浏览器进程与磁盘缓存。
///         每个窗口建一个环境，缓存与 Cookie 会分裂、内核进程也会翻倍。
///         官方推荐整个进程只建一次。无痕窗口是唯一例外，它有自己的临时目录。</item>
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

    private BrowserContext()
    {
        Settings = AppSettings.Load();
        AdBlock = new AdBlocker();
        AdBlock.Enabled = Settings.AdBlockEnabled;
        History = new HistoryStore();
        Bookmarks = new BookmarkStore();
        Passwords = new PasswordStore();
    }

    public AppSettings Settings { get; }

    public AdBlocker AdBlock { get; }

    public HistoryStore History { get; }

    public BookmarkStore Bookmarks { get; }

    public PasswordStore Passwords { get; }

    /// <summary>普通窗口共用的 WebView2 环境；第一个窗口打开时创建。</summary>
    private CoreWebView2Environment _sharedEnvironment;

    /// <summary>
    /// 取共用环境，没有就创建。并发调用会被串行化，不会建出两个环境。
    /// </summary>
    /// <param name="forceDarkPages">是否让内核把网页按深色渲染（内核启动参数，创建后不可改）。</param>
    public async Task<CoreWebView2Environment> GetEnvironmentAsync(bool forceDarkPages)
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
                    AppPaths.WebViewDataFolder, forceDarkPages).ConfigureAwait(true);
                Log.Info("共用 WebView2 环境已创建，所有普通窗口共用");
            }
            return _sharedEnvironment;
        }
        finally
        {
            _environmentGate.Release();
        }
    }

    /// <summary>环境是否已经建好（没建好的话，新窗口需要等待）。</summary>
    public bool HasEnvironment => _sharedEnvironment != null;
}
