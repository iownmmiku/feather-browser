using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherBrowser.Core;

namespace FeatherBrowser.Services;

/// <summary>
/// 设置项。用 System.Text.Json 的源生成器序列化：
/// 反射式序列化会在启动时构建大量元数据，源生成器既省内存也更快。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(BookmarkEntry))]
[JsonSerializable(typeof(List<BookmarkEntry>))]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSerializable(typeof(List<HistoryEntry>))]
internal partial class JsonContext : JsonSerializerContext
{
}
