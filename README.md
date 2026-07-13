# CAU Grade Feishu Monitor

中国农业大学新教务成绩监控工具。程序在 Windows 服务器上启动独立 Edge 浏览器，定时请求教务成绩接口；发现新增成绩或成绩变化后，通过飞书自定义机器人发送通知。

## Features

- 每 60 秒请求一次成绩接口。
- 复用独立 Edge 浏览器登录态，不依赖 Cookie 提取。
- 查询时优先请求 `cjcx_list` 接口，避免重复读取静态旧页面。
- 新成绩通知包含课程名、成绩、学分和当前必修课加权绩点。
- 启动时发送当前成绩数量和必修课总绩点。
- 支持 Windows 上的 NJUConnect / EasierConnect SOCKS5 代理。
- 提供一键启动脚本：先启动 VPN，再启动成绩监控。
- 默认启用低资源模式，适合较小规格的 Windows ECS。

## Security

仓库通过 `.gitignore` 排除运行态和敏感文件：

- `.env`
- `vpn.env`
- `config.json`
- `state.json`
- `browser-profile/`
- `node_modules/`
- `logs/`
- `EasierConnect.exe`

飞书 webhook、飞书 secret、VPN 账号密码等敏感信息应只保存在本地运行环境中。

## Requirements

- Windows Server / Windows 11
- Microsoft Edge
- Node.js 18 或更高版本

## Installation

安装依赖：

```powershell
npm install
```

创建本地配置文件：

```powershell
Copy-Item .\.env.example .\.env
Copy-Item .\config.njuconnect.example.json .\config.json
```

编辑 `.env`：

```env
FEISHU_WEBHOOK_URL=https://open.feishu.cn/open-apis/bot/v2/hook/...
FEISHU_BOT_SECRET=
MONITOR_PROXY_SERVER=
```

测试飞书机器人：

```powershell
powershell -ExecutionPolicy Bypass -File .\test-feishu.ps1
```

## Basic Usage

启动监控：

```powershell
powershell -ExecutionPolicy Bypass -File .\start.ps1
```

程序会打开一个独立 Edge 窗口。需要在该窗口中完成综合服务平台登录，并进入“课程成绩查询”页面。后续查询会复用该浏览器会话。

## VPN Integration

使用重编译的 NJUConnect / EasierConnect 时，`EasierConnect.exe` 可放置在以下任一位置：

- 仓库根目录
- `.\NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe`
- 与仓库同级的 `NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe`

创建 VPN 配置：

```powershell
Copy-Item .\vpn.env.example .\vpn.env
notepad .\vpn.env
```

至少配置：

```env
CAU_VPN_USERNAME=<vpn-username>
CAU_VPN_PASSWORD=<vpn-password>
```

一键启动 VPN 和成绩监控：

```text
一键启动VPN和查询.bat
```

脚本流程：

1. 启动 `EasierConnect.exe`
2. 等待 `127.0.0.1:1080` SOCKS5 可用
3. 启动成绩监控程序

自定义 VPN 程序路径：

```env
CAU_VPN_EXE=C:\path\to\EasierConnect.exe
```

需要短信验证码、TOTP 或查看 VPN 输出时：

```env
CAU_VPN_SHOW_WINDOW=true
```

## Configuration

`config.njuconnect.example.json` 默认只让教务域名走 SOCKS5：

```json
{
  "proxy": {
    "enabled": true,
    "server": "socks5://127.0.0.1:1080",
    "browser": false
  },
  "browser": {
    "loadExtensionDir": "edge-cau-proxy-extension",
    "args": [
      "--host-resolver-rules=MAP newjw.cau.edu.cn 10.200.36.235,EXCLUDE localhost"
    ]
  }
}
```

成绩查询本身每 60 秒访问一次服务器，因此程序不再额外执行 VPN keepalive。

## GPA Calculation

只统计类型/属性包含“必修”的课程，按学分加权平均：

| 成绩 | 绩点 |
| --- | --- |
| A+ | 4.0 |
| A | 4.0 |
| A- | 3.7 |
| B+ | 3.3 |
| B | 3.0 |
| B- | 2.7 |
| C+ | 2.3 |
| C | 2.0 |
| D+ | 1.5 |
| D | 1.0 |
| F | 0 |

启动后首次查到成绩时，飞书通知示例：

```text
当前共有23科成绩，绩点为：3.72
```

发现新成绩时，飞书通知示例：

```text
检测到新成绩或成绩变化
课程名课成绩：A
学分：3
当前总绩点为：3.72
```

## Empty Result Tolerance

已有历史成绩基线时，偶发请求超时不会立刻报警。默认连续 3 次空结果才发送登录/页面异常通知：

```json
{
  "emptyResultNotifyAfter": 3,
  "emptyResultRetrySeconds": 60,
  "requestTimeoutMs": 30000
}
```

## Scheduled Start

仅启动成绩监控：

```powershell
powershell -ExecutionPolicy Bypass -File .\create-scheduled-task.ps1
```

VPN 和成绩监控一起启动时，可将 Windows 计划任务指向：

```text
一键启动VPN和查询.bat
```

## Notes

本仓库不包含 EasyConnect / EasierConnect 二进制文件。相关二进制文件应从可信来源获取，并遵守对应项目许可证及学校网络使用规定。
