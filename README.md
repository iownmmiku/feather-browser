# 轻羽浏览器 · Feather Browser

面向 Windows 10 / 11 x64 的浏览器，使用 .NET 8 WinForms 与 CEF Chromium。通过限制驻留标签数量、销毁闲置视图和冻结后台页面控制内存。

**1.5.0 已从 WebView2 迁移到 CEF。** 内核随安装包提供，主进程、渲染、GPU 与网络子进程均由 `FeatherBrowser.exe` 承载。仍保留 Chromium 多进程架构，任务管理器中的具体分组方式取决于 Windows。

## 下载与升级

到 [GitHub Releases](https://github.com/iownmmiku/feather-browser/releases/latest) 下载 `FeatherBrowser-1.5.0-setup.exe`。

安装包包括 .NET、Chromium 内核和经微软签名验证的 VC++ x64 运行库，无需另装 WebView2。VC++ 依赖缺失时离线安装。升级前关闭浏览器，再运行安装程序。安装目录默认为 `%ProgramFiles%\Feather Browser`。

1.5.0 保留已有设置、书签、历史、下载记录和轻羽保存的密码。CEF 网页缓存位于 `%LOCALAPPDATA%\FeatherBrowser\Chromium`，与旧版 WebView2 数据分开；旧缓存保留，网站 Cookie 与登录状态不迁移，需要重新登录。内核打包后安装包和磁盘占用增加，内存仍由实际页面和驻留上限决定。

卸载默认保留个人数据，只删除安装负载文件与空目录；用户可选择同时删除 `%LOCALAPPDATA%\FeatherBrowser` 中的个人数据。

## 本轮修复

- 修正 WinForms Dock 顺序，网页区域完整位于工具栏、标签栏、查找条和状态栏之间，缩放与调整窗口时保持正确布局。
- 标签侧边栏与网页并排显示，滚动后命中区域立即更新，支持键盘选择、关闭标签和打开内存面板。
- 菜单动作在主窗口上执行，修复关闭菜单后点击无响应；长菜单适应屏幕高度，可滚动访问底部选项。
- 替换内核并重新接入导航、下载、内部管理页、拦截、密码助手、深色主题和网页缩放。
- 冷标签的快速切换先合并创建请求；视图初始化和销毁期间保留原生窗口及关闭处理器，避免崩溃或误关主窗口。
- 同一数据目录再次启动时，通过当前用户专用管道在已有进程打开新窗口，共用 Cookie 与数据。

验证与技术说明见 [1.5.0 修复记录](docs/fixes-1.5.0.md) 和 [回归报告](docs/regression-1.5.0.txt)。旧版截图仅供界面风格参考：[浅色](docs/screenshot-light.png)、[深色](docs/screenshot-dark.png)。

## 功能

- 智能地址栏、前进后退、刷新与停止、首页、多窗口、全屏、网页缩放。
- 顶部标签栏支持切换、关闭、拖动排序与横向滚动；右侧标签列表显示状态和内存。
- 书签、历史和下载管理页支持搜索、打开、复制与删除；下载支持保存位置选择、进度和完成记录。
- Chromium 原生页内查找，在网站 JavaScript 关闭时也可使用。
- `window.open` 与新窗口链接在当前浏览器打开标签。
- 浅色、深色和跟随系统主题；界面倍率 100%、115%、130%、150%。
- 图片、JavaScript、磁盘缓存、第三方 Cookie 的设置；域名级广告及追踪拦截。
- 会话恢复、已关闭标签恢复、夸克数据导入、本机加密密码库与表单助手。

## 内存策略

| 标签状态 | 行为 | 切回时 |
|---|---|---|
| Live 渲染中 | 保留 CEF 控件和页面 | 立即显示 |
| Suspended 挂起 | 用 Chromium 生命周期接口冻结后台页面 | 恢复页面状态 |
| Cold 休眠 | 销毁 CEF 控件，只保留标题与 URL 等信息 | 重新加载 |

默认同时驻留 2 个标签，上限可设为 1～8。当前标签优先，剩余名额给最近访问过的标签；从未加载的后台标签不占预算。超出上限的标签销毁视图。关闭驻留页面能够释放页面资源，CEF 的 GPU、网络等共享进程仍可能保留。

菜单中的「立即回收标签内存」销毁非当前视图，并回收外壳工作集。普通切换不强制执行 GC，减少停顿。状态栏与内存面板按当前宿主的进程树统计 `FeatherBrowser.exe` 子进程，排除其他应用与无关辅助进程。

普通窗口共用一个 CEF 请求上下文和数据存储；无痕窗口使用独立的内存 Cookie 与缓存，不写浏览历史和下载记录，关闭后释放上下文。实际下载的文件会保留。

## 数据与密码

数据目录为 `%LOCALAPPDATA%\FeatherBrowser`：

| 文件或目录 | 用途 |
|---|---|
| `settings.json` | 设置和普通窗口会话 |
| `bookmarks.json` | 书签 |
| `history.json` | 最近的浏览历史 |
| `downloads.json` | 下载记录 |
| `passwords.json` | DPAPI 加密的密码库 |
| `Chromium\Default` | 普通浏览 Cookie、缓存等 |
| `pages` | 本地管理页 HTML |
| `user_blocklist.txt` | 自定义域名规则 |

自定义拦截规则一行一个域名，`!` 表示白名单，也支持 hosts 与 `||example.com^` 格式。

密码使用当前 Windows 用户的 DPAPI 加密，无法直接移到另一个账户或电脑。页面加载时只提供用户名列表，用户选择后才填入对应密码；来源按协议、主机和端口匹配，账号选择使用当前文档随机令牌验证。填入输入框后，当前页面脚本能够读取内容。无痕窗口不使用轻羽密码助手。

菜单中的「从夸克导入」可导入书签、历史和兼容的保存密码；读取原始文件的副本，不更改来源数据。Cookie 不导入。新版 Chromium 的应用绑定加密或不同 Windows 账户可能导致部分密码无法解密。

```powershell
FeatherBrowser.exe '--import-quark=all;2000' --import-report=report.txt
FeatherBrowser.exe --import-quark=bookmarks
FeatherBrowser.exe '--import-quark=history;500'
FeatherBrowser.exe --import-quark=passwords
```

## 快捷键

| 快捷键 | 功能 | 快捷键 | 功能 |
|---|---|---|---|
| Ctrl+T | 新建标签 | Ctrl+W | 关闭标签 |
| Ctrl+N | 新窗口 | Ctrl+Shift+N | 无痕窗口 |
| Ctrl+Shift+T | 恢复关闭标签 | Ctrl+Shift+E | 标签侧边栏 |
| Ctrl+Shift+O | 书签管理 | Ctrl+J | 下载管理 |
| Ctrl+H | 历史 | Ctrl+Tab | 下一个标签 |
| Ctrl+L | 地址栏 | Ctrl+D | 收藏页面 |
| Ctrl+F | 页内查找 | F5 / Ctrl+R | 刷新 |
| Alt+← / Alt+→ | 后退 / 前进 | Ctrl++ / Ctrl+- | 网页缩放 |
| Ctrl+= / Ctrl+0 | 界面放大 / 复位 | F11 | 全屏 |
| Esc | 关闭菜单、侧边栏、查找或停止加载 | | |

## 构建与验证

需要 .NET 8 SDK 或兼容的更新 SDK、Windows x64；构建安装程序需要 NSIS 3。开发运行依赖 VC++ x64 运行库，发布安装包会附带它。

```powershell
dotnet build .\src\FeatherBrowser.csproj -c Release
dotnet run --project .\tests\FeatherBrowser.Tests.csproj -c Release
.\tools\build-installer.ps1 -MakensisPath C:\Tools\nsis\makensis.exe
```

构建脚本默认读取项目版本号，以 `win-x64 --self-contained true` 发布完整依赖，验证 VC++ 安装程序签名后生成 `dist\FeatherBrowser-1.5.0-setup.exe`。`-SkipInstaller` 只生成发布目录。使用 SelfHost 承载 CEF 子进程，发布目录不包含默认的 `CefSharp.BrowserSubprocess.exe`。

回归测试使用隔离的临时数据目录和本机 HTTP 服务器，覆盖真实 Chromium 导航、Cookie、脚本策略、消息来源校验、密码助手、下载、标签生命周期、布局与菜单。测试不会使用真实用户配置或保存密码。

```powershell
FeatherBrowser.exe --data-dir=C:\Temp\FeatherProbe --theme=dark
FeatherBrowser.exe --data-dir=C:\Temp\FeatherProbe --uitest=menu
FeatherBrowser.exe --selftest report.txt
```

CEF / CefSharp 固定为 `152.0.60`，Chromium 的安全更新需要随应用发布新版本。WebView2 时代的内存报告不能用作当前版本基准。第三方许可随安装包放在 `ThirdPartyNotices` 目录。项目代码使用 MIT 许可。