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

$bundledNode = Join-Path $PSScriptRoot "node\node.exe"
if (Test-Path $bundledNode) {
    & $bundledNode ".\monitor.js" "--test-feishu"
    exit $LASTEXITCODE
}

$systemNode = Get-Command node -ErrorAction SilentlyContinue
if ($systemNode) {
    node ".\monitor.js" "--test-feishu"
    exit $LASTEXITCODE
}

Write-Host "Node.js was not found. Use the full ZIP package that contains the node folder, or install Node.js LTS."
exit 1
