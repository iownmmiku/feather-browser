using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>
/// 一条保存的登录凭据。
///
/// <p><b>密码不以明文存储</b>：<see cref="PasswordProtected"/> 是经 DPAPI 加密后的 Base64，
/// 只有当前 Windows 用户在本机能解开。站点与用户名保持明文 —— 自动填充需要按站点匹配，
/// 而且 Chromium 等主流浏览器也是这么存的。
/// </summary>
public sealed class PasswordEntry
{
    /// <summary>站点来源（完整 origin，含 scheme://host:port）。</summary>
    public string Origin { get; set; } = "";

    /// <summary>用于匹配的可注册域名，例如 example.com。</summary>
    public string Domain { get; set; } = "";

    public string Username { get; set; } = "";

    /// <summary>DPAPI 保护后的密码（Base64）。</summary>
    public string PasswordProtected { get; set; } = "";

    public long SavedAt { get; set; }

    /// <summary>来源标记："quark" 表示从夸克导入，空表示在本程序里保存。</summary>
    public string Source { get; set; } = "";

    /// <summary>界面展示用：站点名。只读属性不写进 JSON。</summary>
    [JsonIgnore]
    public string DisplaySite
    {
        get
        {
            if (!string.IsNullOrEmpty(Domain))
            {
                return Domain;
            }
            string host = UrlUtils.RawHostOf(Origin);
            return string.IsNullOrEmpty(host) ? Origin : host;
        }
    }

    [JsonIgnore]
    public string DisplayUser =>
        string.IsNullOrWhiteSpace(Username) ? "（无用户名）" : Username;
}

/// <summary>密码库文件的顶层结构。单独一层是为了以后加版本迁移。</summary>
public sealed class PasswordFile
{
    public int Version { get; set; } = 1;

    public List<PasswordEntry> Entries { get; set; } = new();
}

/// <summary>
/// 本机密码库。
///
/// <p>设计取向：
/// <list type="bullet">
///   <item>密码逐条用 DPAPI 加密后再写盘，**不写明文文件**；</item>
///   <item>明文只在需要填充或查看的那一瞬间解密到内存，用完不缓存；</item>
///   <item>整个库只有一份内存副本，且只在保存时重建索引。</item>
/// </list>
/// </summary>
public sealed class PasswordStore
{
    private readonly List<PasswordEntry> _entries = new();
    private Dictionary<string, List<PasswordEntry>> _byDomain;

    public PasswordStore()
    {
        Load();
    }

    public int Count => _entries.Count;

    /// <summary>按站点倒序的快照，供管理界面显示。</summary>
    public List<PasswordEntry> Snapshot()
    {
        return _entries
            .OrderBy(e => e.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>某个来源（如 quark）导入了多少条。</summary>
    public int CountBySource(string source) =>
        _entries.Count(e => string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- 读写

    /// <summary>新增或更新一条凭据（同一站点 + 同一用户名视为同一条）。</summary>
    public void Save(string origin, string username, string password, string source = "")
    {
        if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(password))
        {
            return;
        }

        string domain = UrlUtils.RegistrableDomain(origin);
        byte[] protectedBytes = Dpapi.Protect(Encoding.UTF8.GetBytes(password));
        string encoded = Convert.ToBase64String(protectedBytes);

        int index = _entries.FindIndex(e =>
            string.Equals(e.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Username, username ?? "", StringComparison.Ordinal));

        if (index >= 0)
        {
            _entries[index].PasswordProtected = encoded;
            _entries[index].Origin = origin;
            _entries[index].SavedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
        }
        else
        {
            _entries.Add(new PasswordEntry
            {
                Origin = origin,
                Domain = domain,
                Username = username ?? "",
                PasswordProtected = encoded,
                SavedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
                Source = source,
            });
        }

        _byDomain = null;
        SaveToDisk();
    }

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < _entries.Count)
        {
            _entries.RemoveAt(index);
            _byDomain = null;
            SaveToDisk();
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _byDomain = null;
        SaveToDisk();
    }

    /// <summary>解出明文密码。仅用于填充与「显示密码」，调用方不要缓存结果。</summary>
    public string RevealPassword(PasswordEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.PasswordProtected))
        {
            return "";
        }
        try
        {
            byte[] cipher = Convert.FromBase64String(entry.PasswordProtected);
            byte[] plain = Dpapi.Unprotect(cipher);
            if (plain == null)
            {
                return "";
            }
            string text = Encoding.UTF8.GetString(plain);
            Array.Clear(plain, 0, plain.Length);
            return text;
        }
        catch
        {
            return "";
        }
    }

    // ---------------------------------------------------------------- 站点匹配

    /// <summary>
    /// 找出适用于某个网址的凭据。匹配顺序：同域名优先，其次同 origin。
    /// </summary>
    public List<PasswordEntry> FindForUrl(string url)
    {
        var result = new List<PasswordEntry>();
        if (string.IsNullOrEmpty(url))
        {
            return result;
        }

        string host = UrlUtils.RawHostOf(url);
        string domain = UrlUtils.RegistrableDomain(url);
        if (string.IsNullOrEmpty(host))
        {
            return result;
        }

        EnsureIndex();

        // 先精确命中主机（含子域）
        foreach (PasswordEntry entry in _entries)
        {
            if (string.Equals(entry.Domain, domain, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(entry);
            }
        }

        // 子域与主域互相兼容：mail.example.com 也能用 example.com 的凭据
        if (result.Count == 0 && !string.IsNullOrEmpty(domain))
        {
            string parent = UrlUtils.ParentDomain(host);
            if (!string.IsNullOrEmpty(parent))
            {
                foreach (PasswordEntry entry in _entries)
                {
                    if (string.Equals(entry.Domain, parent, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(entry);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>这个站点是否已经存过账号（用于决定要不要提示「保存」）。</summary>
    public bool HasForUrl(string url) => FindForUrl(url).Count > 0;

    private void EnsureIndex()
    {
        if (_byDomain != null)
        {
            return;
        }
        var map = new Dictionary<string, List<PasswordEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (PasswordEntry entry in _entries)
        {
            if (!map.TryGetValue(entry.Domain, out List<PasswordEntry> list))
            {
                list = new List<PasswordEntry>();
                map[entry.Domain] = list;
            }
            list.Add(entry);
        }
        _byDomain = map;
    }

    // ---------------------------------------------------------------- 落盘

    private static string FilePath => AppPaths.PasswordsFile;

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }
            string json = File.ReadAllText(FilePath, Encoding.UTF8);
            var file = JsonSerializer.Deserialize(json, JsonContext.Default.PasswordFile);
            if (file?.Entries != null)
            {
                _entries.AddRange(file.Entries.Where(e => !string.IsNullOrEmpty(e.Domain)));
            }
            Log.Info($"密码库载入 {_entries.Count} 条");
        }
        catch (Exception ex)
        {
            Log.Warn("读取密码库失败: " + ex.Message);
        }
    }

    private void SaveToDisk()
    {
        try
        {
            AppPaths.EnsureCreated();
            var file = new PasswordFile { Version = 1, Entries = _entries };
            string json = JsonSerializer.Serialize(file, JsonContext.Default.PasswordFile);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex)
        {
            Log.Warn("保存密码库失败: " + ex.Message);
        }
    }
}
