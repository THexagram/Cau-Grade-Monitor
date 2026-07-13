$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Set-Location $PSScriptRoot

if (-not (Test-Path ".\config.json")) {
    Copy-Item ".\config.example.json" ".\config.json"
    Write-Host "Created config.json from config.example.json"
}

if (-not (Test-Path ".\.env")) {
    Copy-Item ".\.env.example" ".\.env"
    Write-Host "Created .env from .env.example"
}

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Host ""
    Write-Host "Node.js/npm is not installed or not in PATH."
    Write-Host "Install Node.js LTS first, then reopen PowerShell and rerun this script."
    Write-Host ""
    Write-Host "Recommended command if winget is available:"
    Write-Host "  winget install --id OpenJS.NodeJS.LTS -e"
    Write-Host ""
    Write-Host "If winget is unavailable, download the Windows x64 LTS installer from:"
    Write-Host "  https://nodejs.org/"
    exit 1
}

npm install

Write-Host ""
Write-Host "Next:"
Write-Host "1. Edit .env and put FEISHU_WEBHOOK_URL / FEISHU_BOT_SECRET."
Write-Host "2. Run: powershell -ExecutionPolicy Bypass -File .\start.ps1"
