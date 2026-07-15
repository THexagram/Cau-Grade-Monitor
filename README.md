# CAU Grade Monitor

[![CI](https://github.com/THexagram/Cau-Grade-Monitor/actions/workflows/ci.yml/badge.svg)](https://github.com/THexagram/Cau-Grade-Monitor/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/THexagram/Cau-Grade-Monitor)](https://github.com/THexagram/Cau-Grade-Monitor/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4)](https://github.com/THexagram/Cau-Grade-Monitor)

中国农业大学教务成绩监控工具。它在 Windows 上复用独立 Edge 登录状态，定时请求成绩接口，并在出现新成绩或成绩变化时通过飞书机器人通知。桌面端同时管理 EasierConnect 兼容 VPN、成绩监控、绩点规则和运行日志。

## 功能

- 原生 Windows GUI，一处管理 VPN、成绩监控、飞书通知和日志。
- 复用独立 Edge 用户目录，无需从浏览器数据库提取 Cookie。
- 默认每 60 秒请求一次成绩接口，避免反复读取静态旧页面。
- 新成绩通知包含课程名、成绩、学分和当前总绩点。
- 支持按课程类型设置总体绩点规则，并对单门课程设置加入或排除例外。
- 使用 Windows DPAPI 为当前用户加密保存 VPN 密码、飞书 Webhook 和签名密钥。
- 支持 NJUConnect / EasierConnect 提供的本地 SOCKS5 代理。
- 支持定时重建 VPN 会话、连续隧道 `EOF` 恢复和托盘运行。
- 低资源浏览器模式适用于小规格 Windows ECS。

## 下载

从 [Releases](https://github.com/THexagram/Cau-Grade-Monitor/releases/latest) 按使用方式选择：

| 文件 | 适用场景 | 启动方式 |
| --- | --- | --- |
| `CauGradeMonitor-gui-win-x64.zip` | 推荐，大多数 Windows 用户 | 运行 `CauGradeMonitor.exe` |
| `CauGradeMonitor-cli-portable-win-x64.zip` | 只使用命令行，不想安装 Node.js | 运行 `pwsh .\start-full.ps1` |
| `CauGradeMonitor-cli-node-required.zip` | 已安装 Node.js，希望下载最小包 | 先运行 `pwsh .\install.ps1` |

所有公开包都不会包含：

- EasierConnect 或其他第三方 VPN 二进制文件
- VPN 账号密码
- 飞书 Webhook 或签名密钥
- Edge 登录资料和成绩缓存

GUI 版首次启动时在“设置”中选择本机的 `EasierConnect.exe`，填写 VPN 与飞书配置，再点击“启动全部”。命令行版按包内示例创建 `.env`、`config.json` 和可选的 `vpn.env`；需要同时管理 VPN 时运行 `pwsh .\start-vpn-and-monitor.ps1`。如果当前网络可以直接访问教务系统，可以只启动成绩监控。

运行数据保存在：

```text
%LOCALAPPDATA%\CauGradeMonitor
```

加密后的敏感配置只能由同一台 Windows 设备上的同一用户解密。将程序迁移到其他服务器后需要重新填写。

## 使用流程

1. 启动桌面程序并填写配置。
2. 启动 VPN，等待 SOCKS5 状态就绪。
3. 启动成绩监控，在程序打开的独立 Edge 中完成统一身份认证。
4. 从综合服务平台进入“课程成绩查询”页面。
5. 保持桌面程序在运行或最小化到托盘。

监控会优先请求 `cjcx_list` 接口获取最新数据。已有历史成绩时，单次请求超时或空结果不会立刻报警；默认连续 3 次失败后才提示登录或页面异常。

## 绩点规则

首次使用默认统计类型或属性中包含“必修”（不含“非必修”）或“体育类”的课程。桌面端提供两层规则：

- 课程类型多选框决定总体计入范围。
- “全部成绩”表格中的复选框用于单独加入或排除某一门课程。

单门课程重新切换到总体规则对应的状态时，该课程例外会自动取消。所有规则会保存在当前用户配置中，并在下次启动监控时用于飞书通知和总绩点计算。

完成选择后点击“全部成绩”页面的“刷新绩点”，界面会立即重算 GPA、计入课程数和总学分。运行中的监控会热加载新规则，后续查询与飞书通知无需重启即可生效。

| 成绩 | 绩点 | 成绩 | 绩点 |
| --- | ---: | --- | ---: |
| A+ | 4.0 | B | 3.0 |
| A | 4.0 | B- | 2.7 |
| A- | 3.7 | C+ | 2.3 |
| B+ | 3.3 | C | 2.0 |
| D+ | 1.5 | D | 1.0 |
| F | 0 | | |

总绩点按有效课程学分加权平均。

## 飞书通知

启动后首次查询成功：

```text
当前共有23科成绩，绩点为：3.72
```

发现新成绩或成绩变化：

```text
检测到新成绩或成绩变化
课程名课成绩：A
学分：3
当前总绩点为：3.72
```

## VPN 集成

程序使用 EasierConnect 兼容客户端提供的 `127.0.0.1:1080` SOCKS5 代理。默认行为包括：

- 每 120 分钟主动建立新 VPN 会话。
- VPN 进程退出或 SOCKS5 端口消失时自动恢复。
- 隧道日志连续出现 4 次 `EOF` 时提前重连。
- VPN 重连期间保留成绩监控和 Edge 登录窗口。
- 不额外发送网页保活请求。

需要短信验证码、TOTP 或交互式认证的 VPN 无法完全无人值守重连。

## Windows 崩溃诊断

桌面端每 10 分钟向 `%LOCALAPPDATA%\CauGradeMonitor\logs\resources-YYYYMMDD.csv` 写入一行轻量资源数据，并在 VPN 重连前后额外采样。记录包括 GUI、EasierConnect、Node、Edge 的内存、句柄、线程数，以及系统内存负载、可用物理内存和可用提交量；资源数据保留 7 天，应用日志保留 14 天。

如果 Windows ECS 出现蓝屏、内核崩溃或无故重启，先在重启后的 ECS 管理员 PowerShell 7 中运行：

```powershell
pwsh -ExecutionPolicy Bypass -File .\collect-crash-diagnostics.ps1
```

脚本会在桌面生成 `CauGradeMonitor-Diagnostics-时间.zip`，其中包含 BugCheck/Kernel-Power 事件、页面文件、驱动、系统状态、转储文件元数据和资源轨迹。它不会打包转储正文、账号密码、飞书密钥、Cookie、配置文件、成绩或应用日志正文。

为了让下一次崩溃留下可分析的内核转储，可提前运行：

```powershell
pwsh -ExecutionPolicy Bypass -File .\enable-kernel-dump.ps1 -ConfigureSystemManagedPagefile
```

如果脚本修改了页面文件，请安排一次正常重启。完整的 `C:\Windows\MEMORY.DMP` 可能很大且包含敏感内存，不要上传到公开 Issue；诊断 ZIP 默认只记录其文件名、大小和时间。

## 从源码运行

要求：

- Windows 10、Windows 11 或 Windows Server
- Microsoft Edge
- Node.js 18 或更高版本
- 构建桌面端时需要 .NET 8 SDK

安装依赖并创建配置：

```powershell
npm install
Copy-Item .\.env.example .\.env
Copy-Item .\config.njuconnect.example.json .\config.json
```

启动命令行监控：

```powershell
pwsh -ExecutionPolicy Bypass -File .\start.ps1
```

同时启动 VPN 和监控：

```powershell
pwsh -ExecutionPolicy Bypass -File .\start-vpn-and-monitor.ps1
```

也可以双击 `一键启动VPN和查询.bat`。

## 构建桌面端

生成包含指定 EasierConnect 的内部便携包：

```powershell
pwsh -File .\build-desktop.ps1 `
  -EasierConnectExe C:\path\to\EasierConnect.exe
```

生成不包含第三方 VPN 二进制的公共便携包：

```powershell
pwsh -File .\build-desktop.ps1 -ExcludeEasierConnect
```

结果位于 `desktop-publish/CauGradeMonitor-win-x64.zip`。

## 构建命令行包

同时生成需要本机 Node.js 的精简包和内置 Node.js 的 Windows x64 便携包：

```powershell
pwsh -File .\build-cli.ps1
```

结果位于 `cli-publish/`。命令行构建不会包含 EasierConnect、真实配置、浏览器资料或运行状态。

## 测试

```powershell
npm test
npm run check
dotnet build .\desktop\CauGradeMonitor.Desktop\CauGradeMonitor.Desktop.csproj -c Release
```

GitHub Actions 会在每次推送和 Pull Request 时运行 Node.js 测试、语法检查和 Windows 桌面端编译。

## 安全

`.gitignore` 会排除 `.env`、`vpn.env`、`config.json`、浏览器资料、日志、缓存、压缩包和 VPN 二进制。不要在 Issue、日志截图或提交中公开账号密码、飞书 Webhook、签名密钥、Cookie 或完整成绩单。

安全问题请参阅 [SECURITY.md](SECURITY.md)。

## 项目结构

```text
desktop/CauGradeMonitor.Desktop/  Windows GUI
edge-cau-proxy-extension/         Edge 分流扩展
monitor.js                        成绩查询与飞书通知
start-vpn-and-monitor.ps1         VPN 与监控监督脚本
build-desktop.ps1                 便携包构建脚本
```

## 说明

本项目不包含 EasyConnect / EasierConnect 二进制文件。请从可信来源获取兼容客户端，并遵守对应项目许可证、学校网络规定和教务系统使用要求。

当前仓库尚未指定开源许可证。未经授权，不代表可以自由复制、修改或重新分发源码。
