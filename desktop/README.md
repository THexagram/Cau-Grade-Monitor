# CAU Grade Monitor Desktop

Windows GUI integrating the CAU grade monitor with an EasierConnect-compatible SOCKS5 VPN client.

## Features

- Start and stop the VPN and grade monitor from one window.
- Reconnect the VPN on a schedule, after repeated tunnel `EOF` failures, or when the process/port disappears.
- Preserve the managed Edge profile and login state between restarts.
- Store the VPN password, Feishu webhook, and signing secret with Windows DPAPI for the current user.
- Display current grade count, required and sports-course weighted GPA, service state, and recent logs.
- Select course types as the default GPA rule, then include or exclude individual courses as exceptions.
- Recalculate GPA immediately after changing course selections without restarting the monitor, VPN, or Edge.
- Minimize to the Windows notification area for long-running operation.
- Record low-frequency process and system-memory telemetry with bounded retention.

## Run

Open `CauGradeMonitor.exe`, configure the VPN account and Feishu bot on the Settings page, then select `启动全部`.

Runtime data is stored under:

```text
%LOCALAPPDATA%\CauGradeMonitor
```

Encrypted secrets can only be decrypted by the same Windows user on the same machine. When moving the package to another server, enter the secrets again.

## Windows crash diagnostics

If the ECS reports an operating-system crash or unexpected reboot, run this from an elevated PowerShell 7 prompt after the server is back online:

```powershell
pwsh -ExecutionPolicy Bypass -File .\collect-crash-diagnostics.ps1
```

To enable kernel dumps and a system-managed pagefile before the next incident:

```powershell
pwsh -ExecutionPolicy Bypass -File .\enable-kernel-dump.ps1 -ConfigureSystemManagedPagefile
```

The collector excludes dump contents, credentials, Feishu secrets, browser data, configuration, grades, and application-log contents. Do not publish `C:\Windows\MEMORY.DMP`; analyze or transfer it privately if the BugCheck event does not identify the faulting driver.

## Build

Requirements: .NET 8 SDK or later, Node.js 18 or later, and `npm`.

```powershell
pwsh -File .\build-desktop.ps1 `
  -EasierConnectExe C:\path\to\EasierConnect.exe `
  -NodeDirectory C:\path\to\node-folder
```

For a public package that must not auto-discover or include an EasierConnect binary:

```powershell
pwsh -File .\build-desktop.ps1 -ExcludeEasierConnect
```

The public repository does not include EasierConnect binaries or private configuration. Review the upstream project's terms before redistributing a compatible VPN binary.
