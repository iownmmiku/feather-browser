using System.Reflection;
using System.Text;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>
/// 轻量广告与追踪拦截器。
///
/// <p>取舍：不做 ABP 那种完整的规则引擎（规则编译后动辄几十 MB 内存、匹配也慢）。
/// 这里只做两件成本极低的事：
/// <list type="number">
///   <item>域名黑名单，HashSet 精确匹配 + 父域匹配，O(1)；</item>
///   <item>路径关键字黑名单，仅对子资源生效，避免误伤正文。</item>
/// </list>
///
/// <p>命中后返回一个空的 200 响应，网页不会因为拦截而报错或卡住。
/// </summary>
public sealed class AdBlocker
{
    private static readonly string[] PathKeywords =
    {
        "/ads/", "/ad/", "/adv/", "/advert", "/banner", "/popunder",
        "/analytics", "/statistics", "/stats.",
        "/beacon", "/pagead", "/doubleclick", "/gampad",
        "/adserver", "/adservice", "/adframe", "/adimage", "/adjs",
        "/sponsor", "/click.php", "/impression",
        "/log.gif", "/1x1.gif", "/blank.gif", "/counter", "/telemetry",
    };

    private readonly HashSet<string> _hostBlacklist = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hostWhitelist = new(StringComparer.Ordinal);

    /// <summary>累计拦截次数，供状态栏显示。</summary>
    public long BlockedCount { get; private set; }

    public bool Enabled { get; set; } = true;

    public int HostRuleCount => _hostBlacklist.Count;

    public AdBlocker()
    {
        LoadEmbedded();
        LoadUserRules();
    }

    public void ResetCounter() => BlockedCount = 0;

    // ---------------------------------------------------------------- 规则加载

    private void LoadEmbedded()
    {
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            // 找出内嵌的 blocklist.txt（根命名空间可能变化，这里用后缀匹配更稳）
            string name = Array.Find(assembly.GetManifestResourceNames(),
                n => n.EndsWith("blocklist.txt", StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                Log.Warn("未找到内置拦截规则资源，仅启用路径关键字拦截");
                return;
            }

            using Stream stream = assembly.GetManifestResourceStream(name);
            if (stream == null)
            {
                return;
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                AddHostLine(line, _hostBlacklist);
            }
            Log.Info($"内置拦截规则载入 {_hostBlacklist.Count} 条域名");
        }
        catch (Exception ex)
        {
            Log.Error("载入内置拦截规则失败", ex);
        }
    }

    private void LoadUserRules()
    {
        try
        {
            string path = AppPaths.UserBlockListFile;
            if (!File.Exists(path))
            {
                return;
            }
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw;
                if (line.StartsWith('!'))
                {
                    AddHostLine(line[1..], _hostWhitelist);
                }
                else
                {
                    AddHostLine(line, _hostBlacklist);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("载入用户拦截规则失败: " + ex.Message);
        }
    }

    private static void AddHostLine(string line, HashSet<string> target)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        string text = line.Trim().ToLowerInvariant();
        if (text.Length == 0 || text[0] == '#' || text[0] == '[')
        {
            return;
        }

        // 兼容 hosts 文件格式：0.0.0.0 example.com
        if (text.StartsWith("0.0.0.0 ") || text.StartsWith("127.0.0.1 "))
        {
            int space = text.IndexOf(' ');
            if (space > 0)
            {
                text = text[(space + 1)..].Trim();
            }
        }

        // 兼容 ABP 写法 ||example.com^
        if (text.StartsWith("||"))
        {
            text = text[2..];
        }

        int end = text.Length;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '^' or '/' or '$' or '*' or ' ')
            {
                end = i;
                break;
            }
        }
        text = text[..end].Trim();

        if (text.StartsWith("*."))
        {
            text = text[2..];
        }
        if (text.StartsWith('.'))
        {
            text = text[1..];
        }

        if (text.Length > 0 && text.Contains('.'))
        {
            target.Add(text);
        }
    }

    // ---------------------------------------------------------------- 决策

    /// <summary>
    /// 判断一个请求是否应当被拦截。
    /// </summary>
    /// <param name="url">请求地址。</param>
    /// <param name="isMainFrame">是否为主文档请求。</param>
    /// <returns>true 表示应当拦截。</returns>
    public bool ShouldBlock(string url, bool isMainFrame)
    {
        if (!Enabled || string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 主文档永远放行，否则会出现「点了链接却打不开」的诡异现象
        if (isMainFrame)
        {
            return false;
        }

        string lower = url.ToLowerInvariant();
        string host = UrlUtils.RawHostOf(url);
        if (host.Length > 0)
        {
            if (IsWhitelisted(host))
            {
                return false;
            }
            if (IsHostBlocked(host))
            {
                BlockedCount++;
                return true;
            }
        }

        if (MatchesPathKeyword(lower))
        {
            BlockedCount++;
            return true;
        }

        return false;
    }

    private bool IsWhitelisted(string host)
    {
        foreach (string rule in _hostWhitelist)
        {
            if (UrlUtils.HostMatches(host, rule))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsHostBlocked(string host)
    {
        if (_hostBlacklist.Contains(host))
        {
            return true;
        }

        int index = host.IndexOf('.');
        while (index > 0 && index < host.Length - 1)
        {
            string parent = host[(index + 1)..];
            if (_hostBlacklist.Contains(parent))
            {
                return true;
            }
            index = host.IndexOf('.', index + 1);
        }
        return false;
    }

    private static bool MatchesPathKeyword(string lowerUrl)
    {
        foreach (string keyword in PathKeywords)
        {
            if (lowerUrl.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
