using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherBrowser.Core;

namespace FeatherBrowser.Services;

/// <summary>可选搜索引擎。</summary>
public sealed class SearchEngine
{
    public string Name { get; set; } = "Bing";

    /// <summary>查询模板，%s 会被替换为编码后的关键词。</summary>
    public string Template { get; set; } = "https://www.bing.com/search?q=%s";

    public static readonly SearchEngine[] BuiltIn =
    {
        new() { Name = "Bing", Template = "https://www.bing.com/search?q=%s" },
        new() { Name = "百度", Template = "https://www.baidu.com/s?wd=%s" },
        new() { Name = "Google", Template = "https://www.google.com/search?q=%s" },
        new() { Name = "DuckDuckGo", Template = "https://duckduckgo.com/?q=%s" },
        new() { Name = "搜狗", Template = "https://www.sogou.com/web?query=%s" },
        new() { Name = "必应国际", Template = "https://www.bing.com/search?q=%s&ensearch=1" },
    };
}

/// <summary>
/// 用户设置与运行时会话状态。整体作为一个 JSON 文件保存，读写都在后台线程完成。
/// </summary>
public sealed class AppSettings
{
    // ---------------- 行为设置 ----------------

    /// <summary>启动页。为空表示打开内置首页。</summary>
    public string HomePage { get; set; } = "";

    public int SearchEngineIndex { get; set; }

    public bool AdBlockEnabled { get; set; } = true;

    public bool LoadImages { get; set; } = true;

    public bool JavaScriptEnabled { get; set; } = true;

    public bool DesktopUserAgent { get; set; }

    public bool BlockThirdPartyCookies { get; set; } = true;

    /// <summary>
    /// 登录表单自动填充。开启后：站点有已保存账号时，点用户名输入框会弹出账号列表，
    /// 选中后填充；提交登录时若还没保存过，会询问是否保存。
    /// </summary>
    public bool PasswordAutofill { get; set; } = true;

    // ---------------- 界面 ----------------

    /// <summary>
    /// 界面整体倍率。默认 1.15（比「标准」大一档，避免高分屏上看着偏小）。
    /// 可选 1.0 / 1.15 / 1.3 / 1.5。
    /// </summary>
    public float UiScale { get; set; } = 1.15f;

    /// <summary>
    /// 主题：0 = 跟随系统，1 = 浅色，2 = 深色。
    /// 用整数存（而不是枚举）是为了让 JSON 更直白，也避免将来改枚举名导致旧配置失效。
    /// </summary>
    public int ThemeMode { get; set; }

    // ---------------- 内存策略 ----------------

    /// <summary>允许同时驻留（渲染）的标签数上限。超出的标签会被休眠。</summary>
    public int MaxLiveTabs { get; set; } = 2;

    /// <summary>窗口失去焦点 / 最小化时，是否挂起除当前标签外的所有标签。</summary>
    public bool SuspendOnDeactivate { get; set; } = true;

    /// <summary>是否使用系统 Edge 的磁盘缓存（关闭可省磁盘与部分内存）。</summary>
    public bool UseDiskCache { get; set; } = true;

    // ---------------- 窗口几何 ----------------

    /// <summary>窗口逻辑宽度（未乘 DPI 与界面倍率），换显示器时不会过大。</summary>
    public int WindowWidth { get; set; } = 1180;

    public int WindowHeight { get; set; } = 780;

    public bool WindowMaximized { get; set; }

    // ---------------- 运行时会话（随上次退出保存） ----------------

    public List<string> SessionUrls { get; set; } = new();

    public int SessionActiveIndex { get; set; }

    [JsonIgnore]
    public SearchEngine Engine =>
        SearchEngine.BuiltIn[
            SearchEngineIndex >= 0 && SearchEngineIndex < SearchEngine.BuiltIn.Length
                ? SearchEngineIndex
                : 0];

    [JsonIgnore]
    public string HomeUrl => UrlUtils.Normalize(HomePage, Engine.Template);

    // ---------------- 持久化 ----------------

    private static string FilePath => AppPaths.SettingsFile;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize(json, JsonContext.Default.AppSettings);
                if (loaded != null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取设置失败，使用默认值: " + ex.Message);
        }
        return new AppSettings();
    }

    /// <summary>同步保存，调用方通常在退出或设置对话框确认时使用。</summary>
    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            string json = JsonSerializer.Serialize(this, JsonContext.Default.AppSettings);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex)
        {
            Log.Warn("保存设置失败: " + ex.Message);
        }
    }
}

/// <summary>会话状态（标签列表），单独一个类便于源生成。</summary>
public sealed class SessionState
{
    public List<string> Urls { get; set; } = new();

    public int ActiveIndex { get; set; }
}
