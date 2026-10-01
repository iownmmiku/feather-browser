using System.Text;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>
/// 页面内的登录表单自动填充。
///
/// <p>安全设计（重要）：
/// <list type="bullet">
///   <item>注入页面的脚本里**只有用户名**，绝不含密码；</item>
///   <item>用户点选某个账号后，宿主才单独调用一次脚本把密码填进去；</item>
///   <item>脚本不把密码写进任何全局变量，也不发回宿主；</item>
///   <item>提交时只把用户**输入框里当前的值**发回宿主，用于询问「是否保存」。</item>
/// </list>
/// 这样即使页面里有恶意脚本，它也拿不到你保存的密码 —— 除非你自己点选。
/// </summary>
internal static class LoginAutofill
{
    /// <summary>页面发回宿主的消息前缀，避免和页面自身的 postMessage 混淆。</summary>
    public const string MessagePrefix = "feather:";

    /// <summary>
    /// 页面脚本正文。编译时作为源文件嵌入程序集。
    ///
    /// <p>把它放在 .js 文件里而不是拼 C# 字符串，是因为这段 JS 里有大量引号与正则，
    /// 塞进 C# 立即会遇到转义与「逐字字符串里不能有连续两个引号」的问题。
    /// 嵌入的代价只有几 KB。
    /// </summary>
    private static string Body => _body ??= LoadBody();

    private static string _body;

    private static string LoadBody()
    {
        try
        {
            Type type = typeof(LoginAutofill);
            string name = type.Namespace + ".login-autofill.js";
            using Stream stream = type.Assembly.GetManifestResourceStream(name);
            if (stream == null)
            {
                // 万一资源名变了，退化成「按后缀找」
                string found = Array.Find(type.Assembly.GetManifestResourceNames(),
                    n => n.EndsWith("login-autofill.js", StringComparison.OrdinalIgnoreCase));
                if (found == null)
                {
                    Log.Warn("找不到登录辅助脚本资源，自动填充将不可用");
                    return "";
                }
                using Stream fallback = type.Assembly.GetManifestResourceStream(found);
                if (fallback == null)
                {
                    return "";
                }
                using var reader2 = new StreamReader(fallback, Encoding.UTF8);
                return reader2.ReadToEnd();
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Error("读取登录辅助脚本失败", ex);
            return "";
        }
    }

    /// <summary>
    /// 生成要注入页面的脚本。
    /// </summary>
    /// <param name="accounts">当前站点可用的账号（只含用户名，不含密码）。</param>
    /// <returns>包含账号选择与表单提交监听的脚本。</returns>
    public static string BuildScript(IEnumerable<(string Id, string Username)> accounts, string pickToken = "")
    {
        var list = accounts?.ToList() ?? new List<(string Id, string Username)>();
        string body = Body;
        if (string.IsNullOrEmpty(body))
        {
            return "";
        }

        var accountsJs = new StringBuilder("[");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0)
            {
                accountsJs.Append(',');
            }
            accountsJs.Append("{id:").Append(JsString(list[i].Id))
                      .Append(",u:").Append(JsString(list[i].Username)).Append('}');
        }
        accountsJs.Append(']');

        // 用拼接而不是替换：只有账号列表是动态的，脚本正文原样附在后面
        var sb = new StringBuilder(body.Length + accountsJs.Length + 64);
        sb.Append("(function(){if(window.__featherLogin)return;window.__featherLogin=true;")
          .Append("var PICK_TOKEN=").Append(JsString(pickToken)).Append(';')
          .Append("var ACCOUNTS=").Append(accountsJs).Append(';')
          .Append(body)
          .Append("})();");
        return sb.ToString();
    }

    /// <summary>把字符串转成安全的 JS 字面量。</summary>
    private static string JsString(string text)
    {
        if (text == null)
        {
            return "''";
        }
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '<': sb.Append("\\u003c"); break;   // 防止提前结束 script 标签
                case '>': sb.Append("\\u003e"); break;
                case '&': sb.Append("\\u0026"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>从宿主收到的消息里解析出类型与字段。返回 null 表示不是我们的消息。</summary>
    public static (string Type, string Id, string Username, string Password, string Token)? ParseMessage(string json)
    {
        if (string.IsNullOrEmpty(json) || !json.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json[MessagePrefix.Length..]);
            var root = doc.RootElement;
            string Get(string name) =>
                root.TryGetProperty(name, out System.Text.Json.JsonElement e) &&
                e.ValueKind == System.Text.Json.JsonValueKind.String
                    ? e.GetString()
                    : "";

            return (Get("type"), Get("id"), Get("username"), Get("password"), Get("token"));
        }
        catch (Exception ex)
        {
            Log.Warn("解析页面消息失败: " + ex.Message);
            return null;
        }
    }
}
