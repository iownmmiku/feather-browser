using System.Text;
using FeatherBrowser.Core;
using FeatherBrowser.Services;

namespace FeatherBrowser;

/// <summary>
/// 命令行形式的夸克数据导入（<c>--import-quark=...</c>）。
///
/// <p>存在的意义是把「导入」这件事做成可脚本化、可复现的操作：
/// 界面上有同样的功能（设置 → 从夸克导入），这里只是它的无界面版本，
/// 方便批量迁移或排查问题。
///
/// <p>输出只给统计数字，**不打印任何密码内容**。
/// </summary>
internal static class QuarkImportCli
{
    public static string Run(string spec, string reportPath = null)
    {
        string report = Execute(spec);

        // 这是个 WinExe：没有真实控制台时 stdout 编码不可靠，中文会乱。
        // 所以除了写 stdout，也支持把报告写到文件（UTF-8），便于脚本读取。
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            try
            {
                string full = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                File.WriteAllText(full, report, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Warn("写导入报告失败: " + ex.Message);
            }
        }

        Console.WriteLine(report);
        return report;
    }

    private static string Execute(string spec)
    {
        bool bookmarks = false;
        bool history = false;
        bool passwords = false;
        int historyLimit = 2000;

        string[] parts = (spec ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        string kinds = parts.Length > 0 ? parts[0].Trim().ToLowerInvariant() : "all";
        if (parts.Length > 1 && int.TryParse(parts[1], out int limit) && limit > 0)
        {
            historyLimit = limit;
        }

        switch (kinds)
        {
            case "all":
                bookmarks = history = passwords = true;
                break;
            case "bookmarks":
                bookmarks = true;
                break;
            case "history":
                history = true;
                break;
            case "passwords":
                passwords = true;
                break;
            case "nopasswords":
                // 有些环境不方便碰密码，给个明确的排除项
                bookmarks = history = true;
                break;
            default:
                return "参数无法识别。用法：--import-quark=all|bookmarks|history|passwords[;历史条数]";
        }

        var sb = new StringBuilder();
        sb.AppendLine("==== 从夸克导入 ====");
        sb.AppendLine("数据写入目录: " + AppPaths.Root);
        sb.AppendLine("夸克数据目录: " + QuarkImporter.QuarkUserDataPath);
        sb.AppendLine("夸克是否在运行: " + (QuarkImporter.IsQuarkRunning() ? "是（读取的是文件快照）" : "否"));

        if (!QuarkImporter.IsAvailable())
        {
            sb.AppendLine("没有找到夸克的数据目录，无法导入。");
            return sb.ToString();
        }

        var bookmarksStore = new BookmarkStore();
        var historyStore = new HistoryStore();
        var passwordStore = new PasswordStore();

        var importer = new QuarkImporter(bookmarksStore, historyStore, passwordStore);

        var result = importer.Import(bookmarks, history, passwords, historyLimit);

        // 导入内部已经在 finally 里落过盘；这里再显式兜一次，
        // 保证即使导入中途抛异常，内存里已改好的数据也不会因为进程退出而丢。
        bookmarksStore.Flush();
        historyStore.Flush();

        sb.AppendLine();
        sb.AppendLine("导入结果: " + result.Summary());
        foreach (string note in result.Notes)
        {
            sb.AppendLine("说明: " + note);
        }
        sb.AppendLine();
        sb.AppendLine("数据已写入: " + Services.AppPaths.Root);
        return sb.ToString();
    }
}
