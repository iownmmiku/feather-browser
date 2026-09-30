using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatherBrowser.Services;
using Microsoft.Data.Sqlite;

namespace FeatherBrowser.Core;

/// <summary>一次导入的结果统计。</summary>
public sealed class ImportResult
{
    public int BookmarksImported;
    public int BookmarksSkipped;
    public int HistoryImported;
    public int PasswordsImported;
    public int PasswordsFailed;
    public readonly List<string> Notes = new();

    public bool AnySuccess =>
        BookmarksImported > 0 || HistoryImported > 0 || PasswordsImported > 0;

    public string Summary()
    {
        var sb = new StringBuilder();
        if (BookmarksImported > 0 || BookmarksSkipped > 0)
        {
            sb.Append($"书签 {BookmarksImported} 条");
            if (BookmarksSkipped > 0)
            {
                sb.Append($"（跳过重复 {BookmarksSkipped} 条）");
            }
            sb.Append("；");
        }
        if (HistoryImported > 0)
        {
            sb.Append($"历史 {HistoryImported} 条；");
        }
        if (PasswordsImported > 0 || PasswordsFailed > 0)
        {
            sb.Append($"密码 {PasswordsImported} 条");
            if (PasswordsFailed > 0)
            {
                sb.Append($"（{PasswordsFailed} 条解密失败）");
            }
            sb.Append("；");
        }
        return sb.Length == 0 ? "没有导入任何数据" : sb.ToString().TrimEnd('；');
    }
}

/// <summary>
/// 从夸克浏览器（Chromium 内核）迁移数据。
///
/// <p>三块数据的难度完全不同：
/// <list type="bullet">
///   <item><b>书签</b>：直接读 <c>Bookmarks</c> JSON，最简单；</item>
///   <item><b>历史</b>：读 <c>History</c> 这个 SQLite 库的 urls / visits 表；</item>
///   <item><b>密码</b>：需要先用 DPAPI 解开 <c>Local State</c> 里的主密钥，
///         再用 AES-256-GCM 逐条解密 <c>Login Data</c> 里的密文。
///         这个过程只在本程序内部做，明文直接加密进本程序的密码库，不落明文文件。</item>
/// </list>
///
/// <p>所有读取都先复制快照：夸克运行时这些文件是打开的，直接读可能拿到写了一半的内容。
/// </summary>
public sealed class QuarkImporter
{
    private readonly BookmarkStore _bookmarks;
    private readonly HistoryStore _history;
    private readonly PasswordStore _passwords;

    public QuarkImporter(BookmarkStore bookmarks, HistoryStore history, PasswordStore passwords)
    {
        _bookmarks = bookmarks;
        _history = history;
        _passwords = passwords;
    }

    /// <summary>夸克的用户数据目录。</summary>
    public static string QuarkUserDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Quark", "User Data");

    public static string QuarkProfilePath => Path.Combine(QuarkUserDataPath, "Default");

    /// <summary>夸克装没装、有没有数据。</summary>
    public static bool IsAvailable()
    {
        try
        {
            return Directory.Exists(QuarkUserDataPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>夸克此刻是否在运行（运行中也能导入，但我们读的是快照）。</summary>
    public static bool IsQuarkRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("quark").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 快照

    private static string _snapshotDir;

    /// <summary>
    /// 把需要的文件复制到临时目录再读。
    /// 夸克运行时这些文件被占用，而且它随时可能写入，直接读会读到半成品。
    /// </summary>
    private static string PrepareSnapshot()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "feather_quark_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        foreach (string name in new[] { "Bookmarks", "History", "Login Data", "Web Data" })
        {
            string src = Path.Combine(QuarkProfilePath, name);
            if (!File.Exists(src))
            {
                continue;
            }
            try
            {
                // 用 FileShare.ReadWrite 打开，避免被夸克的写锁挡住
                using var input = new FileStream(src, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(Path.Combine(dir, name), FileMode.Create,
                    FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
            catch (Exception ex)
            {
                Log.Warn($"快照 {name} 失败: {ex.Message}");
            }
        }

        // Local State 在上一级目录，密码解密要用
        string localState = Path.Combine(QuarkUserDataPath, "Local State");
        if (File.Exists(localState))
        {
            try
            {
                File.Copy(localState, Path.Combine(dir, "Local State"), true);
            }
            catch (Exception ex)
            {
                Log.Warn("快照 Local State 失败: " + ex.Message);
            }
        }

        // SQLite 的预写日志也要一起带，否则可能丢最近的记录
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            foreach (string name in new[] { "History", "Login Data" })
            {
                string src = Path.Combine(QuarkProfilePath, name + suffix);
                if (File.Exists(src))
                {
                    try
                    {
                        File.Copy(src, Path.Combine(dir, name + suffix), true);
                    }
                    catch
                    {
                        // 没带上也不影响读取已落盘的数据
                    }
                }
            }
        }

        return dir;
    }

    private static void CleanupSnapshot(string dir)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // 临时目录清不掉不影响使用
        }
    }

    /// <summary>执行一次完整导入。</summary>
    public ImportResult Import(bool bookmarks, bool history, bool passwords, int historyLimit = 2000)
    {
        var result = new ImportResult();

        if (!IsAvailable())
        {
            result.Notes.Add("没有找到夸克的数据目录：" + QuarkUserDataPath);
            return result;
        }

        if (IsQuarkRunning())
        {
            result.Notes.Add("导入时夸克正在运行，这里读的是文件快照，数据以磁盘上的为准。");
        }

        _snapshotDir = PrepareSnapshot();
        try
        {
            // 批量模式：导入期间只改内存，最后统一落盘一次。
            // 不这样做的话每加一条就写一次盘，几百条会互相抢文件锁，写盘全部失败。
            _bookmarks.BeginBatch();
            _history.BeginBatch();

            if (bookmarks)
            {
                ImportBookmarks(result);
            }
            if (history)
            {
                ImportHistory(result, historyLimit);
            }
            if (passwords)
            {
                ImportPasswords(result);
            }
        }
        catch (Exception ex)
        {
            Log.Error("导入夸克数据失败", ex);
            result.Notes.Add("导入过程中出错：" + ex.Message);
        }
        finally
        {
            // 无论成功失败都要落盘，不能把已经改好的内存数据丢掉
            try
            {
                _bookmarks.Flush();
                _history.Flush();
            }
            catch (Exception ex)
            {
                Log.Warn("导入后落盘失败: " + ex.Message);
            }

            CleanupSnapshot(_snapshotDir);
            _snapshotDir = null;
        }

        return result;
    }

    // ---------------------------------------------------------------- 书签

    private void ImportBookmarks(ImportResult result)
    {
        string path = Path.Combine(_snapshotDir, "Bookmarks");
        if (!File.Exists(path))
        {
            result.Notes.Add("夸克没有 Bookmarks 文件，跳过书签。");
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (!doc.RootElement.TryGetProperty("roots", out JsonElement roots))
        {
            result.Notes.Add("夸克的 Bookmarks 结构不认识，跳过书签。");
            return;
        }

        // 收集顺序：先书签栏，再其他书签
        foreach (string key in new[] { "bookmark_bar", "other", "synced" })
        {
            if (!roots.TryGetProperty(key, out JsonElement node))
            {
                continue;
            }
            WalkBookmarks(node, "", result);
        }
    }

    /// <summary>递归遍历书签树。目录名拼进标题里，因为我们自己的书签是平铺的。</summary>
    private void WalkBookmarks(JsonElement node, string folderPrefix, ImportResult result)
    {
        if (!node.TryGetProperty("children", out JsonElement children) ||
            children.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement child in children.EnumerateArray())
        {
            string type = child.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
            string name = child.TryGetProperty("name", out JsonElement n) ? n.GetString() : "";

            if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase))
            {
                string prefix = string.IsNullOrEmpty(folderPrefix)
                    ? name
                    : folderPrefix + " / " + name;
                WalkBookmarks(child, prefix, result);
                continue;
            }

            if (!string.Equals(type, "url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string url = child.TryGetProperty("url", out JsonElement u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) ||
                url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string title = string.IsNullOrWhiteSpace(name) ? BookmarkEntry.FallbackTitle(url) : name;
            if (!string.IsNullOrEmpty(folderPrefix))
            {
                title = folderPrefix + " · " + title;
            }

            if (_bookmarks.AddImported(url, title))
            {
                result.BookmarksImported++;
            }
            else
            {
                result.BookmarksSkipped++;
            }
        }
    }

    // ---------------------------------------------------------------- 历史

    private void ImportHistory(ImportResult result, int limit)
    {
        string path = Path.Combine(_snapshotDir, "History");
        if (!File.Exists(path))
        {
            result.Notes.Add("夸克没有 History 文件，跳过历史记录。");
            return;
        }

        var rows = new List<(string Url, string Title, long VisitedAt)>();

        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    // 快照是临时副本，用可读写模式打开最稳：
                    // 只读模式遇到带 WAL 的库时，SQLite 可能因为无法处理日志而打不开。
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                }.ToString());
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            // last_visit_time 是 Chromium 的微秒时间戳（1601-01-01 起算）
            command.CommandText =
                @"SELECT url, COALESCE(title, ''), last_visit_time
                  FROM urls
                  WHERE last_visit_time > 0
                  ORDER BY last_visit_time DESC
                  LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取夸克历史库失败", ex);
            result.Notes.Add("读取夸克历史失败：" + ex.Message);
            return;
        }

        foreach ((string url, string title, long visitTime) in rows)
        {
            if (_history.RecordImported(url, title, FromChromiumTime(visitTime)))
            {
                result.HistoryImported++;
            }
        }

        if (rows.Count == 0)
        {
            result.Notes.Add("夸克的历史库里没有可导入的记录。");
        }
        else if (result.HistoryImported == 0)
        {
            result.Notes.Add($"从夸克读到 {rows.Count} 条历史，但都没有写进去。");
        }
    }

    /// <summary>Chromium 时间戳（1601-01-01 起的微秒数）转 Unix 秒。</summary>
    private static long FromChromiumTime(long chromiumMicros)
    {
        if (chromiumMicros <= 0)
        {
            return DateTimeOffset.Now.ToUnixTimeSeconds();
        }
        // 1601-01-01 到 1970-01-01 相差 11644473600 秒
        long unixSeconds = chromiumMicros / 1_000_000 - 11644473600L;
        return unixSeconds > 0 ? unixSeconds : DateTimeOffset.Now.ToUnixTimeSeconds();
    }

    // ---------------------------------------------------------------- 密码

    private void ImportPasswords(ImportResult result)
    {
        string loginData = Path.Combine(_snapshotDir, "Login Data");
        string localState = Path.Combine(_snapshotDir, "Local State");

        if (!File.Exists(loginData))
        {
            result.Notes.Add("夸克没有 Login Data 文件，跳过密码。");
            return;
        }
        if (!File.Exists(localState))
        {
            result.Notes.Add("找不到 Local State（存解密主密钥的文件），无法解密密码。");
            return;
        }

        byte[] masterKey = ReadMasterKey(localState);
        if (masterKey == null)
        {
            result.Notes.Add("主密钥无法解开。这通常意味着夸克的数据来自另一个 Windows 账户或另一台电脑。");
            return;
        }

        var rows = new List<(string Origin, string Username, byte[] Blob)>();
        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = loginData,
                    // 同 ImportHistory：快照用可读写模式打开，只读模式遇到 WAL 会打不开
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                @"SELECT origin_url, username_value, password_value
                  FROM logins
                  WHERE password_value IS NOT NULL AND length(password_value) > 0";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string origin = reader.IsDBNull(0) ? "" : reader.GetString(0);
                string username = reader.IsDBNull(1) ? "" : reader.GetString(1);
                byte[] blob = reader.IsDBNull(2) ? null : (byte[])reader[2];
                if (!string.IsNullOrEmpty(origin) && blob is { Length: > 0 })
                {
                    rows.Add((origin, username, blob));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取夸克密码库失败", ex);
            result.Notes.Add("读取夸克密码库失败：" + ex.Message);
            return;
        }

        // 用 using 包住，保证密钥用完清零
        try
        {
            foreach ((string origin, string username, byte[] blob) in rows)
            {
                string password = DecryptPassword(blob, masterKey);
                if (string.IsNullOrEmpty(password))
                {
                    result.PasswordsFailed++;
                    continue;
                }
                _passwords.Save(origin, username, password, source: "quark");
                result.PasswordsImported++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }

        if (rows.Count > 0 && result.PasswordsFailed > 0)
        {
            result.Notes.Add($"有 {result.PasswordsFailed} 条密码用了新的加密格式，暂时解不开。");
        }
    }

    /// <summary>
    /// 读出 Chromium 的主密钥：Local State 里的 encrypted_key 是「DPAPI(密钥)」，
    /// Base64 解码后去掉开头的 "DPAPI" 五个字节，再用当前用户的 DPAPI 解开。
    /// </summary>
    private static byte[] ReadMasterKey(string localStatePath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(localStatePath, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("os_crypt", out JsonElement osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out JsonElement keyElement))
            {
                return null;
            }

            string encoded = keyElement.GetString();
            if (string.IsNullOrEmpty(encoded))
            {
                return null;
            }

            byte[] raw = Convert.FromBase64String(encoded);
            if (raw.Length <= 5 || Encoding.ASCII.GetString(raw, 0, 5) != "DPAPI")
            {
                // 不是 DPAPI 前缀说明不是 Windows 的常规格式
                return null;
            }

            byte[] protectedKey = raw[5..];

            // 关键：Chromium 系浏览器保存主密钥用的是「无附加熵」的 DPAPI。
            // 如果传我们自己的熵进去，会得到错误码 13（数据无效）。
            byte[] key = Dpapi.Unprotect(protectedKey, entropy: null);
            if (key == null)
            {
                // 少数环境可能带了熵，退一步再试一次本程序自己的熵
                key = Dpapi.Unprotect(protectedKey);
            }
            if (key == null || key.Length == 0)
            {
                Log.Warn("夸克主密钥解密失败：密文无法用当前 Windows 账户解开");
                return null;
            }
            return key;
        }
        catch (Exception ex)
        {
            Log.Warn("读取夸克主密钥失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 解密一条 Chromium 密码。
    ///
    /// <p>格式：<c>"v10" + 12 字节 nonce + 密文 + 16 字节 GCM 认证标签</c>。
    /// 老版本 Chromium 可能直接是 DPAPI 密文，所以留了回退分支。
    /// </summary>
    private static string DecryptPassword(byte[] blob, byte[] masterKey)
    {
        try
        {
            // 老格式：整块都是 DPAPI 密文
            if (blob.Length > 0 && blob[0] != (byte)'v')
            {
                byte[] legacy = Dpapi.Unprotect(blob);
                return legacy == null ? null : Encoding.UTF8.GetString(legacy);
            }

            if (blob.Length < 3 + 12 + 16)
            {
                return null;
            }

            string prefix = Encoding.ASCII.GetString(blob, 0, 3);
            if (prefix != "v10" && prefix != "v11")
            {
                return null;
            }

            byte[] nonce = blob[3..15];
            int tagLength = 16;
            int cipherLength = blob.Length - 3 - 12 - tagLength;
            if (cipherLength <= 0)
            {
                return null;
            }

            byte[] cipher = blob[15..(15 + cipherLength)];
            byte[] tag = blob[(15 + cipherLength)..];
            byte[] plain = new byte[cipherLength];

            using var aes = new AesGcm(masterKey, tagLength);
            aes.Decrypt(nonce, cipher, tag, plain);

            string text = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return text;
        }
        catch (Exception ex)
        {
            Log.Warn("解密一条夸克密码失败: " + ex.Message);
            return null;
        }
    }
}
