# CAU Grade Monitor Desktop

Windows GUI integrating the CAU grade monitor with an EasierConnect-compatible SOCKS5 VPN client.

## Features

- Start and stop the VPN and grade monitor from one window.
- Reconnect the VPN on a schedule, after repeated tunnel `EOF` failures, or when the process/port disappears.
- Preserve the managed Edge profile and login state between restarts.
- Store the VPN password, Feishu webhook, and signing secret with Windows DPAPI for the current user.
- Display current grade count, required and sports-course weighted GPA, service state, and recent logs.
- Select the GPA scope and browse all courses from the latest successful grade query.
- Minimize to the Windows notification area for long-running operation.

## Run

Open `CauGradeMonitor.exe`, configure the VPN account and Feishu bot on the Settings page, then select `启动全部`.

Runtime data is stored under:

```text
%LOCALAPPDATA%\CauGradeMonitor
```

Encrypted secrets can only be decrypted by the same Windows user on the same machine. When moving the package to another server, enter the secrets again.

## Build

Requirements: .NET 8 SDK or later, Node.js 18 or later, and `npm`.

```powershell
pwsh -File .\build-desktop.ps1 `
  -EasierConnectExe C:\path\to\EasierConnect.exe `
  -NodeDirectory C:\path\to\node-folder
```

The public repository does not include EasierConnect binaries or private configuration. Review the upstream project's terms before redistributing a compatible VPN binary.
