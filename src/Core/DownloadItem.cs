using System.Text.Json.Serialization;

namespace FeatherBrowser.Core;

/// <summary>下载项的状态。</summary>
public enum DownloadState
{
    /// <summary>正在下载。</summary>
    Running,

    /// <summary>已完成。</summary>
    Done,

    /// <summary>失败或被取消。</summary>
    Failed,
}

/// <summary>
/// 一条下载记录。
///
/// <para>只保存展示需要的信息。文件本身由内核按浏览器的默认行为写到磁盘，
/// 这里不接管字节流 —— 接管会让大文件多一次拷贝，也更容易出错。</para>
/// </summary>
public sealed class DownloadItem
{
    public string Url { get; set; } = "";

    public string FileName { get; set; } = "";

    /// <summary>保存到磁盘的完整路径（完成后才有）。</summary>
    public string Path { get; set; } = "";

    public string State { get; set; } = "running";

    /// <summary>已下载字节数。</summary>
    public long ReceivedBytes { get; set; }

    /// <summary>总字节数，未知时为 0。</summary>
    public long TotalBytes { get; set; }

    /// <summary>开始时间（Unix 秒）。</summary>
    public long StartedAt { get; set; }

    /// <summary>失败原因（失败时才有）。</summary>
    public string Error { get; set; } = "";

    /// <summary>进度百分比，总长度未知时返回 0。</summary>
    [JsonIgnore]
    public int Percent => TotalBytes > 0
        ? (int)Math.Clamp(ReceivedBytes * 100 / TotalBytes, 0, 100)
        : 0;

    [JsonIgnore]
    public bool IsRunning => State == "running";

    [JsonIgnore]
    public string SizeText
    {
        get
        {
            long value = IsRunning ? ReceivedBytes : TotalBytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = value;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return (unit == 0 ? size.ToString("0") : size.ToString("0.0")) + " " + units[unit];
        }
    }
}
