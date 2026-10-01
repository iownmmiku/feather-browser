using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FeatherBrowser.Core;
using FeatherBrowser.Services;
using FeatherBrowser.UI;

namespace FeatherBrowser.Tests;

internal static class Program
{
    private static readonly List<string> Results = new();
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "FeatherRegression_" + Guid.NewGuid().ToString("N"));
        AppPaths.OverrideRoot(root);
        AppPaths.EnsureCreated();
        InternalPages.Materialize();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Fail("UI exception", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Fail("Unobserved task", e.Exception); e.SetObserved(); };

        using var form = new Form
        {
            Width = 800, Height = 600, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
        };
        form.Shown += async (_, _) =>
        {
            try { await RunAsync(form); }
            catch (Exception ex) { Fail("Runner", ex); }
            finally { form.Close(); }
        };
        Application.Run(form);
        string report = string.Join(Environment.NewLine, Results) +
            $"{Environment.NewLine}Failures: {_failures}{Environment.NewLine}Data: {root}";
        File.WriteAllText(Path.Combine(root, "regression-report.txt"), report);
        Console.WriteLine(report);
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunAsync(Form form)
    {
        await Check("Address recognition uses the host and preserves internal/file URLs", () =>
        {
            string template = "https://search.test/?q=%s";
            Equal("https://example.com/a.js?q=x.y", UrlUtils.Normalize("example.com/a.js?q=x.y", template));
            Equal("http://localhost:8080/a", UrlUtils.Normalize("localhost:8080/a", template));
            Equal("https://localhost.example.com", UrlUtils.Normalize("localhost.example.com", template));
            Equal("http://[::1]:8080/a", UrlUtils.Normalize("[::1]:8080/a", template));
            Equal(UrlUtils.InternalHome, UrlUtils.Normalize(UrlUtils.InternalHome, template));
            Equal("file:///C:/my file.html", UrlUtils.Normalize("file:///C:/my file.html", template));
            Equal("https://search.test/?q=" + Uri.EscapeDataString("你好 世界"), UrlUtils.Normalize("你好 世界", template));
            Require(InternalPages.Handles(InternalPages.BookmarksUrl + "?q=x#top"));
            Require(!InternalPages.Handles("https://feather.local:444/bookmarks.html"));
            Require(!InternalPages.Handles("https://feather.local/unknown.html"));
            return Task.CompletedTask;
        });

        await Check("Passwords stay within the saved scheme, host and port", () =>
        {
            var store = new PasswordStore();
            store.Clear();
            store.Save("https://alice.github.io/login", "same", "alpha");
            Require(store.FindForUrl("https://bob.github.io").Count == 0);
            Require(store.FindForUrl("http://alice.github.io").Count == 0);
            Require(store.FindForUrl("https://alice.github.io:444").Count == 0);
            store.Save("https://bob.github.io", "same", "beta");
            Equal("alpha", store.RevealPassword(store.FindForUrl("https://alice.github.io/path").Single()));
            Equal("beta", store.RevealPassword(store.FindForUrl("https://bob.github.io").Single()));
            Require(!File.ReadAllText(AppPaths.PasswordsFile).Contains("alpha"));
            store.Clear();
            Require(LoginAutofill.BuildScript(Array.Empty<(string, string)>()).Contains("addEventListener('submit'"));
            return Task.CompletedTask;
        });

        using var server = new TestServer();
        await Check("Theme font refresh preserves controls and existing font references", () =>
        {
            using var probe = new Form { ShowInTaskbar = false, Location = new Point(-32000, -32000), StartPosition = FormStartPosition.Manual };
            using var text = new TextBox { Font = Theme.UiFont };
            using var title = new Label { Font = Theme.UiFontTitle };
            probe.Controls.Add(text);
            probe.Controls.Add(title);
            probe.Show();
            Font previous = Theme.UiFont;
            Theme.Scale = 1.3f;
            Theme.RefreshFonts();
            Equal(Theme.Pt(10), text.Font.SizeInPoints);
            Equal(Theme.Pt(15), title.Font.SizeInPoints);
            using var later = new TextBox { Font = previous };
            probe.Controls.Add(later);
            _ = later.Handle;
            Require(previous.Height > 0);
            Theme.Scale = 1.15f;
            Theme.RefreshFonts();
            probe.Close();
            return Task.CompletedTask;
        });
        await WithManager(form, new AppSettings { MaxLiveTabs = 2 }, async tabs =>
        {
            await Check("Homepage remains internal and does not enter history", async () =>
            {
                var tab = tabs.NewTab(UrlUtils.InternalHome);
                await Loaded(tab);
                Require(tab.IsHomePage);
                Equal(UrlUtils.InternalHome, tab.Url);
                Require(tabs.History.Count == 0);

                string oldTemplate = tabs.Settings.Engine.Template;
                try
                {
                    tabs.Settings.Engine.Template = server.Url + "search?q=%s";
                    await tab.View.CoreWebView2.ExecuteScriptAsync("location.href='feather://search?q=hello%20world'");
                    await Until(() => tab.Url.StartsWith(server.Url + "search") && tab.Progress == 100 && !tab.IsLoading);
                    Equal(server.Url + "search?q=hello%20world", tab.Url);
                }
                finally { tabs.Settings.Engine.Template = oldTemplate; }
            });

            await Check("Cold background tabs do not consume the resident budget", async () =>
            {
                tabs.CloseAllExcept(tabs.Active);
                BrowserTab kept = tabs.Active;
                tabs.NewTab(UrlUtils.InternalHome, activate: false);
                var active = tabs.NewTab(UrlUtils.InternalHome);
                await Loaded(active);
                Require(kept.View != null, "Previously loaded background view was evicted by a cold tab");
                Equal(2, tabs.Tabs.Count(t => t.View != null));
            });

            await Check("Rapid initialization, closing and recreation obey the resident limit", async () =>
            {
                tabs.Settings.MaxLiveTabs = 1;
                for (int i = 0; i < 16; i++)
                {
                    var pending = tabs.NewTab(UrlUtils.InternalHome);
                    Require(tabs.Tabs.Count(t => t.View != null) <= 1);
                    if (i % 3 == 0) tabs.CloseTab(pending);
                }
                var current = tabs.Active;
                await Loaded(current);
                Require(tabs.Tabs.Count(t => t.View != null) == 1);
                var old = current.View;
                current.DestroyView();
                tabs.Activate(current);
                await Loaded(current);
                Require(!ReferenceEquals(old, current.View));
                Require(current.IsHomePage);
                Require(current.View.Parent != null);
            });

            await Check("A pending suspend cannot hide the reactivated tab", async () =>
            {
                tabs.Settings.MaxLiveTabs = 2;
                var first = tabs.Active;
                var second = tabs.NewTab(UrlUtils.InternalHome);
                await Loaded(second);
                var pending = first.SuspendAsync();
                tabs.Activate(first);
                await pending;
                Equal(TabLife.Live, first.Life);
                Require(!first.View.CoreWebView2.IsSuspended);
                // Parent form is deliberately offscreen, so visibility is checked on the controller state above.
                Equal(first, tabs.Active);
            });

            await Check("Image loading switch blocks the actual network request", async () =>
            {
                tabs.Settings.LoadImages = false;
                var tab = tabs.NewTab(server.Url + "images?blocked");
                await Loaded(tab);
                await Task.Delay(200);
                Equal(0, server.ImageRequests);
                tabs.Settings.LoadImages = true;
                tab.NavigateTo(server.Url + "images?enabled");
                await Until(() => server.ImageRequests > 0);
                await Loaded(tab);
            });

            await Check("External content cannot call the internal management bridge", async () =>
            {
                int messages = 0;
                void Received(BrowserTab _, string json, string source) => messages++;
                tabs.InternalPageMessage += Received;
                try
                {
                    var tab = tabs.NewTab(server.Url + "bridge");
                    await Loaded(tab);
                    await tab.View.CoreWebView2.ExecuteScriptAsync("chrome.webview.postMessage(JSON.stringify({feather:'bookmarks-clear'}))");
                    await Task.Delay(200);
                    Equal(0, messages);
                    tab.NavigateTo(InternalPages.BookmarksUrl);
                    await Loaded(tab);
                    await Until(() => messages > 0);
                }
                finally { tabs.InternalPageMessage -= Received; }
            });

            await Check("Disabling website JavaScript keeps the internal pages operational", async () =>
            {
                tabs.Settings.JavaScriptEnabled = false;
                try
                {
                    var tab = tabs.NewTab(server.Url + "scripts");
                    await Loaded(tab);
                    Require(!tab.View.CoreWebView2.Settings.IsScriptEnabled);
                    Equal("true", await tab.View.CoreWebView2.ExecuteScriptAsync("typeof PAGE_SCRIPT_RAN === 'undefined'"));
                    tab.NavigateTo(InternalPages.BookmarksUrl);
                    await Loaded(tab);
                    Require(tab.View.CoreWebView2.Settings.IsScriptEnabled);
                    await UntilScript(tab, "typeof window.featherUpdate === 'function'");
                }
                finally { tabs.Settings.JavaScriptEnabled = true; }
            });

            await Check("The first login submission is captured without saved accounts", async () =>
            {
                string? receivedSource = null;
                string? receivedPassword = null;
                void Submitted(BrowserTab _, string source, string user, string password)
                { receivedSource = source; receivedPassword = password; Equal("test-user", user); }
                tabs.SaveCredentialRequested += Submitted;
                try
                {
                    var tab = tabs.NewTab(server.Url + "login");
                    await Loaded(tab);
                    await UntilScript(tab, "typeof window.__featherFillPassword === 'function'");
                    await tab.View.CoreWebView2.ExecuteScriptAsync("document.querySelector('input[type=text]').value='test-user';document.querySelector('input[type=password]').value='test-password';document.querySelector('form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));");
                    await Until(() => receivedPassword != null);
                    Equal(server.Url + "login", receivedSource);
                    Equal("test-password", receivedPassword);
                }
                finally { tabs.SaveCredentialRequested -= Submitted; }
            });

            await Check("Forged account selections cannot retrieve a saved password", async () =>
            {
                tabs.Passwords.Save(server.Url, "test-user", "stored-secret");
                var tab = tabs.NewTab(server.Url + "login");
                await Loaded(tab);
                await UntilScript(tab, "typeof window.__featherFillPassword === 'function'");
                string accountId = "test-user\u0001" + UrlUtils.RegistrableDomain(server.Url);
                string message = "feather:" + JsonSerializer.Serialize(new { type = "pick", id = accountId, token = "forged" });
                await tab.View.CoreWebView2.ExecuteScriptAsync("chrome.webview.postMessage(" + JsonSerializer.Serialize(message) + ")");
                await Task.Delay(200);
                Equal("\"\"", await tab.View.CoreWebView2.ExecuteScriptAsync("document.querySelector('input[type=password]').value"));
                tab.NavigateTo(server.Url + "login?next-document");
                tabs.NotifyCredentialPicked(tab, accountId);
                await Loaded(tab);
                Equal("\"\"", await tab.View.CoreWebView2.ExecuteScriptAsync("document.querySelector('input[type=password]').value"));
                tabs.Passwords.Clear();
            });

            await Check("Browser-process failure reported by a background tab revives the active tab", async () =>
            {
                var active = tabs.Active;
                var background = tabs.Tabs.First(t => t != active && t.View != null);
                tabs.NotifyProcessFailed(background, "BrowserProcessExited", "test");
                await Loaded(active);
                Equal(active, tabs.Active);
                Require(active.View != null);
            });
        });

        await WithManager(form, new AppSettings(), async tabs =>
        {
            await Check("Private browsing does not persist history or download records", async () =>
            {
                int before = tabs.History.Count;
                var tab = tabs.NewTab(server.Url + "private");
                await Loaded(tab);
                Equal(before, tabs.History.Count);
                string file = Path.Combine(AppPaths.Root, "downloads.json");
                string? original = File.Exists(file) ? File.ReadAllText(file) : null;
                tabs.Downloads.Begin(server.Url + "private-file", "private-file");
                Equal(original, File.Exists(file) ? File.ReadAllText(file) : null);
            });
        }, incognito: true);

        await Check("Closing the first window keeps the second window and its session", async () =>
        {
            using var context = new BrowserApplicationContext();
            bool exited = false;
            context.ThreadExit += (_, _) => exited = true;
            using var first = HiddenWindow(server.Url + "first");
            using var second = HiddenWindow(server.Url + "second");
            context.OpenWindow(first);
            context.OpenWindow(second);
            try
            {
                await Until(() => GetTabs(first)?.Active?.Progress == 100 && GetTabs(second)?.Active?.Progress == 100);
                var shared = BrowserContext.Shared;
                shared.Bookmarks.Toggle(server.Url + "saved", "saved");
                first.Close();
                Require(!second.IsDisposed && !exited);
                Require(shared.AdBlock.Enabled);
                Equal(server.Url + "first", AppSettings.Load().SessionUrls.Single());
                second.Close();
                await Until(() => exited);
                Equal(server.Url + "second", AppSettings.Load().SessionUrls.Single());
                Require(new BookmarkStore().Contains(server.Url + "saved"));
                first.PersistSession(); // A repeated call after disposal must not erase the saved session.
                Equal(server.Url + "second", AppSettings.Load().SessionUrls.Single());
            }
            finally { if (!first.IsDisposed) first.Close(); if (!second.IsDisposed) second.Close(); }
        });

        await Check("Private windows clean their temporary profile without replacing the normal session", async () =>
        {
            var saved = AppSettings.Load().SessionUrls.ToArray();
            using var context = new BrowserApplicationContext();
            using var window = HiddenWindow(server.Url + "private-window", incognito: true);
            context.OpenWindow(window);
            await Until(() => GetTabs(window)?.Active?.Progress == 100);
            string profile = GetTabs(window)!.TemporaryDataFolder;
            window.Close();
            await window.CleanupTask;
            Require(!Directory.Exists(profile), "Private profile was left on disk");
            Require(saved.SequenceEqual(AppSettings.Load().SessionUrls));
        });

        await Check("Closing a private window during initialization still cleans its profile", async () =>
        {
            using var context = new BrowserApplicationContext();
            using var window = HiddenWindow(server.Url + "private-startup", incognito: true);
            context.OpenWindow(window);
            await Until(() => GetTabs(window) != null);
            string profile = GetTabs(window)!.TemporaryDataFolder;
            window.Close();
            await window.CleanupTask;
            Require(!Directory.Exists(profile));
        });

        await Check("Memory monitoring excludes other WebView2 environments", async () =>
        {
            await WithManager(form, new AppSettings(), async tabs =>
            {
                var tab = tabs.NewTab(UrlUtils.InternalHome);
                await Loaded(tab);
                var ownIds = MemoryMonitor.GetBrowserProcessIds();
                Require(ownIds.Contains((int)tab.View.CoreWebView2.BrowserProcessId));
                string otherRoot = Path.Combine(AppPaths.Root, "unregistered-profile");
                var other = await TabManager.CreateEnvironmentAsync(otherRoot, false);
                using var view = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
                form.Controls.Add(view);
                try
                {
                    await view.EnsureCoreWebView2Async(other);
                    int otherId = (int)view.CoreWebView2.BrowserProcessId;
                    Require(!MemoryMonitor.GetBrowserProcessIds().Contains(otherId));
                    MemoryMonitor.Refresh();
                    Require(MemoryMonitor.WebViewProcessCount > 0 && MemoryMonitor.WebViewWorkingSet > 0);
                }
                finally { form.Controls.Remove(view); }
            });
        });
    }

    private static MainForm HiddenWindow(string url, bool incognito = false) => new(url, incognito)
    {
        ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
    };

    private static TabManager? GetTabs(MainForm form) =>
        (TabManager?)typeof(MainForm).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);

    private static async Task WithManager(Form form, AppSettings settings, Func<TabManager, Task> action, bool incognito = false)
    {
        using var host = new Panel { Dock = DockStyle.Fill };
        using var parking = new Panel { Visible = false };
        form.Controls.Add(host);
        form.Controls.Add(parking);
        var tabs = new TabManager(new BrowserContext(settings), host, parking, form, incognito,
            incognito ? Path.Combine(AppPaths.Root, "private-test-profile") : null);
        try { await tabs.InitializeAsync(); await action(tabs); }
        finally { tabs.Shutdown(); form.Controls.Remove(host); form.Controls.Remove(parking); }
    }

    private static async Task Loaded(BrowserTab tab) =>
        await Until(() => tab.View?.CoreWebView2 != null && tab.LastNavigationSucceeded == true);

    private static async Task UntilScript(BrowserTab tab, string script)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < 15000)
        {
            if (await tab.View.CoreWebView2.ExecuteScriptAsync(script) == "true") return;
            await Task.Delay(25);
        }
        throw new TimeoutException(script);
    }

    private static async Task Until(Func<bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timeout.ElapsedMilliseconds > 20000) throw new TimeoutException("Condition was not reached");
            await Task.Delay(25);
        }
    }

    private static async Task Check(string name, Func<Task> action)
    {
        try { await action(); Results.Add("PASS " + name); }
        catch (Exception ex) { Fail(name, ex); }
    }

    private static void Fail(string name, Exception ex)
    {
        Interlocked.Increment(ref _failures);
        Results.Add("FAIL " + name + ": " + ex);
    }

    private static void Require(bool condition, string message = "Assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }

    private static void Equal<T>(T expected, T actual) =>
        Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");
}

internal sealed class TestServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _imageRequests;
    public string Url { get; }
    public int ImageRequests => Volatile.Read(ref _imageRequests);

    public TestServer()
    {
        _listener.Start();
        Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
        _loop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string request = await reader.ReadLineAsync(_stop.Token) ?? "";
                string? header;
                do { header = await reader.ReadLineAsync(_stop.Token); } while (!string.IsNullOrEmpty(header));
                string path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                byte[] body;
                string contentType;
                if (path.StartsWith("/image.png"))
                {
                    Interlocked.Increment(ref _imageRequests);
                    body = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII=");
                    contentType = "image/png";
                }
                else
                {
                    string extra = path.StartsWith("/images") ? "<img src='/image.png?" + path.Split('?').Last() + "'>" :
                        path.StartsWith("/login") ? "<form><input type='text'><input type='password'><button type='submit'>Submit</button></form>" : "Test page";
                    body = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>Local test</title></head><body>" + extra + "<script>window.PAGE_SCRIPT_RAN=true</script></body></html>");
                    contentType = "text/html; charset=utf-8";
                }
                byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}
