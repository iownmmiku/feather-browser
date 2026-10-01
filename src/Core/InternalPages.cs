using System.Text;
using FeatherBrowser.Services;

namespace FeatherBrowser.Core;

/// <summary>
/// 内置管理页（书签 / 下载 / 历史）的页面定义与落盘。
///
/// <para><b>为什么不用 feather:// 自定义协议。</b>
/// 一开始的写法是 web 资源拦截（<c>WebResourceRequested</c>）+ <c>feather://</c> 地址，
/// 实测两条路都不通：
/// <list type="bullet">
///   <item><c>AddWebResourceRequestedFilter("feather://*")</c> 不报错也不生效，
///         导航直接以 <c>ConnectionAborted</c> 失败；</item>
///   <item>改成注册 <c>*</c> / All 之后处理器**依然一次都没被调用** ——
///         因为 WebView2 对自定义协议根本不走这个事件。</item>
/// </list>
/// 结果是页面全空白，而且日志里看不出原因。</para>
///
/// <para><b>现在的做法</b>：把页面生成为磁盘上的 HTML，再用
/// <c>SetVirtualHostNameToFolderMapping</c> 把 <c>feather.local</c> 映射到那个目录。
/// 于是地址是普通的 <c>https://feather.local/bookmarks.html</c>，走标准 HTTP 加载路径，
/// 行为确定、地址栏可见、来源独立。数据通过 <c>postMessage</c> 往返，页面不自己持久化。</para>
/// </summary>
internal static class InternalPages
{
    /// <summary>虚拟主机名（注册为安全的自定义 scheme，见 TabManager）。</summary>
    public const string Host = "feather.local";

    public const string BookmarksFile = "bookmarks.html";

    public const string DownloadsFile = "downloads.html";

    public const string HistoryFile = "history.html";

    public const string BookmarksUrl = "https://" + Host + "/" + BookmarksFile;

    public const string DownloadsUrl = "https://" + Host + "/" + DownloadsFile;

    public const string HistoryUrl = "https://" + Host + "/" + HistoryFile;

    /// <summary>该地址是否是内置管理页。</summary>
    public static bool Handles(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == Host && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath is "/bookmarks.html" or "/downloads.html" or "/history.html";

    /// <summary>按地址取页面标题。</summary>
    public static string TitleFor(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "轻羽浏览器";
        }
        if (url.EndsWith(BookmarksFile, StringComparison.OrdinalIgnoreCase))
        {
            return "书签管理";
        }
        if (url.EndsWith(DownloadsFile, StringComparison.OrdinalIgnoreCase))
        {
            return "下载内容";
        }
        if (url.EndsWith(HistoryFile, StringComparison.OrdinalIgnoreCase))
        {
            return "历史记录";
        }
        return "轻羽浏览器";
    }

    /// <summary>
    /// 把三个页面写到磁盘。每次启动都重写，保证页面随程序版本更新。
    /// </summary>
    public static void Materialize()
    {
        try
        {
            string folder = Folder;
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, BookmarksFile), BuildBookmarksPage());
            File.WriteAllText(Path.Combine(folder, DownloadsFile), BuildDownloadsPage());
            File.WriteAllText(Path.Combine(folder, HistoryFile), BuildHistoryPage());
            Log.Info($"内置管理页已生成: {folder}");
        }
        catch (Exception ex)
        {
            Log.Warn("生成内置管理页失败: " + ex.Message);
        }
    }

    /// <summary>内置页面所在目录。</summary>
    public static string Folder => Path.Combine(AppPaths.Root, "pages");

    // ---------------------------------------------------------------- 页面骨架

    /// <summary>公共外壳：样式、顶栏、与宿主通信的桥。</summary>
    private static string Shell(string title, string subtitle, string toolbarHtml,
        string bodyHtml, string script)
    {
        var sb = new StringBuilder(8192);
        sb.Append("""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>
""").Append(title).Append("""
</title>
<style>
:root {
  --bg: #16171a; --surface: #1e2024; --surface-hover: #26282d;
  --text: #e8eaed; --text-dim: #9aa0a6; --border: #2f3237;
  --accent: #5a8cff; --accent-soft: rgba(90,140,255,0.16);
  --danger: #e5534b;
}
@media (prefers-color-scheme: light) {
  :root {
    --bg: #f6f7f9; --surface: #ffffff; --surface-hover: #eef0f3;
    --text: #1f2023; --text-dim: #6b7280; --border: #e2e5ea;
    --accent: #3a64e0; --accent-soft: rgba(58,100,224,0.12);
    --danger: #d1372e;
  }
}
* { box-sizing: border-box; }
body {
  margin: 0; background: var(--bg); color: var(--text);
  font: 14px/1.5 "Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif;
}
header {
  position: sticky; top: 0; z-index: 5;
  display: flex; align-items: center; gap: 12px;
  padding: 16px 24px; background: var(--bg);
  border-bottom: 1px solid var(--border);
}
h1 { margin: 0; font-size: 19px; font-weight: 600; }
.sub { color: var(--text-dim); font-size: 13px; margin-right: auto; }
button, input, select {
  font: inherit; color: var(--text); background: var(--surface);
  border: 1px solid var(--border); border-radius: 8px;
  padding: 7px 12px; outline: none;
}
button { cursor: pointer; }
button:hover { background: var(--surface-hover); }
button.primary { background: var(--accent); border-color: var(--accent); color: #fff; }
button.primary:hover { filter: brightness(1.08); }
button.danger:hover { color: var(--danger); border-color: var(--danger); }
input { min-width: 200px; }
input:focus { border-color: var(--accent); }
main { padding: 8px 24px 48px; }
.row {
  display: flex; align-items: center; gap: 14px;
  padding: 11px 12px; border-radius: 10px;
}
.row:hover { background: var(--surface-hover); }
.row .grow { flex: 1; min-width: 0; }
.row .title {
  font-size: 14px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.row .url {
  color: var(--text-dim); font-size: 12px;
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.row .when { color: var(--text-dim); font-size: 12px; white-space: nowrap; }
.row .act { opacity: 0; display: flex; gap: 6px; }
.row:hover .act { opacity: 1; }
.row .act button { padding: 3px 9px; font-size: 12px; border-radius: 6px; }
.empty { color: var(--text-dim); text-align: center; padding: 64px 0; }
.day { color: var(--text-dim); font-size: 12px; font-weight: 600;
       padding: 18px 12px 6px; }
.bar { height: 5px; border-radius: 3px; background: var(--border); overflow: hidden;
       margin-top: 6px; }
.bar > i { display: block; height: 100%; background: var(--accent); width: 0; }
</style>
</head>
<body>
<header>
  <h1>
""").Append(title).Append("""
</h1>
  <span class="sub" id="sub">
""").Append(subtitle).Append("""
</span>
""").Append(toolbarHtml).Append("""
</header>
<main id="main">
""").Append(bodyHtml).Append("""
</main>
<script>
// 与宿主通信。
// 坑：window.chrome.webview 这个桥不一定在脚本解析时就存在。
// 早期版本在脚本顶层直接 postMessage，桥还没注入就被 if 静默跳过，
// 表现为数据永远不来（页面一直停在「正在载入…」），而且日志里什么都没有。
// 所以这里改成：可发送就发，发不出去就重试若干次，最后仍失败就明确报错。
function send(kind, payload) {
  const bridge = window.chrome && window.chrome.webview;
  if (!bridge) return false;
  try {
    // 一定要传**字符串**，不能传对象。
    // 传对象时宿主用 TryGetWebMessageAsString() 取会抛
    // ArgumentException("Value does not fall within the expected range.")，
    // 而且该异常容易被外层空 catch 吞掉，表现成「消息根本没发出来」，极难排查。
    bridge.postMessage(JSON.stringify(Object.assign({ feather: kind }, payload || {})));
    return true;
  } catch (e) {
    return false;
  }
}
function sendWhenReady(kind, payload) {
  let tries = 0;
  (function attempt() {
    if (send(kind, payload)) return;
    if (++tries > 25) {
      document.body.insertAdjacentHTML('afterbegin',
        '<div style="padding:12px 24px;color:#e5534b">'
        + '无法与浏览器通信，数据载入失败。</div>');
      return;
    }
    setTimeout(attempt, 200);
  })();
}
window.featherUpdate = function () { };
""").Append(script).Append("""
</script>
</body>
</html>
""");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 书签页

    private static string BuildBookmarksPage() => Shell(
        "书签管理", "从夸克迁移过来的收藏也在这里",
        """
  <input id="q" placeholder="搜索书签" autocomplete="off">
  <button class="danger" id="clear">清空全部</button>
""",
        """
<div id="list"><div class="empty">正在载入…</div></div>
""",
        """
let items = [];
let query = '';

function escapeHtml(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g,
    c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

function render() {
  const list = document.getElementById('list');
  const q = query.trim().toLowerCase();
  const filtered = q
    ? items.filter(i => (i.title || '').toLowerCase().includes(q)
                     || (i.url || '').toLowerCase().includes(q))
    : items;
  document.getElementById('sub').textContent =
    items.length + ' 个书签' + (q ? '（匹配 ' + filtered.length + ' 个）' : '');

  if (!filtered.length) {
    list.innerHTML = '<div class="empty">'
      + (items.length ? '没有匹配的书签' : '还没有书签，点地址栏右侧的 ☆ 添加') + '</div>';
    return;
  }

  list.innerHTML = filtered.map(b => `
    <div class="row">
      <div class="grow">
        <div class="title">${escapeHtml(b.title || b.url)}</div>
        <div class="url">${escapeHtml(b.url)}</div>
      </div>
      <div class="act">
        <button data-open="${b.index}">打开</button>
        <button data-copy="${b.index}">复制</button>
        <button class="danger" data-del="${b.index}">删除</button>
      </div>
    </div>`).join('');
}

document.getElementById('list').addEventListener('click', e => {
  const t = e.target;
  if (t.dataset.open !== undefined) send('bookmarks-open', { index: +t.dataset.open });
  if (t.dataset.copy !== undefined) send('bookmarks-copy', { index: +t.dataset.copy });
  if (t.dataset.del !== undefined) send('bookmarks-delete', { index: +t.dataset.del });
});

document.getElementById('q').addEventListener('input', e => {
  query = e.target.value; render();
});

document.getElementById('clear').addEventListener('click', () => {
  if (confirm('确定清空全部书签？此操作不可撤销。')) send('bookmarks-clear');
});

window.featherUpdate = function (payload) { items = payload.items || []; render(); };
sendWhenReady('bookmarks-load');
""");

    // ---------------------------------------------------------------- 下载页

    private static string BuildDownloadsPage() => Shell(
        "下载内容", "正在下载与已完成",
        """
  <button id="folder">打开下载文件夹</button>
  <button class="danger" id="clear">清除已完成</button>
""",
        """
<div id="list"><div class="empty">正在载入…</div></div>
""",
        """
let items = [];

function escapeHtml(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g,
    c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}
function size(n) {
  if (!n && n !== 0) return '';
  const u = ['B','KB','MB','GB']; let i = 0; let v = n;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return (i === 0 ? v : v.toFixed(1)) + ' ' + u[i];
}

function render() {
  const list = document.getElementById('list');
  document.getElementById('sub').textContent = items.length + ' 项';
  if (!items.length) {
    list.innerHTML = '<div class="empty">还没有下载记录</div>';
    return;
  }
  list.innerHTML = items.map((d, i) => {
    const running = d.state === 'running';
    const failed = d.state === 'failed';
    const status = running ? '正在下载 ' + (d.received ? size(d.received) : '')
      : failed ? ('失败' + (d.error ? '：' + escapeHtml(d.error) : '')) : size(d.total);
    return `
    <div class="row">
      <div class="grow">
        <div class="title">${escapeHtml(d.fileName)}</div>
        <div class="url">${escapeHtml(d.url)}</div>
        ${running ? '<div class="bar"><i style="width:' + (d.percent || 0) + '%"></i></div>' : ''}
        <div class="url">${status}${d.path ? ' · ' + escapeHtml(d.path) : ''}</div>
      </div>
      <div class="act">
        ${d.path ? `<button data-open="${i}">打开</button>
                    <button data-reveal="${i}">所在位置</button>` : ''}
        <button class="danger" data-del="${i}">移除</button>
      </div>
    </div>`;
  }).join('');
}

document.getElementById('list').addEventListener('click', e => {
  const t = e.target;
  if (t.dataset.open !== undefined) send('downloads-open', { index: +t.dataset.open });
  if (t.dataset.reveal !== undefined) send('downloads-reveal', { index: +t.dataset.reveal });
  if (t.dataset.del !== undefined) send('downloads-remove', { index: +t.dataset.del });
});
document.getElementById('clear').addEventListener('click', () => send('downloads-clear'));
document.getElementById('folder').addEventListener('click', () => send('downloads-folder'));

window.featherUpdate = function (payload) { items = payload.items || []; render(); };
sendWhenReady('downloads-load');
""");

    // ---------------------------------------------------------------- 历史页

    private static string BuildHistoryPage() => Shell(
        "历史记录", "最近访问的页面",
        """
  <input id="q" placeholder="搜索历史" autocomplete="off">
  <select id="range">
    <option value="0">全部时间</option>
    <option value="1">今天</option>
    <option value="7">最近 7 天</option>
    <option value="30">最近 30 天</option>
  </select>
  <button class="danger" id="clear">清空历史</button>
""",
        """
<div id="list"><div class="empty">正在载入…</div></div>
""",
        """
let items = [];
let query = '';
let days = 0;

function escapeHtml(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g,
    c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}
function host(u) { try { return new URL(u).host; } catch (e) { return ''; } }
function dayLabel(ts) {
  const d = new Date(ts * 1000);
  const today = new Date(); today.setHours(0,0,0,0);
  const that = new Date(d); that.setHours(0,0,0,0);
  const diff = Math.round((today - that) / 86400000);
  if (diff <= 0) return '今天';
  if (diff === 1) return '昨天';
  return d.getFullYear() + ' 年 ' + (d.getMonth() + 1) + ' 月 ' + d.getDate() + ' 日';
}
function timeLabel(ts) {
  const d = new Date(ts * 1000);
  return String(d.getHours()).padStart(2,'0') + ':' + String(d.getMinutes()).padStart(2,'0');
}

function render() {
  const list = document.getElementById('list');
  const q = query.trim().toLowerCase();
  let filtered = items;
  if (q) filtered = filtered.filter(i =>
    (i.title || '').toLowerCase().includes(q) || (i.url || '').toLowerCase().includes(q));
  if (days > 0) {
    const cutoff = Math.floor(Date.now() / 1000) - days * 86400;
    filtered = filtered.filter(i => i.when >= cutoff);
  }
  document.getElementById('sub').textContent =
    '共 ' + filtered.length + ' 条' + (filtered.length !== items.length ? '（总计 ' + items.length + '）' : '');

  if (!filtered.length) {
    list.innerHTML = '<div class="empty">没有匹配的历史记录</div>';
    return;
  }

  let html = '';
  let lastDay = '';
  filtered.forEach(i => {
    const day = dayLabel(i.when);
    if (day !== lastDay) { html += '<div class="day">' + day + '</div>'; lastDay = day; }
    html += `
    <div class="row">
      <span class="when">${timeLabel(i.when)}</span>
      <div class="grow">
        <div class="title">${escapeHtml(i.title || host(i.url))}</div>
        <div class="url">${escapeHtml(i.url)}</div>
      </div>
      <div class="act">
        <button data-open="${i.index}">打开</button>
        <button class="danger" data-del="${i.index}">删除</button>
      </div>
    </div>`;
  });
  list.innerHTML = html;
}

document.getElementById('list').addEventListener('click', e => {
  const t = e.target;
  if (t.dataset.open !== undefined) send('history-open', { index: +t.dataset.open });
  if (t.dataset.del !== undefined) send('history-delete', { index: +t.dataset.del });
});
document.getElementById('q').addEventListener('input', e => { query = e.target.value; render(); });
document.getElementById('range').addEventListener('change', e => {
  days = +e.target.value; render();
});
document.getElementById('clear').addEventListener('click', () => {
  if (confirm('确定清空全部历史记录？')) send('history-clear');
});

window.featherUpdate = function (payload) { items = payload.items || []; render(); };
sendWhenReady('history-load');
""");
}
