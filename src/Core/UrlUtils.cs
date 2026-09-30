using System.Text;

namespace FeatherBrowser.Core;

/// <summary>
/// 地址与搜索词识别。纯字符串运算，不依赖正则，也不会分配大对象。
/// </summary>
public static class UrlUtils
{
    /// <summary>内置首页的伪协议。</summary>
    public const string InternalHome = "feather://home";

    private static readonly string[] KnownTlds =
    {
        "com", "cn", "net", "org", "io", "gov", "edu", "me", "tv", "cc", "co", "info", "biz",
        "xyz", "top", "site", "online", "app", "dev", "shop", "vip", "club", "art", "tech",
        "uk", "us", "jp", "kr", "de", "fr", "ru", "hk", "tw", "sg", "au", "ca", "in", "br",
        "moe", "fun", "live", "pro", "store", "wiki", "name", "group", "work", "space",
    };

    public static bool IsInternal(string url) =>
        !string.IsNullOrEmpty(url) &&
        url.StartsWith("feather://", StringComparison.OrdinalIgnoreCase);

    /// <summary>判断输入应当作为网址打开还是送去搜索。</summary>
    public static bool LooksLikeUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string text = input.Trim();
        if (text.Contains(' '))
        {
            return false;
        }

        if (HasScheme(text) || text.StartsWith("feather://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 去掉可能的端口与路径，只看主机部分
        string hostPart = text;
        int colon = hostPart.IndexOf(':');
        if (colon > 0)
        {
            hostPart = hostPart[..colon];
        }
        int slash = hostPart.IndexOf('/');
        if (slash > 0)
        {
            hostPart = hostPart[..slash];
        }

        if (IsIpv4(hostPart))
        {
            return true;
        }

        int dot = text.IndexOf('.');
        if (dot <= 0)
        {
            return false;
        }

        string tld = text[(text.LastIndexOf('.') + 1)..];
        int cut = tld.Length;
        for (int i = 0; i < tld.Length; i++)
        {
            if (!char.IsLetterOrDigit(tld[i]))
            {
                cut = i;
                break;
            }
        }
        tld = tld[..cut].ToLowerInvariant();
        if (tld.Length == 0)
        {
            return false;
        }

        foreach (string known in KnownTlds)
        {
            if (string.Equals(known, tld, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // 未知后缀：长度 2~6 且整体无空格，按网址处理更符合直觉
        return tld.Length is >= 2 and <= 6;
    }

    private static bool HasScheme(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static bool IsIpv4(string value)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }
        foreach (string part in parts)
        {
            if (part.Length is 0 or > 3)
            {
                return false;
            }
            foreach (char c in part)
            {
                if (!char.IsDigit(c))
                {
                    return false;
                }
            }
            if (int.Parse(part) > 255)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>把地址栏输入整理成可加载的 URL。</summary>
    public static string Normalize(string input, string searchTemplate)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return InternalHome;
        }

        string text = input.Trim();
        if (LooksLikeUrl(text))
        {
            return HasScheme(text) ? text : "http://" + text;
        }

        string template = string.IsNullOrEmpty(searchTemplate)
            ? "https://www.bing.com/search?q=%s"
            : searchTemplate;
        return template.Replace("%s", Uri.EscapeDataString(text));
    }

    /// <summary>取主机名（去掉 www.），失败返回空串。</summary>
    public static string HostOf(string url)
    {
        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            string host = uri.Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>完整主机名，用于拦截匹配。</summary>
    public static string RawHostOf(string url)
    {
        try
        {
            return new Uri(url, UriKind.Absolute).Host.ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>地址栏展示：去掉 scheme 与根路径的斜杠。</summary>
    public static string PrettyForBar(string url)
    {
        if (string.IsNullOrEmpty(url) || IsInternal(url))
        {
            return "";
        }

        string text = url;
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = text[8..];
        }
        else if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..];
        }

        if (text.EndsWith('/') && text.IndexOf('/') == text.Length - 1)
        {
            text = text[..^1];
        }
        return text;
    }

    /// <summary>父域匹配：a.ads.example.com 命中 ads.example.com。</summary>
    public static bool HostMatches(string host, string rule)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(rule))
        {
            return false;
        }
        return host.Equals(rule, StringComparison.Ordinal) ||
               host.EndsWith("." + rule, StringComparison.Ordinal);
    }

    /// <summary>截断过长的显示文本，避免列表控件反复测量超长字符串。</summary>
    public static string Ellipsis(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text ?? "";
        }
        return text[..max] + "…";
    }

    /// <summary>从任意字符串里抽取第一个 http(s) 链接（用于命令行参数与剪贴板）。</summary>
    public static string ExtractFirstUrl(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        foreach (string token in text.Split(' ', '\t', '\r', '\n', '"', '\''))
        {
            string candidate = token.Trim();
            if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        return "";
    }

    /// <summary>构造内置首页的 HTML。</summary>
    /// <param name="searchName">当前搜索引擎名称，显示在搜索框提示里。</param>
    /// <param name="dark">深色主题：背景、文字、卡片都换成深色一套。</param>
    public static string BuildHomeHtml(string searchName, bool dark = false)
    {
        // 主题相关的色值集中在这里，浅色/深色只换这一组。
        string pageBg = dark
            ? "radial-gradient(120% 90% at 50% 0%,#1d2030 0%,#141519 55%,#101114 100%)"
            : "radial-gradient(120% 90% at 50% 0%,#ffffff 0%,#f4f7fd 55%,#eaeff9 100%)";
        string textMain = dark ? "#e8eaee" : "#1c2026";
        string textSub = dark ? "#8d94a3" : "#787f8c";
        string textFoot = dark ? "#5c626e" : "#a8adb8";
        string cardBg = dark ? "rgba(255,255,255,.06)" : "#ffffff";
        string cardBorder = dark ? "rgba(255,255,255,.08)" : "transparent";
        string cardShadow = dark
            ? "0 4px 16px rgba(0,0,0,.45)"
            : "0 6px 26px rgba(60,90,160,.12)";
        string tileShadow = dark
            ? "0 3px 10px rgba(0,0,0,.4)"
            : "0 3px 10px rgba(60,90,160,.12)";
        string tileBg = dark ? "rgba(255,255,255,.07)" : "#ffffff";
        string accent = dark ? "#7ba0ff" : "#4a7cf7";
        string accentHover = dark ? "#6b8ff0" : "#3a68dd";

        var sb = new StringBuilder(3072);
        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("<meta name=\"color-scheme\" content=\"")
          .Append(dark ? "dark" : "light").Append("\">")
          .Append("<title>新标签页</title><style>")
          .Append("*{box-sizing:border-box}")
          .Append("html,body{margin:0;height:100%}")
          .Append("body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;color:").Append(textMain)
          .Append(";background:").Append(pageBg)
          .Append(";background-attachment:fixed;display:flex;flex-direction:column;")
          .Append("align-items:center;justify-content:center}")
          .Append("h1{font-size:34px;font-weight:600;margin:0 0 8px;letter-spacing:3px}")
          .Append("p.sub{margin:0 0 38px;color:").Append(textSub).Append(";font-size:14px}")
          // 搜索框：整条做成胶囊，与浏览器工具栏的圆角风格一致
          .Append("form{width:min(660px,88vw);display:flex;background:").Append(cardBg)
          .Append(";border:1px solid ").Append(cardBorder)
          .Append(";border-radius:28px;box-shadow:").Append(cardShadow)
          .Append(";overflow:hidden;transition:box-shadow .18s ease,transform .18s ease}")
          .Append("form:focus-within{transform:translateY(-1px)}")
          .Append("input{flex:1;border:0;outline:0;background:transparent;color:").Append(textMain)
          .Append(";padding:17px 26px;font-size:15px;font-family:inherit}")
          .Append("input::placeholder{color:").Append(textSub).Append("}")
          .Append("button{border:0;background:").Append(accent).Append(";color:#fff;padding:0 30px;")
          .Append("font-size:14px;font-family:inherit;cursor:pointer;transition:background .15s}")
          .Append("button:hover{background:").Append(accentHover).Append("}")
          .Append(".quick{margin-top:38px;display:flex;gap:18px;flex-wrap:wrap;justify-content:center}")
          .Append(".quick a{display:flex;flex-direction:column;align-items:center;gap:8px;width:82px;")
          .Append("text-decoration:none;color:").Append(textSub).Append(";font-size:12px}")
          .Append(".ico{width:50px;height:50px;border-radius:16px;background:").Append(tileBg)
          .Append(";border:1px solid ").Append(cardBorder)
          .Append(";display:flex;align-items:center;justify-content:center;font-size:19px;")
          .Append("font-weight:600;color:").Append(accent)
          .Append(";box-shadow:").Append(tileShadow)
          .Append(";transition:transform .16s ease,background .16s ease}")
          .Append(".quick a:hover .ico{transform:translateY(-3px);background:")
          .Append(dark ? "rgba(255,255,255,.13)" : "#f5f8ff").Append("}")
          .Append(".quick a:hover{color:").Append(textMain).Append("}")
          .Append("footer{position:fixed;bottom:16px;color:").Append(textFoot).Append(";font-size:11px}")
          .Append("</style></head><body>")
          .Append("<h1>轻羽浏览器</h1><p class=\"sub\">Windows 原生 · 低内存 · 基于 Edge 内核</p>")
          .Append("<form onsubmit=\"go(event)\"><input id=\"q\" placeholder=\"搜索 ")
          .Append(Escape(searchName))
          .Append(" 或输入网址\" autocomplete=\"off\"><button type=\"submit\">前往</button></form>")
          .Append("<div class=\"quick\">")
          .Append(QuickLink("B", "哔哩哔哩", "https://www.bilibili.com"))
          .Append(QuickLink("知", "知乎", "https://www.zhihu.com"))
          .Append(QuickLink("G", "GitHub", "https://github.com"))
          .Append(QuickLink("百", "百度", "https://www.baidu.com"))
          .Append(QuickLink("T", "淘宝", "https://www.taobao.com"))
          .Append("</div><footer>feather://home · 按 Ctrl+L 可直接输入网址</footer>")
          .Append("<script>function go(e){e.preventDefault();var v=document.getElementById('q').value.trim();")
          .Append("if(v)location.href='feather://search?q='+encodeURIComponent(v);}</script>")
          .Append("</body></html>");
        return sb.ToString();
    }

    private static string QuickLink(string initial, string label, string url) =>
        $"<a href=\"{url}\"><span class=\"ico\">{Escape(initial)}</span>{Escape(label)}</a>";

    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        return text.Replace("&", "&amp;")
                   .Replace("<", "&lt;")
                   .Replace(">", "&gt;")
                   .Replace("\"", "&quot;")
                   .Replace("'", "&#39;");
    }
}
