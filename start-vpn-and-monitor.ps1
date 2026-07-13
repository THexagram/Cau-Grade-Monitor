$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Set-Location $PSScriptRoot

function Write-Log($Message) {
    $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Host "[$stamp] $Message"
}

function Import-KeyValueFile($Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Get-Content -LiteralPath $Path -Encoding UTF8 | ForEach-Object {
        $line = $_.Trim()
        if (-not $line -or $line.StartsWith("#")) {
            return
        }

        $index = $line.IndexOf("=")
        if ($index -lt 1) {
            return
        }

        $key = $line.Substring(0, $index).Trim()
        $value = $line.Substring($index + 1).Trim()
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        [Environment]::SetEnvironmentVariable($key, $value, "Process")
    }
}

function Get-EnvValue($Name, $Default = "") {
    $value = [Environment]::GetEnvironmentVariable($Name, "Process")
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $Default
    }
    return $value.Trim()
}

function Get-EnvInt($Name, $Default, $Minimum = 0) {
    $rawValue = Get-EnvValue $Name "$Default"
    $parsedValue = 0
    if (-not [int]::TryParse($rawValue, [ref]$parsedValue) -or $parsedValue -lt $Minimum) {
        throw "$Name must be an integer greater than or equal to $Minimum. Current value: $rawValue"
    }
    return $parsedValue
}

function Read-PasswordPlainText($Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Resolve-ExistingPath($PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return $null
    }
    $expanded = [Environment]::ExpandEnvironmentVariables($PathValue)
    if (-not [System.IO.Path]::IsPathRooted($expanded)) {
        $expanded = Join-Path $PSScriptRoot $expanded
    }
    if (Test-Path -LiteralPath $expanded) {
        return (Resolve-Path -LiteralPath $expanded).Path
    }
    return $null
}

function Find-VpnExe {
    $configured = Resolve-ExistingPath (Get-EnvValue "CAU_VPN_EXE")
    if ($configured) {
        return $configured
    }

    $candidates = @(
        (Join-Path $PSScriptRoot "EasierConnect.exe"),
        (Join-Path $PSScriptRoot "NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe"),
        (Join-Path $PSScriptRoot "..\NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe"),
        (Join-Path $PSScriptRoot "..\..\output\NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe"),
        (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "output\NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Test-TcpPort($HostName, $Port, $TimeoutMs = 1000) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) {
            return $false
        }
        $client.EndConnect($async)
        return $true
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Wait-TcpPort($HostName, $Port, $TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-TcpPort $HostName $Port 1000) {
            return $true
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Wait-TcpPortClosed($HostName, $Port, $TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-TcpPort $HostName $Port 500)) {
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    return -not (Test-TcpPort $HostName $Port 500)
}

function Get-TcpListenerProcess($Port) {
    $connection = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $connection) {
        return $null
    }

    try {
        return Get-Process -Id $connection.OwningProcess -ErrorAction Stop
    } catch {
        return [pscustomobject]@{
            Id = $connection.OwningProcess
            ProcessName = "unknown"
            Path = $null
        }
    }
}

function Split-HostPort($Value) {
    $parts = $Value.Split(":")
    if ($parts.Count -ne 2) {
        throw "Invalid CAU_VPN_SOCKS_BIND value: $Value"
    }
    return @{
        Host = $parts[0]
        Port = [int]$parts[1]
    }
}

function Ensure-MonitorFiles {
    if (-not (Test-Path -LiteralPath ".\config.json")) {
        if (Test-Path -LiteralPath ".\config.njuconnect.example.json") {
            Copy-Item -LiteralPath ".\config.njuconnect.example.json" -Destination ".\config.json" -Force
            Write-Log "Created config.json from config.njuconnect.example.json"
        } elseif (Test-Path -LiteralPath ".\config.example.json") {
            Copy-Item -LiteralPath ".\config.example.json" -Destination ".\config.json" -Force
            Write-Log "Created config.json from config.example.json"
        }
    }

    if (-not (Test-Path -LiteralPath ".\.env") -and (Test-Path -LiteralPath ".\.env.example")) {
        Copy-Item -LiteralPath ".\.env.example" -Destination ".\.env" -Force
        Write-Log "Created .env from .env.example"
    }
}

function Get-NodeExe {
    $bundledNode = Join-Path $PSScriptRoot "node\node.exe"
    if (Test-Path -LiteralPath $bundledNode) {
        return $bundledNode
    }

    $systemNode = Get-Command node -ErrorAction SilentlyContinue
    if ($systemNode) {
        return $systemNode.Source
    }

    throw "Node.js was not found. Use the full ZIP package that contains the node folder, or install Node.js LTS."
}

function Stop-VpnProcess($Process, $SocksHost, $SocksPort) {
    if ($Process -and -not $Process.HasExited) {
        Write-Log "Stopping VPN process PID=$($Process.Id)."
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $Process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }

    if (-not (Wait-TcpPortClosed $SocksHost $SocksPort 10)) {
        throw "SOCKS5 port ${SocksHost}:$SocksPort is still occupied after stopping the VPN process."
    }
}

function Start-ManagedVpn(
    $VpnExe,
    [string[]]$VpnArgs,
    $VpnServer,
    $SocksHost,
    $SocksPort,
    $SocksBind,
    $ShowWindow,
    $StartTimeoutSeconds
) {
    $vpnWorkingDir = Split-Path $VpnExe -Parent
    $stdoutLog = $null
    $stderrLog = $null

    Write-Log "Starting VPN: $vpnServer -> SOCKS5 $SocksBind"
    if ($ShowWindow) {
        $process = Start-Process -FilePath $VpnExe -ArgumentList $VpnArgs -WorkingDirectory $vpnWorkingDir -PassThru
    } else {
        $logDir = Join-Path $PSScriptRoot "logs"
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
        $stdoutLog = Join-Path $logDir "vpn-$stamp.log"
        $stderrLog = Join-Path $logDir "vpn-$stamp.err.log"
        $process = Start-Process -FilePath $VpnExe -ArgumentList $VpnArgs -WorkingDirectory $vpnWorkingDir -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog
        Write-Log "VPN logs: $stdoutLog"
    }

    if (-not (Wait-TcpPort $SocksHost $SocksPort $StartTimeoutSeconds)) {
        $exitedText = if ($process.HasExited) { " VPN process exited with code $($process.ExitCode)." } else { "" }
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        throw "VPN SOCKS5 port $SocksBind did not become available within $StartTimeoutSeconds seconds.$exitedText"
    }

    Write-Log "VPN SOCKS5 is ready on $SocksBind (PID=$($process.Id))."
    return [pscustomobject]@{
        Process = $process
        StartedAt = Get-Date
        StdoutLog = $stdoutLog
        LogPosition = [long]0
        EofFailureCount = 0
    }
}

function Update-VpnLogHealth($VpnRuntime, $EofRestartCount) {
    if ($EofRestartCount -le 0 -or [string]::IsNullOrWhiteSpace($VpnRuntime.StdoutLog) -or -not (Test-Path -LiteralPath $VpnRuntime.StdoutLog)) {
        return $false
    }

    try {
        $stream = [System.IO.File]::Open($VpnRuntime.StdoutLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($stream.Length -lt $VpnRuntime.LogPosition) {
                $VpnRuntime.LogPosition = [long]0
            }
            [void]$stream.Seek($VpnRuntime.LogPosition, [System.IO.SeekOrigin]::Begin)
            $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
            try {
                $newText = $reader.ReadToEnd()
                $VpnRuntime.LogPosition = $stream.Position
            } finally {
                $reader.Dispose()
            }
        } finally {
            $stream.Dispose()
        }
    } catch {
        Write-Log "Could not inspect VPN log health: $($_.Exception.Message)"
        return $false
    }

    foreach ($line in ($newText -split "`r?`n")) {
        if ($line -match "(?i)Error occurred while (send|recv).+EOF") {
            $VpnRuntime.EofFailureCount++
        } elseif ($line -match "(?i)(send|recv) handshake: read [1-9][0-9]* bytes") {
            $VpnRuntime.EofFailureCount = 0
        }
    }

    return $VpnRuntime.EofFailureCount -ge $EofRestartCount
}

$envFile = Join-Path $PSScriptRoot "vpn.env"
$envExample = Join-Path $PSScriptRoot "vpn.env.example"
if (-not (Test-Path -LiteralPath $envFile) -and (Test-Path -LiteralPath $envExample)) {
    Copy-Item -LiteralPath $envExample -Destination $envFile -Force
    Write-Log "Created vpn.env from vpn.env.example. You can edit vpn.env to make future starts fully automatic."
}
Import-KeyValueFile $envFile

$vpnServer = Get-EnvValue "CAU_VPN_SERVER" "vpn.cau.edu.cn"
$vpnPort = [int](Get-EnvValue "CAU_VPN_PORT" "443")
$vpnUsername = Get-EnvValue "CAU_VPN_USERNAME"
$vpnPassword = Get-EnvValue "CAU_VPN_PASSWORD"
$socksBind = Get-EnvValue "CAU_VPN_SOCKS_BIND" "127.0.0.1:1080"
$resolveRule = Get-EnvValue "CAU_VPN_RESOLVE" "newjw.cau.edu.cn=10.200.36.235"
$showVpnWindow = (Get-EnvValue "CAU_VPN_SHOW_WINDOW" "false").ToLowerInvariant() -in @("1", "true", "yes", "y")
$monitorConfig = Get-EnvValue "CAU_MONITOR_CONFIG"
$restartMinutes = Get-EnvInt "CAU_VPN_RESTART_MINUTES" 120 0
$supervisorIntervalSeconds = Get-EnvInt "CAU_VPN_SUPERVISOR_INTERVAL_SECONDS" 30 5
$eofRestartCount = Get-EnvInt "CAU_VPN_EOF_RESTART_COUNT" 4 0
$vpnRetrySeconds = Get-EnvInt "CAU_VPN_RETRY_SECONDS" 30 5
$vpnStartTimeoutSeconds = Get-EnvInt "CAU_VPN_START_TIMEOUT_SECONDS" 60 10

if ([string]::IsNullOrWhiteSpace($vpnUsername)) {
    $vpnUsername = Read-Host "VPN username"
}
if ([string]::IsNullOrWhiteSpace($vpnPassword)) {
    $vpnPassword = Read-PasswordPlainText "VPN password"
}

$vpnExe = Find-VpnExe
if (-not $vpnExe) {
    throw "EasierConnect.exe was not found. Put NJUConnect-rebuild-windows-x64-cau-dns next to this monitor folder, or set CAU_VPN_EXE in vpn.env."
}

$socks = Split-HostPort $socksBind
$vpnArgs = @(
    "-server", $vpnServer,
    "-port", "$vpnPort",
    "-username", $vpnUsername,
    "-password", $vpnPassword,
    "-socks-bind", $socksBind
)
if (-not [string]::IsNullOrWhiteSpace($resolveRule)) {
    $vpnArgs += @("-resolve", $resolveRule)
}

$existingListener = Get-TcpListenerProcess $socks.Port
if ($existingListener) {
    $existingPath = $null
    try {
        $existingPath = $existingListener.Path
    } catch {
        $existingPath = $null
    }
    $isEasierConnect = $existingListener.ProcessName -match "(?i)^EasierConnect$"
    if ($existingPath) {
        $isEasierConnect = $isEasierConnect -or ([string]::Equals($existingPath, $vpnExe, [System.StringComparison]::OrdinalIgnoreCase))
    }

    if (-not $isEasierConnect) {
        throw "SOCKS5 port $socksBind is already used by PID=$($existingListener.Id) ($($existingListener.ProcessName)). Stop that process or choose another CAU_VPN_SOCKS_BIND."
    }

    Write-Log "Found an existing EasierConnect listener (PID=$($existingListener.Id)); restarting it so automatic reconnection can manage the session."
    Stop-Process -Id $existingListener.Id -Force -ErrorAction SilentlyContinue
    if (-not (Wait-TcpPortClosed $socks.Host $socks.Port 10)) {
        throw "SOCKS5 port $socksBind stayed open after the existing EasierConnect process was stopped."
    }
}

Ensure-MonitorFiles

$nodeExe = Get-NodeExe
$monitorArgs = @(".\monitor.js")
if (-not [string]::IsNullOrWhiteSpace($monitorConfig)) {
    $monitorArgs += @("--config", $monitorConfig)
}

$vpnRuntime = $null
$monitorProcess = $null
$exitCode = 1

try {
    $vpnRuntime = Start-ManagedVpn $vpnExe $vpnArgs $vpnServer $socks.Host $socks.Port $socksBind $showVpnWindow $vpnStartTimeoutSeconds
    $restartText = if ($restartMinutes -gt 0) { "every $restartMinutes minute(s)" } else { "disabled" }
    $eofText = if ($eofRestartCount -gt 0 -and -not $showVpnWindow) { "enabled after $eofRestartCount consecutive EOF log entries" } else { "disabled" }
    Write-Log "VPN supervisor active. Scheduled reconnect: $restartText; EOF recovery: $eofText."

    Write-Log "Starting grade monitor."
    $monitorProcess = Start-Process -FilePath $nodeExe -ArgumentList $monitorArgs -WorkingDirectory $PSScriptRoot -PassThru -NoNewWindow

    while (-not $monitorProcess.HasExited) {
        Start-Sleep -Seconds $supervisorIntervalSeconds

        $restartReason = $null
        if ($vpnRuntime.Process.HasExited) {
            $restartReason = "VPN process exited with code $($vpnRuntime.Process.ExitCode)"
        } elseif (-not (Test-TcpPort $socks.Host $socks.Port 1000)) {
            $restartReason = "SOCKS5 port $socksBind stopped listening"
        } elseif ($restartMinutes -gt 0 -and (Get-Date) -ge $vpnRuntime.StartedAt.AddMinutes($restartMinutes)) {
            $restartReason = "scheduled $restartMinutes-minute session renewal"
        } elseif (Update-VpnLogHealth $vpnRuntime $eofRestartCount) {
            $restartReason = "$($vpnRuntime.EofFailureCount) consecutive EOF entries in the VPN tunnel log"
        }

        if (-not $restartReason) {
            continue
        }

        Write-Log "Reconnecting VPN: $restartReason. The grade monitor will keep running."
        while (-not $monitorProcess.HasExited) {
            try {
                Stop-VpnProcess $vpnRuntime.Process $socks.Host $socks.Port
                $vpnRuntime = Start-ManagedVpn $vpnExe $vpnArgs $vpnServer $socks.Host $socks.Port $socksBind $showVpnWindow $vpnStartTimeoutSeconds
                Write-Log "VPN reconnection completed."
                break
            } catch {
                Write-Log "VPN reconnection failed: $($_.Exception.Message) Retrying in $vpnRetrySeconds seconds."
                Start-Sleep -Seconds $vpnRetrySeconds
            }
        }
    }

    $monitorProcess.WaitForExit()
    $exitCode = $monitorProcess.ExitCode
    Write-Log "Grade monitor exited with code $exitCode."
} finally {
    if ($vpnRuntime -and $vpnRuntime.Process -and -not $vpnRuntime.Process.HasExited) {
        Stop-Process -Id $vpnRuntime.Process.Id -Force -ErrorAction SilentlyContinue
    }
}

exit $exitCode
