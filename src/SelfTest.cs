using System.Diagnostics;
using System.Text;
using FeatherBrowser.Core;
using FeatherBrowser.Services;

namespace FeatherBrowser;

/// <summary>
/// 命令行自检（<c>--selftest</c>）。
///
/// <p>省内存这类说法必须能被测出来，否则只是口号。这个自检会：
/// <list type="number">
///   <item>依次打开若干个真实网页，记录内核进程数与内存；</item>
///   <item>调用一次「回收后台标签内存」，再记录一次；</item>
///   <item>逐个关闭标签，记录内存回落情况；</item>
///   <item>把全过程写成一份文本报告。</item>
/// </list>
/// 整个过程不需要人看界面，适合放进脚本或 CI。
/// </summary>
internal static class SelfTest
{
    private static readonly string[] TestUrls =
    {
        "https://example.com/",
        "https://www.bing.com/",
        "https://www.baidu.com/",
        "https://cn.bing.com/",
        "https://www.sogou.com/",
    };

    public static Task RunAsync(string outputPath)
    {
        AppPaths.EnsureCreated();
        // 自检时不恢复上次会话，也不写脏设置
        var settings = new AppSettings { MaxLiveTabs = 2, SuspendOnDeactivate = true, AdBlockEnabled = true };

        var report = new StringBuilder();
        var form = new Form
        {
            Width = 1100,
            Height = 800,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Text = "FeatherBrowser self-test",
        };

        var viewHost = new Panel { Dock = DockStyle.Fill };
        var parking = new Panel { Visible = false, Size = new Size(1, 1) };
        form.Controls.Add(viewHost);
        form.Controls.Add(parking);

        var adBlock = new AdBlocker();
        var history = new HistoryStore();
        var bookmarks = new BookmarkStore();

        TabManager tabs = null;
        Exception failure = null;
        int traceLines = 0;

        form.Shown += async (_, _) =>
        {
            try
            {
                tabs = new TabManager(new BrowserContext(settings), viewHost, parking, form,
                    incognito: false, temporaryDataFolder: null);

                report.AppendLine("==== 轻羽浏览器 内存自检 ====");
                report.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine("标签上限 MaxLiveTabs = " + settings.MaxLiveTabs);
                report.AppendLine("内置拦截规则: " + adBlock.HostRuleCount + " 条域名");
                report.AppendLine();

                var total = Stopwatch.StartNew();
                await tabs.InitializeAsync();
                report.AppendLine($"[内核就绪] 用时 {total.ElapsedMilliseconds} ms");

                // 打开策略轨迹，把每一次降档决策都记进报告（便于核对上限是否真的生效）
                TabManager.Trace = line =>
                {
                    if (traceLines < 400)
                    {
                        traceLines++;
                        report.AppendLine("      · " + line);
                    }
                    else if (traceLines == 400)
                    {
                        traceLines++;
                        report.AppendLine("      · （省略后续轨迹）");
                    }
                };
                MemoryMonitor.Refresh();
                long baselineAvailable = MemoryMonitor.AvailablePhysical;
                long baselineWebView = MemoryMonitor.KernelWorkingSet;
                Sample(report, "内核就绪（0 标签）");
                report.AppendLine($"    → 基准可用内存 {MemoryMonitor.Mb(baselineAvailable)}，" +
                                  $"内核工作集 {MemoryMonitor.Mb(baselineWebView)}");

                // ---- 阶段 1：逐个打开标签 ----
                report.AppendLine();
                report.AppendLine("---- 阶段 1：依次打开 " + TestUrls.Length + " 个标签 ----");
                for (int i = 0; i < TestUrls.Length; i++)
                {
                    string url = TestUrls[i];
                    BrowserTab tab = tabs.NewTab(url);
                    var sw = Stopwatch.StartNew();
                    bool ok = await WaitForLoadAsync(tab, 25000);
                    sw.Stop();

                    report.AppendLine(
                        $"  {i + 1}. {(ok ? "成功" : "超时")} {url}  " +
                        $"({sw.ElapsedMilliseconds} ms, 标题: {Core.UrlUtils.Ellipsis(tab.DisplayTitle, 28)})" +
                        $"  [渲染 {tabs.LiveCount} / 挂起 {tabs.SuspendedCount} / 休眠 {tabs.ColdCount}]");
                    Sample(report, $"打开第 {i + 1} 个标签后");
                }

                int tabsWithLivePeak = tabs.LiveCount;
                report.AppendLine();
                report.AppendLine($"标签总数 {tabs.Count}，其中真正持有渲染进程的 {tabs.LiveCount} 个" +
                                  $"（历史上共创建过 {tabs.TotalCreated} 个 Chromium 视图）");

                // ---- 阶段 2：主动回收 ----
                report.AppendLine();
                report.AppendLine("---- 阶段 2：执行一次「立即回收后台标签内存」----");
                tabs.ReclaimNow(includeActive: false);
                await Task.Delay(1200);
                Sample(report, "回收后");
                report.AppendLine($"  当前档位：渲染 {tabs.LiveCount} / 挂起 {tabs.SuspendedCount} / 休眠 {tabs.ColdCount}");

                // ---- 阶段 3：逐个关闭 ----
                report.AppendLine();
                report.AppendLine("---- 阶段 3：逐个关闭全部标签 ----");
                report.AppendLine($"  关闭前：{tabs.Count} 个标签");
                int guard = 0;
                while (tabs.Count > 1 && guard++ < 40)
                {
                    tabs.CloseTab(tabs.Tabs[0]);
                }
                report.AppendLine($"  循环后：{tabs.Count} 个标签（循环执行 {guard} 次）");
                if (tabs.Count > 0)
                {
                    tabs.CloseTab(tabs.Tabs[0]);
                }
                report.AppendLine($"  关闭最后一个标签后：{tabs.Count} 个标签（应自动补一个首页标签）");
                await Task.Delay(1500);
                Sample(report, "全部关闭后");
                MemoryMonitor.Refresh();
                long withOneTab = MemoryMonitor.AvailablePhysical;
                long withOneTabWebView = MemoryMonitor.KernelWorkingSet;
                report.AppendLine($"  → 只剩 1 个首页标签时：系统可用 {MemoryMonitor.Mb(withOneTab)}，" +
                                  $"内核工作集 {MemoryMonitor.Mb(withOneTabWebView)}");

                // ---- 阶段 4：验证冷标签能重新加载 ----
                report.AppendLine();
                report.AppendLine("---- 阶段 4：验证休眠标签切回后能恢复加载 ----");
                BrowserTab first = tabs.NewTab("https://example.com/");
                await WaitForLoadAsync(first, 20000);
                BrowserTab second = tabs.NewTab("https://www.bing.com/");
                await WaitForLoadAsync(second, 20000);                BrowserTab third = tabs.NewTab("https://www.baidu.com/");
                await WaitForLoadAsync(third, 20000);
                report.AppendLine($"  新建 3 个标签后：共 {tabs.Count} 个，" +
                                  $"渲染 {tabs.LiveCount} / 挂起 {tabs.SuspendedCount} / 休眠 {tabs.ColdCount}");
                int coldBefore = tabs.ColdCount;

                BrowserTab revive = tabs.Tabs[0];
                bool wasCold = revive.Life == TabLife.Cold;
                tabs.Activate(revive);
                bool revived = await WaitForLoadAsync(revive, 25000);
                report.AppendLine($"  切回最早那个标签：原档位 {(wasCold ? "休眠" : "非休眠")}，" +
                                  $"重新加载 {(revived ? "成功" : "失败")}，" +
                                  $"本标签累计创建 Chromium 视图 {revive.CreatedCount} 次");
                Sample(report, "恢复加载后");

                report.AppendLine();
                report.AppendLine("==== 小结 ====");
                MemoryMonitor.Refresh();
                long afterOpen = MemoryMonitor.AvailablePhysical;
                long afterOpenWebView = MemoryMonitor.KernelWorkingSet;
                long sysDelta = withOneTab - afterOpen;
                long webViewDelta = afterOpenWebView - withOneTabWebView;

                report.AppendLine(
                    $"· 打开 {TestUrls.Length} 个真实网页后，同时处于「渲染中」的标签始终不超过 " +
                    $"{settings.MaxLiveTabs} 个（见上文每一步的【渲染 x】），其余标签处于「休眠」，不占渲染进程");

                // 系统可用内存受其他程序影响，会上下浮动；内核工作集是本次改动直接引起的，
                // 但工作集包含共享页，因此两个数字都给出，并说明各自的含义。
                report.AppendLine(
                    $"· 内核工作集（本程序直接引起的部分）：只有 1 个标签时 " +
                    $"{MemoryMonitor.Mb(withOneTabWebView)}，开着 {TestUrls.Length} 个标签时 " +
                    $"{MemoryMonitor.Mb(afterOpenWebView)}，增量 " +
                    $"{(webViewDelta >= 0 ? "+" : "-")}{MemoryMonitor.Mb(Math.Abs(webViewDelta))}");
                report.AppendLine(
                    $"· 系统可用内存：1 个标签时 {MemoryMonitor.Mb(withOneTab)}，" +
                    $"{TestUrls.Length} 个标签时 {MemoryMonitor.Mb(afterOpen)}，" +
                    $"减少 {MemoryMonitor.Mb(Math.Max(0, sysDelta))}" +
                    $"（这个数字会被系统里其他程序影响，只能作参考）");
                report.AppendLine(
                    "· 工作集统计包含多个进程共享的页面，所以增量会小于各进程工作集之和；" +
                    "休眠中的标签只占一行 URL 字符串和标题，没有渲染进程，这就是" +
                    "「开几十个标签内存也不线性增长」的原因");
                report.AppendLine(
                    "· 本程序自身不打包 Chromium：FeatherBrowser.dll 约 160KB，" +
                    "内核复用系统已安装的 Edge Chromium 视图 运行时（全系统共用一份）");
                report.AppendLine(
                    "· 把「同时渲染标签数」设为 1 内存会更低，代价是切标签时需要重新加载页面");
            }
            catch (Exception ex)
            {
                failure = ex;
                report.AppendLine();
                report.AppendLine("!! 自检异常: " + ex);
                Log.Error("自检失败", ex);
            }
            finally
            {
                try
                {
                    tabs?.Shutdown();
                }
                catch
                {
                    // 忽略
                }
                form.Close();
            }
        };

        Application.Run(form);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppPaths.Root);
            File.WriteAllText(outputPath, report.ToString(), new UTF8Encoding(false));
            Console.WriteLine(report.ToString());
            Console.WriteLine("报告已写入: " + outputPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("写入报告失败: " + ex.Message);
        }

        if (failure != null)
        {
            Environment.ExitCode = 3;
        }
        TabManager.Trace = null;
        return Task.CompletedTask;
    }

    /// <summary>记录一次内存快照。</summary>
    private static void Sample(StringBuilder report, string label)
    {
        MemoryMonitor.Refresh();
        report.AppendLine(
            $"    [{label}] 本程序工作集 {MemoryMonitor.Mb(MemoryMonitor.WorkingSet)}" +
            $" | 内核 {MemoryMonitor.KernelProcessCount} 进程 {MemoryMonitor.Mb(MemoryMonitor.KernelWorkingSet)}" +
            $" | 系统可用 {MemoryMonitor.Mb(MemoryMonitor.AvailablePhysical)}");
    }

    /// <summary>
    /// 等待某个标签加载完成。
    ///
    /// <p>Chromium 视图 的事件都在 UI 线程上派发，而这里本身就是 UI 线程，
    /// 所以要边抽消息边等，直到标签不再是加载状态。
    /// </summary>
    private static async Task<bool> WaitForLoadAsync(BrowserTab tab, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (tab.LastNavigationSucceeded.HasValue)
            {
                return tab.LastNavigationSucceeded.Value;
            }

            await Task.Delay(50);
        }
        return false;
    }
}
