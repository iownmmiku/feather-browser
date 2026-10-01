using CefSharp;
using CefSharp.Handler;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

internal sealed class BrowserRequestHandler(BrowserView view, TabManager manager, BrowserTab tab) : RequestHandler
{
    protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, bool userGesture, bool isRedirect)
    {
        if (!frame.IsMain) return false;
        string url = request.Url;
        if (url.StartsWith("feather://search?q=", StringComparison.OrdinalIgnoreCase))
        {
            string query = Uri.UnescapeDataString(url["feather://search?q=".Length..]);
            view.Ui(() => { if (tab.OwnsView(view)) tab.NavigateTo(UrlUtils.Normalize(query, manager.Settings.Engine.Template)); });
            return true;
        }
        view.Ui(() => tab.OnNavigationStarting(view, url));
        return false;
    }

    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser,
        IBrowser browser, IFrame frame, IRequest request, bool isNavigation, bool isDownload,
        string requestInitiator, ref bool disableDefaultHandling) => new BrowserResourceHandler(view, manager);

    protected override void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser,
        CefTerminationStatus status, int errorCode, string errorMessage) =>
        view.Ui(() => { if (tab.OwnsView(view)) manager.NotifyProcessFailed(tab, "RenderProcessExited", status + ": " + errorMessage); });
}

internal sealed class BrowserResourceHandler(BrowserView view, TabManager manager) : ResourceRequestHandler
{
    protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request, IRequestCallback callback)
    {
        bool main = request.ResourceType == ResourceType.MainFrame;
        if ((!manager.Settings.LoadImages && request.ResourceType == ResourceType.Image) ||
            manager.AdBlock.ShouldBlock(request.Url, main)) return CefReturnValue.Cancel;
        return CefReturnValue.Continue;
    }

    protected override IResourceHandler GetResourceHandler(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != InternalPages.Host || !uri.IsDefaultPort) return null;
        if (uri.AbsolutePath == "/home.html")
            return ResourceHandler.FromString(view.HomeHtml ?? UrlUtils.BuildHomeHtml(manager.Settings.Engine.Name, manager.PageTheme.ForceDark), mimeType: "text/html", encoding: System.Text.Encoding.UTF8);
        if (InternalPages.Handles(request.Url))
        {
            string path = Path.Combine(InternalPages.Folder, uri.AbsolutePath.TrimStart('/'));
            return ResourceHandler.FromString(File.ReadAllText(path), mimeType: "text/html", encoding: System.Text.Encoding.UTF8);
        }
        return ResourceHandler.FromString("<!doctype html><title>页面不存在</title>", mimeType: "text/html", encoding: System.Text.Encoding.UTF8);
    }

    protected override ICookieAccessFilter GetCookieAccessFilter(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request) => manager.Settings.BlockThirdPartyCookies ? new BrowserCookieFilter(view) : null;
}

internal sealed class BrowserCookieFilter(BrowserView view) : CookieAccessFilter
{
    private bool Allowed(IRequest request) => request.ResourceType == ResourceType.MainFrame ||
        UrlUtils.RegistrableDomain(request.Url) == UrlUtils.RegistrableDomain(view.Source);
    protected override bool CanSendCookie(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, Cookie cookie) => Allowed(request);
    protected override bool CanSaveCookie(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, IResponse response, Cookie cookie) => Allowed(request);
}

internal sealed class BrowserLifeSpanHandler(BrowserView view, TabManager manager, BrowserTab tab) : LifeSpanHandler
{
    protected override bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        if (!view.IsDisposed) view.Ui(() => { if (tab.OwnsView(view)) manager.CloseTab(tab); });
        return true;
    }

    protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        string targetUrl, string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture,
        IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings,
        ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null;
        view.Ui(() => { if (tab.OwnsView(view)) manager.NotifyNewWindowRequested(tab, targetUrl); });
        return true;
    }
}

public sealed class BrowserContextMenu
{
    public string LinkUrl { get; init; }
    public string SelectionText { get; init; }
    internal List<(string Label, Action Action)> Items { get; } = new();
    public void Add(string label, Action action) => Items.Add((label, action));
}

internal sealed class BrowserContextMenuHandler(BrowserView view, TabManager manager, BrowserTab tab) : ContextMenuHandler
{
    private readonly Dictionary<CefMenuCommand, Action> _actions = new();
    protected override void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IContextMenuParams parameters, IMenuModel model)
    {
        _actions.Clear();
        var menu = new BrowserContextMenu { LinkUrl = parameters.LinkUrl, SelectionText = parameters.SelectionText };
        manager.NotifyContextMenuRequested(tab, menu);
        if (menu.Items.Count == 0) return;
        model.AddSeparator();
        for (int i = 0; i < menu.Items.Count; i++)
        {
            var command = (CefMenuCommand)((int)CefMenuCommand.UserFirst + i);
            model.AddItem(command, menu.Items[i].Label);
            _actions[command] = menu.Items[i].Action;
        }
    }
    protected override bool OnContextMenuCommand(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IContextMenuParams parameters, CefMenuCommand commandId, CefEventFlags eventFlags)
    {
        if (!_actions.TryGetValue(commandId, out Action action)) return false;
        view.Ui(action);
        return true;
    }
}

internal sealed class BrowserDownloadHandler(BrowserView view, TabManager manager, bool showDialog = true, string folder = null) : DownloadHandler
{
    private readonly Dictionary<int, DownloadItem> _downloads = new();
    protected override bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser,
        CefSharp.DownloadItem downloadItem, IBeforeDownloadCallback callback)
    {
        int id = downloadItem.Id;
        string url = downloadItem.Url;
        string name = Path.GetFileName(downloadItem.SuggestedFileName);
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        if (view.IsDisposed) { callback.Dispose(); return true; }
        view.Ui(() =>
        {
            using (callback)
            {
                if (callback.IsDisposed) return;
                _downloads[id] = manager.Downloads.Begin(url, name);
                string target = folder ?? manager.Downloads.Folder;
                Directory.CreateDirectory(target);
                callback.Continue(Path.Combine(target, name), showDialog);
                manager.NotifyDownloadsChanged();
            }
        });
        return true;
    }
    protected override void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser,
        CefSharp.DownloadItem downloadItem, IDownloadItemCallback callback)
    {
        int id = downloadItem.Id;
        long received = downloadItem.ReceivedBytes, total = downloadItem.TotalBytes;
        string path = downloadItem.FullPath;
        bool complete = downloadItem.IsComplete, canceled = downloadItem.IsCancelled;
        view.Ui(() =>
        {
            if (!_downloads.TryGetValue(id, out var item)) return;
            manager.Downloads.UpdateProgress(item, received, total);
            if (complete) { manager.Downloads.Complete(item, path, total); _downloads.Remove(id); }
            else if (canceled) { manager.Downloads.Fail(item, "下载已取消或中断"); _downloads.Remove(id); }
            manager.NotifyDownloadsChanged();
        });
    }
}
