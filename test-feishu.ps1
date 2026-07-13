$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Set-Location $PSScriptRoot
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Host "Node.js is not installed or not in PATH."
    Write-Host "Install Node.js LTS, reopen PowerShell, then run this script again."
    exit 1
}
node .\monitor.js --test-feishu
