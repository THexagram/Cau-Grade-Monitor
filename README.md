# CAU Grade Feishu Monitor

中国农业大学新教务成绩监控工具。程序在 Windows 服务器上打开独立 Edge 浏览器，定时向教务成绩接口查询成绩；发现新增成绩或成绩变化后，通过飞书自定义机器人通知。

## 功能

- 每 60 秒查询一次成绩接口。
- 手动登录后复用浏览器登录态，不需要提取 Cookie。
- 查询时优先向服务器请求 `cjcx_list`，避免反复读取静态旧页面。
- 新成绩通知包含课程名、成绩、学分和当前必修课加权绩点。
- 启动时发送当前成绩数量和必修课总绩点。
- 支持 Windows 上的 NJUConnect / EasierConnect SOCKS5 代理。
- 提供一键启动脚本：先启动 VPN，再启动成绩监控。
- 默认低资源模式，适合较小的 Windows ECS。

## 不要提交的文件

仓库已经用 `.gitignore` 排除了这些运行时/敏感文件：

- `.env`
- `vpn.env`
- `config.json`
- `state.json`
- `browser-profile/`
- `node_modules/`
- `logs/`
- `EasierConnect.exe`

请不要把飞书 webhook、飞书 secret、VPN 账号密码上传到 GitHub。

## 安装

需要：

- Windows Server / Windows 11
- Microsoft Edge
- Node.js 18 或更高版本

安装依赖：

```powershell
npm install
```

第一次运行会自动从示例文件创建 `.env` 和 `config.json`。你也可以手动复制：

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

测试飞书：

```powershell
powershell -ExecutionPolicy Bypass -File .\test-feishu.ps1
```

## 普通启动

```powershell
powershell -ExecutionPolicy Bypass -File .\start.ps1
```

程序会打开一个独立的 Edge。请在这个 Edge 中登录综合服务平台，并进入“课程成绩查询”页面。后续程序会使用同一个浏览器会话查询成绩接口。

## 一键启动 VPN 和监控

如果你使用重编译的 NJUConnect / EasierConnect，请把 `EasierConnect.exe` 放在以下任一位置：

- 本目录
- `.\NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe`
- 与本目录同级的 `NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe`

第一次运行前：

```powershell
Copy-Item .\vpn.env.example .\vpn.env
notepad .\vpn.env
```

至少填写：

```env
CAU_VPN_USERNAME=你的账号
CAU_VPN_PASSWORD=你的密码
```

然后双击：

```text
一键启动VPN和查询.bat
```

脚本会：

1. 启动 `EasierConnect.exe`
2. 等待 `127.0.0.1:1080` SOCKS5 可用
3. 启动成绩监控程序

如果你的 VPN 程序放在其他路径，在 `vpn.env` 中设置：

```env
CAU_VPN_EXE=C:\path\to\EasierConnect.exe
```

如果 VPN 需要短信验证码，或你想看 VPN 输出：

```env
CAU_VPN_SHOW_WINDOW=true
```

## 配置重点

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

成绩查询本身每 60 秒访问一次服务器，因此程序不再额外做 VPN keepalive。

## 绩点规则

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

启动后首次查到成绩时，飞书通知类似：

```text
当前共有23科成绩，绩点为：3.72
```

发现新成绩时，飞书通知类似：

```text
检测到新成绩或成绩变化
课程名课成绩：A
学分：3
当前总绩点为：3.72
```

## 误报容忍

如果已经有历史成绩基线，偶发一次请求超时不会立刻报警。默认连续 3 次空结果才发登录/页面异常通知：

```json
{
  "emptyResultNotifyAfter": 3,
  "emptyResultRetrySeconds": 60,
  "requestTimeoutMs": 30000
}
```

## 开机自启

如果只启动监控：

```powershell
powershell -ExecutionPolicy Bypass -File .\create-scheduled-task.ps1
```

如果需要 VPN 和监控一起启动，建议把 Windows 计划任务指向：

```text
一键启动VPN和查询.bat
```

## 说明

本仓库不包含 EasyConnect / EasierConnect 二进制文件。请从你自己构建或信任的来源获取，并遵守对应项目许可证和学校网络使用规定。
