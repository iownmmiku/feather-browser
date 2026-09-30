using System.Text.Json;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>书签条目。</summary>
public sealed class BookmarkEntry
{
    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public long CreatedAt { get; set; }

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? FallbackTitle(Url) : Title;

    internal static string FallbackTitle(string url)
    {
        string host = UrlUtils.HostOf(url);
        return string.IsNullOrEmpty(host) ? url : host;
    }
}

/// <summary>
/// 书签管理。JSON 落盘，全局只有一份内存副本；
/// 页面要显示时用虚拟模式（VirtualMode）按需取行，不会一次性构造大量列表项。
/// </summary>
public sealed class BookmarkStore
{
    private readonly List<BookmarkEntry> _items = new();

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
            SaveAsync();
            return false;
        }

        _items.Insert(0, new BookmarkEntry
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) ? BookmarkEntry.FallbackTitle(url) : title,
            CreatedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
        });
        SaveAsync();
        return true;
    }

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < _items.Count)
        {
            _items.RemoveAt(index);
            SaveAsync();
        }
    }

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

    /// <summary>后台写盘，不阻塞界面。</summary>
    private void SaveAsync()
    {
        List<BookmarkEntry> snapshot = new(_items);
        Task.Run(() =>
        {
            try
            {
                AppPaths.EnsureCreated();
                string json = JsonSerializer.Serialize(snapshot, JsonContext.Default.ListBookmarkEntry);
                File.WriteAllText(AppPaths.BookmarksFile, json);
            }
            catch (Exception ex)
            {
                Log.Warn("保存书签失败: " + ex.Message);
            }
        });
    }
}
