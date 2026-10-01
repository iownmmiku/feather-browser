using FeatherBrowser.UI;

namespace FeatherBrowser.Services;

/// <summary>消息循环跟随所有浏览器窗口的生命周期。</summary>
internal sealed class BrowserApplicationContext : ApplicationContext
{
    private readonly HashSet<MainForm> _windows = new();
    private SynchronizationContext _ui;

    public void OpenWindow(MainForm window)
    {
        _windows.Add(window);
        window.FormClosed += OnWindowClosed;
        // 浏览器窗口彼此独立，不能设为 owned form，否则关闭父窗口会连带关闭。
        window.Show();
        _ui ??= SynchronizationContext.Current;
    }

    internal void RequestWindow(string url) => _ui?.Post(_ =>
    {
        if (_windows.Count > 0) OpenWindow(new MainForm(url, incognito: false));
    }, null);

    private async void OnWindowClosed(object sender, FormClosedEventArgs e)
    {
        var window = (MainForm)sender;
        window.FormClosed -= OnWindowClosed;
        await window.CleanupTask;
        _windows.Remove(window);
        if (_windows.Count == 0)
        {
            BrowserContext.Shared.Bookmarks.Flush();
            BrowserContext.Shared.History.SaveNow();
            ExitThread();
        }
    }
}
