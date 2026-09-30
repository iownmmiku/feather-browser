using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>历史记录条目。</summary>
public sealed class HistoryEntry
{
    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public long VisitedAt { get; set; }

    /// <summary>界面展示用。只读属性不写进 JSON。</summary>
    [JsonIgnore]
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? BookmarkEntry.FallbackTitle(Url) : Title;

    [JsonIgnore]
    public string TimeText =>
        DateTimeOffset.FromUnixTimeSeconds(VisitedAt).LocalDateTime.ToString("MM-dd HH:mm");
}

/// <summary>
/// 历史记录。只在内存里保留最近 <see cref="MemoryLimit"/> 条，更早的会被裁掉，
/// 这样即使长期使用，内存占用也是常数级。
///
/// <p>写盘同样分「平时异步」与「批量导入后同步落盘」两种模式，
/// 理由见 <see cref="BookmarkStore"/> 的注释。
/// </summary>
public sealed class HistoryStore
{
    /// <summary>内存里保留的最大条数。</summary>
    public const int MemoryLimit = 800;

    private readonly LinkedList<HistoryEntry> _items = new();
    private readonly object _gate = new();
    private DateTime _lastSave = DateTime.MinValue;
    private bool _batchMode;

    public HistoryStore()
    {
        Load();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>按时间倒序快照，供列表显示。</summary>
    public List<HistoryEntry> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
        SaveNow();
    }

    /// <summary>记录一次访问（当前时间）。相同 URL 只保留最新的一条。</summary>
    public void Record(string url, string title)
    {
        RecordImported(url, title, DateTimeOffset.Now.ToUnixTimeSeconds());
    }

    /// <summary>
    /// 导入用：带原始访问时间写入。
    /// 与 <see cref="Record"/> 的区别是它按时间倒序插入，不会打乱导入后的顺序。
    /// </summary>
    /// <returns>true 表示写入；false 表示被过滤或已存在更新的记录。</returns>
    public bool RecordImported(string url, string title, long visitedAt)
    {
        if (string.IsNullOrEmpty(url) ||
            UrlUtils.IsInternal(url) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool added = false;
        lock (_gate)
        {
            var node = _items.First;
            while (node != null)
            {
                if (string.Equals(node.Value.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    // 已经存在：只有新的时间更晚才需要替换
                    if (node.Value.VisitedAt >= visitedAt)
                    {
                        return false;
                    }
                    _items.Remove(node);
                    break;
                }
                node = node.Next;
            }

            var entry = new HistoryEntry
            {
                Url = url,
                Title = title ?? "",
                VisitedAt = visitedAt,
            };

            // 按时间倒序插入，保证导入后列表顺序正确
            var cursor = _items.First;
            while (cursor != null && cursor.Value.VisitedAt > entry.VisitedAt)
            {
                cursor = cursor.Next;
            }
            if (cursor == null)
            {
                _items.AddLast(entry);
            }
            else
            {
                _items.AddBefore(cursor, entry);
            }

            while (_items.Count > MemoryLimit)
            {
                _items.RemoveLast();
            }
            added = true;
        }

        // 批量导入时不在这里写盘，导入结束由 Flush 统一处理
        if (!_batchMode && (DateTime.UtcNow - _lastSave).TotalSeconds > 20)
        {
            SaveNow();
        }
        return added;
    }

    /// <summary>进入批量模式：期间只改内存，不写盘。</summary>
    public void BeginBatch()
    {
        _batchMode = true;
    }

    /// <summary>结束批量模式并同步写盘。</summary>
    public void Flush()
    {
        _batchMode = false;
        SaveNow();
    }

    public void SaveNow()
    {
        List<HistoryEntry> snapshot;
        lock (_gate)
        {
            snapshot = _items.ToList();
        }
        _lastSave = DateTime.UtcNow;

        try
        {
            AppPaths.EnsureCreated();
            string json = JsonSerializer.Serialize(snapshot, JsonContext.Default.ListHistoryEntry);
            string temp = AppPaths.HistoryFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.HistoryFile, true);
        }
        catch (Exception ex)
        {
            Log.Warn("保存历史失败: " + ex.Message);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.HistoryFile))
            {
                return;
            }
            string json = File.ReadAllText(AppPaths.HistoryFile);
            var list = JsonSerializer.Deserialize(json, JsonContext.Default.ListHistoryEntry);
            if (list == null)
            {
                return;
            }
            foreach (var entry in list.Take(MemoryLimit))
            {
                if (!string.IsNullOrEmpty(entry.Url))
                {
                    _items.AddLast(entry);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取历史失败: " + ex.Message);
        }
    }
}
