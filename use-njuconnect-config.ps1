$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Set-Location $PSScriptRoot

if (Test-Path ".\config.json") {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    Copy-Item ".\config.json" ".\config.backup-$stamp.json"
    Write-Host "Backed up config.json to config.backup-$stamp.json"
}

Copy-Item ".\config.njuconnect.example.json" ".\config.json" -Force
Write-Host "Enabled NJUConnect/PAC config."
Write-Host "Make sure EasierConnect.exe is running with SOCKS5 on 127.0.0.1:1080 before starting the monitor."
