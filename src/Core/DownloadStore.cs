using System.Text.Json;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>
/// 下载记录。落盘为 <c>downloads.json</c>，界面是内置的 <c>feather://downloads</c> 页。
///
/// <para>只记不做：文件由内核按浏览器默认方式保存，这里记录状态供界面展示。
/// 记录数量有上限，否则长期使用会积累成几千条没人看的记录。</para>
/// </summary>
public sealed class DownloadStore
{
    private const int MaxRecords = 200;

    private readonly object _gate = new();
    private readonly List<DownloadItem> _items = new();
    private readonly string _file;

    public DownloadStore(bool persist = true)
    {
        if (persist)
        {
            _file = Path.Combine(AppPaths.Root, "downloads.json");
            Load();
        }
    }

    /// <summary>默认下载目录（跟随系统设置，通常就是「下载」文件夹）。</summary>
    public string Folder
    {
        get
        {
            try
            {
                // 优先用系统登记的下载目录
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string raw &&
                    !string.IsNullOrWhiteSpace(raw))
                {
                    return Environment.ExpandEnvironmentVariables(raw);
                }
            }
            catch
            {
                // 读不到就用兜底路径
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// <summary>最新在前。</summary>
    public List<DownloadItem> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToList();
        }
    }

    public DownloadItem Get(int index)
    {
        lock (_gate)
        {
            return index >= 0 && index < _items.Count ? _items[index] : null;
        }
    }

    /// <summary>新建一条下载记录，返回它的引用供后续更新进度。</summary>
    public DownloadItem Begin(string url, string fileName)
    {
        var item = new DownloadItem
        {
            Url = url ?? "",
            FileName = string.IsNullOrWhiteSpace(fileName) ? "未命名文件" : fileName,
            State = "running",
            StartedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
        };

        lock (_gate)
        {
            _items.Insert(0, item);
            TrimLocked();
        }
        Save();
        return item;
    }

    /// <summary>更新进度（内核会高频回调，所以要轻）。</summary>
    public void UpdateProgress(DownloadItem item, long received, long total)
    {
        if (item == null)
        {
            return;
        }
        item.ReceivedBytes = received;
        item.TotalBytes = total;
    }

    /// <summary>标记完成。</summary>
    public void Complete(DownloadItem item, string path, long totalBytes)
    {
        if (item == null)
        {
            return;
        }
        item.State = "done";
        item.Path = path ?? "";
        item.ReceivedBytes = totalBytes > 0 ? totalBytes : item.ReceivedBytes;
        if (totalBytes > 0)
        {
            item.TotalBytes = totalBytes;
        }
        if (string.IsNullOrWhiteSpace(item.FileName) && !string.IsNullOrEmpty(path))
        {
            item.FileName = Path.GetFileName(path);
        }
        Save();
    }

    /// <summary>标记失败。</summary>
    public void Fail(DownloadItem item, string reason)
    {
        if (item == null)
        {
            return;
        }
        item.State = "failed";
        item.Error = reason ?? "";
        Save();
    }

    /// <summary>按界面下标删除一条记录（不删磁盘上的文件）。</summary>
    public bool RemoveAt(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _items.Count)
            {
                return false;
            }
            _items.RemoveAt(index);
        }
        Save();
        return true;
    }

    /// <summary>清除已完成与失败的记录，正在下载的保留。</summary>
    public void ClearFinished()
    {
        lock (_gate)
        {
            _items.RemoveAll(i => !i.IsRunning);
        }
        Save();
    }

    private void TrimLocked()
    {
        while (_items.Count > MaxRecords)
        {
            // 从尾部删，但不要删掉正在下载的
            int index = _items.FindLastIndex(i => !i.IsRunning);
            if (index < 0)
            {
                return;
            }
            _items.RemoveAt(index);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }
            var loaded = JsonSerializer.Deserialize<List<DownloadItem>>(
                File.ReadAllText(_file));
            if (loaded == null)
            {
                return;
            }
            lock (_gate)
            {
                _items.Clear();
                // 上次退出时还在「下载中」的，进程已经没了，标记成失败更诚实
                foreach (DownloadItem item in loaded)
                {
                    if (item.IsRunning)
                    {
                        item.State = "failed";
                        item.Error = "程序退出前未完成";
                    }
                    _items.Add(item);
                }
                TrimLocked();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取下载记录失败: " + ex.Message);
        }
    }

    public void Save()
    {
        if (_file == null) return;
        try
        {
            List<DownloadItem> copy;
            lock (_gate)
            {
                copy = _items.ToList();
            }
            AppPaths.EnsureCreated();
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy,
                new JsonSerializerOptions { WriteIndented = false }));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("保存下载记录失败: " + ex.Message);
        }
    }
}
