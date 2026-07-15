param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "cli-publish"),
    [string]$NodeDirectory = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Resolve-NodeDirectory {
    if (-not [string]::IsNullOrWhiteSpace($NodeDirectory)) {
        $configured = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($NodeDirectory))
        if (Test-Path -LiteralPath (Join-Path $configured "node.exe")) { return $configured }
        throw "NodeDirectory does not contain node.exe: $configured"
    }

    $command = Get-Command node -ErrorAction SilentlyContinue
    if ($command) { return (Split-Path $command.Source -Parent) }
    throw "Node.js was not found. Install Node.js or pass -NodeDirectory."
}

function Remove-BuildPath([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
    $target = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $target.StartsWith($outputRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the build output: $target"
    }
    Remove-Item -LiteralPath $target -Recurse -Force
}

$sourceDirectory = Join-Path $OutputDirectory "CauGradeMonitor-cli-node-required"
$portableDirectory = Join-Path $OutputDirectory "CauGradeMonitor-cli-portable-win-x64"
$sourceZip = Join-Path $OutputDirectory "CauGradeMonitor-cli-node-required.zip"
$portableZip = Join-Path $OutputDirectory "CauGradeMonitor-cli-portable-win-x64.zip"

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Remove-BuildPath $sourceDirectory
Remove-BuildPath $portableDirectory
foreach ($zip in @($sourceZip, $portableZip)) {
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
}
New-Item -ItemType Directory -Path $sourceDirectory, $portableDirectory -Force | Out-Null

$files = @(
    ".env.example",
    "vpn.env.example",
    "config.example.json",
    "config.njuconnect.example.json",
    "package.json",
    "package-lock.json",
    "monitor.js",
    "monitor.test.js",
    "install.ps1",
    "start.ps1",
    "start-full.ps1",
    "check-once-full.ps1",
    "test-feishu.ps1",
    "test-feishu-full.ps1",
    "start-vpn-and-monitor.ps1",
    "use-njuconnect-config.ps1",
    "create-scheduled-task.ps1",
    "collect-crash-diagnostics.ps1",
    "enable-kernel-dump.ps1",
    "cau-njuconnect.pac",
    "一键启动VPN和查询.bat",
    "README.md",
    "SECURITY.md",
    "CHANGELOG.md"
)

foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $sourceDirectory
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "edge-cau-proxy-extension") -Destination $sourceDirectory -Recurse

Get-ChildItem -LiteralPath $sourceDirectory -Force |
    Copy-Item -Destination $portableDirectory -Recurse -Force

$resolvedNodeDirectory = Resolve-NodeDirectory
$nodeTarget = Join-Path $portableDirectory "node"
New-Item -ItemType Directory -Path $nodeTarget -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $resolvedNodeDirectory "node.exe") -Destination $nodeTarget
$nodeLicense = Join-Path $resolvedNodeDirectory "LICENSE"
if (Test-Path -LiteralPath $nodeLicense) { Copy-Item -LiteralPath $nodeLicense -Destination $nodeTarget }

$nodeModules = Join-Path $PSScriptRoot "node_modules"
if (-not (Test-Path -LiteralPath (Join-Path $nodeModules "playwright-core"))) {
    throw "node_modules is incomplete. Run npm install before building the portable CLI package."
}
Copy-Item -LiteralPath $nodeModules -Destination $portableDirectory -Recurse

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($sourceDirectory, $sourceZip, [IO.Compression.CompressionLevel]::Optimal, $false)
[IO.Compression.ZipFile]::CreateFromDirectory($portableDirectory, $portableZip, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "CLI packages created:"
Write-Host "  Minimal:  $sourceZip"
Write-Host "  Portable: $portableZip"
