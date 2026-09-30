using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>书签条目。</summary>
public sealed class BookmarkEntry
{
    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public long CreatedAt { get; set; }

    /// <summary>界面展示用。只读属性不写进 JSON —— 否则每条书签都要多存一份重复的标题。</summary>
    [JsonIgnore]
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? FallbackTitle(Url) : Title;

    internal static string FallbackTitle(string url)
    {
        string host = UrlUtils.HostOf(url);
        return string.IsNullOrEmpty(host) ? url : host;
    }
}

/// <summary>
/// 书签管理。JSON 落盘，全局只有一份内存副本。
///
/// <p>写盘分两种模式：
/// <list type="bullet">
///   <item>平时（用户点一次收藏）：后台异步写，不阻塞界面；</item>
///   <item>批量导入：必须先进 <see cref="BeginBatch"/>，最后 <see cref="Flush"/> 一次。
///         否则每加一条就写一次盘，几百条会互相抢文件锁，写盘全部失败。</item>
/// </list>
/// 后台写盘串行化在一条任务链上，避免并发写同一个文件。
/// </summary>
public sealed class BookmarkStore
{
    private readonly List<BookmarkEntry> _items = new();
    private readonly object _gate = new();
    private Task _writeChain = Task.CompletedTask;
    private bool _batchMode;

    public BookmarkStore()
    {
        Load();
    }

    public IReadOnlyList<BookmarkEntry> Items => _items;

    public int Count => _items.Count;

    public BookmarkEntry this[int index] => _items[index];

    public bool Contains(string url) =>
        _items.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));

    /// <summary>切换书签状态，返回操作后是否已收藏。</summary>
    public bool Toggle(string url, string title)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        int index = _items.FindIndex(b =>
            string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _items.RemoveAt(index);
            QueueSave();
            return false;
        }

        _items.Insert(0, new BookmarkEntry
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) ? BookmarkEntry.FallbackTitle(url) : title,
            CreatedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
        });
        QueueSave();
        return true;
    }

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < _items.Count)
        {
            _items.RemoveAt(index);
            QueueSave();
        }
    }

    /// <summary>
    /// 导入用：按 URL 去重后追加。
    /// 与 <see cref="Toggle"/> 的区别是它**不会**把已存在的书签删掉 ——
    /// 导入时碰到重复项应该跳过，而不是把用户原有的收藏清掉。
    /// </summary>
    /// <returns>true 表示新增；false 表示已存在被跳过。</returns>
    public bool AddImported(string url, string title)
    {
        if (string.IsNullOrEmpty(url) || Contains(url))
        {
            return false;
        }

        _items.Add(new BookmarkEntry
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) ? BookmarkEntry.FallbackTitle(url) : title,
            CreatedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
        });

        if (_batchMode)
        {
            // 批量导入期间只改内存，由调用方在结束时 Flush 一次
        }
        else
        {
            QueueSave();
        }
        return true;
    }

    /// <summary>
    /// 进入批量模式：期间只改内存，不写盘。
    /// 批量导入前必须调用，结束时调用 <see cref="Flush"/>。
    /// </summary>
    public void BeginBatch()
    {
        _batchMode = true;
    }

    /// <summary>结束批量模式并把内存内容同步写盘一次。</summary>
    public void Flush()
    {
        _batchMode = false;
        WriteNow();
    }

    // ---------------------------------------------------------------- 落盘

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.BookmarksFile))
            {
                return;
            }
            string json = File.ReadAllText(AppPaths.BookmarksFile);
            var list = JsonSerializer.Deserialize(json, JsonContext.Default.ListBookmarkEntry);
            if (list != null)
            {
                _items.AddRange(list.Where(b => !string.IsNullOrEmpty(b.Url)));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取书签失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 排队一次后台写盘。写操作串成一条链，保证同一时刻只有一个写盘动作，
    /// 避免用户快速连点收藏时多个写任务互相抢文件锁。
    /// </summary>
    private void QueueSave()
    {
        if (_batchMode)
        {
            return;
        }

        List<BookmarkEntry> snapshot;
        lock (_gate)
        {
            snapshot = new List<BookmarkEntry>(_items);
            _writeChain = _writeChain.ContinueWith(_ => WriteFile(snapshot),
                TaskScheduler.Default);
        }
    }

    private void WriteNow()
    {
        List<BookmarkEntry> snapshot;
        lock (_gate)
        {
            snapshot = new List<BookmarkEntry>(_items);
            // 等排在前面的写入结束，避免和新写入撞在一起
            _writeChain = _writeChain.ContinueWith(_ => WriteFile(snapshot),
                TaskScheduler.Default);
            _writeChain.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private static void WriteFile(List<BookmarkEntry> snapshot)
    {
        try
        {
            AppPaths.EnsureCreated();
            string json = JsonSerializer.Serialize(snapshot, JsonContext.Default.ListBookmarkEntry);
            string temp = AppPaths.BookmarksFile + ".tmp";
            string target = AppPaths.BookmarksFile;

            // 先写临时文件再原子替换：中途失败不会毁掉原有书签
            File.WriteAllText(temp, json);
            File.Move(temp, target, true);
        }
        catch (Exception ex)
        {
            Log.Warn("保存书签失败: " + ex.Message);
        }
    }
}
