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
if (Test-TcpPort $socks.Host $socks.Port 1000) {
    Write-Log "SOCKS5 already available on $socksBind; skip VPN start."
} else {
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

    Write-Log "Starting VPN: $vpnServer -> SOCKS5 $socksBind"
    $vpnWorkingDir = Split-Path $vpnExe -Parent
    if ($showVpnWindow) {
        $vpnProcess = Start-Process -FilePath $vpnExe -ArgumentList $vpnArgs -WorkingDirectory $vpnWorkingDir -PassThru
    } else {
        $logDir = Join-Path $PSScriptRoot "logs"
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $stdoutLog = Join-Path $logDir "vpn-$stamp.log"
        $stderrLog = Join-Path $logDir "vpn-$stamp.err.log"
        $vpnProcess = Start-Process -FilePath $vpnExe -ArgumentList $vpnArgs -WorkingDirectory $vpnWorkingDir -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog
        Write-Log "VPN logs: $stdoutLog"
    }

    if (-not (Wait-TcpPort $socks.Host $socks.Port 60)) {
        $exitedText = if ($vpnProcess.HasExited) { " VPN process exited with code $($vpnProcess.ExitCode)." } else { "" }
        throw "VPN SOCKS5 port $socksBind did not become available within 60 seconds.$exitedText"
    }
    Write-Log "VPN SOCKS5 is ready on $socksBind"
}

Ensure-MonitorFiles

$nodeExe = Get-NodeExe
$monitorArgs = @(".\monitor.js")
if (-not [string]::IsNullOrWhiteSpace($monitorConfig)) {
    $monitorArgs += @("--config", $monitorConfig)
}

Write-Log "Starting grade monitor."
& $nodeExe @monitorArgs
exit $LASTEXITCODE
