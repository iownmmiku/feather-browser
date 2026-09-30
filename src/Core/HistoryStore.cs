using System.Text.Json;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>历史记录条目。</summary>
public sealed class HistoryEntry
{
    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public long VisitedAt { get; set; }

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? BookmarkEntry.FallbackTitle(Url) : Title;

    public string TimeText =>
        DateTimeOffset.FromUnixTimeSeconds(VisitedAt).LocalDateTime.ToString("MM-dd HH:mm");
}

/// <summary>
/// 历史记录。只在内存里保留最近 N 条（默认 800），更早的会被裁掉，
/// 这样即使长期使用，内存占用也是常数级。
/// </summary>
public sealed class HistoryStore
{
    private const int MemoryLimit = 800;

    private readonly LinkedList<HistoryEntry> _items = new();
    private readonly object _gate = new();
    private DateTime _lastSave = DateTime.MinValue;

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

    /// <summary>按时间倒序快照，供列表显示（虚拟模式按需取用）。</summary>
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

    /// <summary>记录一次访问。相同 URL 只保留最新的一条。</summary>
    public void Record(string url, string title)
    {
        if (string.IsNullOrEmpty(url) ||
            UrlUtils.IsInternal(url) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_gate)
        {
            var node = _items.First;
            while (node != null)
            {
                if (string.Equals(node.Value.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    _items.Remove(node);
                    break;
                }
                node = node.Next;
            }

            _items.AddFirst(new HistoryEntry
            {
                Url = url,
                Title = title ?? "",
                VisitedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
            });

            while (_items.Count > MemoryLimit)
            {
                _items.RemoveLast();
            }
        }

        // 最多每 20 秒落一次盘，避免频繁 IO
        if ((DateTime.UtcNow - _lastSave).TotalSeconds > 20)
        {
            SaveNow();
        }
    }

    public void SaveNow()
    {
        List<HistoryEntry> snapshot;
        lock (_gate)
        {
            snapshot = _items.ToList();
        }
        _lastSave = DateTime.UtcNow;

        Task.Run(() =>
        {
            try
            {
                AppPaths.EnsureCreated();
                string json = JsonSerializer.Serialize(snapshot, JsonContext.Default.ListHistoryEntry);
                File.WriteAllText(AppPaths.HistoryFile, json);
            }
            catch (Exception ex)
            {
                Log.Warn("保存历史失败: " + ex.Message);
            }
        });
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
