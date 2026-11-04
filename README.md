# 画廊下载助手

<img src="src/EhGalleryDownloader/Assets/AppIcon.png" alt="画廊下载助手图标" width="96" height="96">

[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-2563EB)](https://github.com/jx645879099-hub/EhGalleryDownloader/releases/latest)
[![Release](https://img.shields.io/github/v/release/jx645879099-hub/EhGalleryDownloader)](https://github.com/jx645879099-hub/EhGalleryDownloader/releases/latest)
[![Build](https://github.com/jx645879099-hub/EhGalleryDownloader/actions/workflows/build.yml/badge.svg)](https://github.com/jx645879099-hub/EhGalleryDownloader/actions/workflows/build.yml)

面向 Windows 的中文 E-Hentai / ExHentai 下载工具，以 `gallery-dl` 为下载内核，提供任务队列、断点续传、Clash 节点实测和图形化操作界面。

当前版本为 **1.1.8**，源码与 Windows 完整包同步发布；程序左下角显示实际运行版本。

配套浏览器扩展当前为 **1.2.2**，可以独立更新；更新文件后需在浏览器扩展管理页刷新已加载的扩展，并确认画廊域名的 HTTP / HTTPS 站点权限。

## 下载

**[下载最新版 Windows 64 位完整包](https://github.com/jx645879099-hub/EhGalleryDownloader/releases/latest)**

完整包已包含 .NET 运行环境、下载内核和浏览器登录导入组件。下载 ZIP 后解压，双击 `EhGalleryDownloader.exe` 即可使用。

桌面快捷方式：右键 `EhGalleryDownloader.exe` → “显示更多选项” → “发送到” → “桌面快捷方式”。程序已内置图标。

## 主要功能

- 一次粘贴一个或多个画廊链接，按队列顺序下载。
- 实时显示当前文件、页数、速度、剩余时间和失败数量。
- 暂停后定位本地第一个缺失页，优先使用已下载图片的令牌快速续传；令牌失效或不可用时退回较慢的兼容方式。
- 下载中心优先显示待处理任务，直接显示继续按钮和失败说明；完整记录仍保留在“下载记录”。
- 支持原图、网站压缩图、画廊信息文件和 CBZ 打包。
- 自动识别 Clash / Mihomo `mixed-port`，测速前主动刷新节点延迟。
- 节点结果按完整传输成功率、断线次数、实际速度和延迟排序。
- 临时网络故障可按测速排名自动换节点继续。
- 同时保留手动 Cookie 和“从当前网页导入”两种登录方式。
- 下载任务和进度自动保存，关闭软件后可以继续。
- 适配 Windows 高 DPI，可选择 100%、110% 或 125% 界面大小。

## 快速开始

1. 解压完整包并运行 `EhGalleryDownloader.exe`。
2. 保持 Clash Verge / Mihomo 正常运行。
3. 在浏览器中登录 E-Hentai 或 ExHentai。
4. 在软件“设置 → ExHentai 登录”中手动填写 Cookie，或按提示加载随软件附带的浏览器组件并从当前网页导入。
5. 粘贴画廊链接，选择保存位置，点击“开始下载”。

更完整的下载、登录、续传和节点测试说明见 [使用指南](docs/USAGE.md)。Cookie 权限和本机数据处理方式见 [隐私与数据说明](docs/PRIVACY.md)。

## 仓库结构

```text
src/EhGalleryDownloader/                 WPF 主程序与浏览器组件
tests/EhGalleryDownloader.IntegrationProbe/  集成测试
docs/                                    使用和隐私说明
.github/workflows/                       GitHub 自动构建检查
CHANGELOG.md                             版本更新记录
```

## 从源码构建

需要 Windows 和 .NET 8 SDK：

```powershell
dotnet build .\EhGalleryDownloader.sln -c Release
dotnet run --project .\tests\EhGalleryDownloader.IntegrationProbe\EhGalleryDownloader.IntegrationProbe.csproj -c Release -- --unit-only

node --input-type=module -e "import('./tests/browser-extension/popup.test.mjs').then(async module => console.log((await module.runExtensionTests()).join('\n')))"
dotnet publish .\src\EhGalleryDownloader\EhGalleryDownloader.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\dist
```

仓库不会提交编译产物、Cookie、本地设置、运行日志、验收下载内容或第三方二进制。源码构建后可以在软件内安装下载内核，也可以自行把 `gallery-dl.exe` 放入根目录的 `tools` 文件夹后重新发布。

版本变化见 [CHANGELOG.md](CHANGELOG.md)，第三方许可见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。

图标的可编辑矢量源文件为 [design/app-icon.xaml](design/app-icon.xaml)。在 Windows 上运行 `powershell -NoProfile -STA -File .\scripts\Build-Icons.ps1` 可重新生成程序和扩展使用的全部尺寸。
