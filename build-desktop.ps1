param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "desktop-publish"),
    [string]$EasierConnectExe = "",
    [string]$NodeDirectory = "",
    [switch]$ExcludeEasierConnect
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

function Resolve-OptionalPath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $expanded = [Environment]::ExpandEnvironmentVariables($Value)
    if (-not [IO.Path]::IsPathRooted($expanded)) { $expanded = Join-Path $PSScriptRoot $expanded }
    if (-not (Test-Path -LiteralPath $expanded)) { return $null }
    return (Resolve-Path -LiteralPath $expanded).Path
}

function Remove-BuildDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $allowedRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
    if (-not $fullPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the build output: $fullPath"
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function Find-NodeDirectory {
    $configured = Resolve-OptionalPath $NodeDirectory
    if ($configured -and (Test-Path -LiteralPath (Join-Path $configured "node.exe"))) { return $configured }

    $local = Join-Path $PSScriptRoot "node"
    if (Test-Path -LiteralPath (Join-Path $local "node.exe")) { return $local }

    $command = Get-Command node -ErrorAction SilentlyContinue
    if ($command) { return (Split-Path $command.Source -Parent) }
    throw "Node.js was not found. Pass -NodeDirectory with a folder containing node.exe."
}

function Find-EasierConnect {
    $configured = Resolve-OptionalPath $EasierConnectExe
    if ($configured) { return $configured }

    foreach ($candidate in @(
        (Join-Path $PSScriptRoot "EasierConnect.exe"),
        (Join-Path $PSScriptRoot "NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe"),
        (Join-Path (Split-Path $PSScriptRoot -Parent) "NJUConnect-rebuild-windows-x64-cau-dns\EasierConnect.exe")
    )) {
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    return $null
}

$projectFile = Join-Path $PSScriptRoot "desktop\CauGradeMonitor.Desktop\CauGradeMonitor.Desktop.csproj"
$publishDirectory = Join-Path $OutputDirectory "CauGradeMonitor-win-x64"
$runtimeDirectory = Join-Path $publishDirectory "runtime"
$zipPath = Join-Path $OutputDirectory "CauGradeMonitor-win-x64.zip"

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Remove-BuildDirectory $publishDirectory
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot "node_modules\playwright-core"))) {
    & npm ci --omit=dev --prefix $PSScriptRoot
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed with code $LASTEXITCODE" }
}

& dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with code $LASTEXITCODE" }

New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "monitor.js") -Destination $runtimeDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "package.json") -Destination $runtimeDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "package-lock.json") -Destination $runtimeDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "node_modules") -Destination $runtimeDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "edge-cau-proxy-extension") -Destination $runtimeDirectory -Recurse

$resolvedNodeDirectory = Find-NodeDirectory
$nodeTarget = Join-Path $runtimeDirectory "node"
New-Item -ItemType Directory -Path $nodeTarget -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $resolvedNodeDirectory "node.exe") -Destination $nodeTarget
$nodeLicense = Join-Path $resolvedNodeDirectory "LICENSE"
if (Test-Path -LiteralPath $nodeLicense) { Copy-Item -LiteralPath $nodeLicense -Destination $nodeTarget }

$resolvedEasierConnect = if ($ExcludeEasierConnect) { $null } else { Find-EasierConnect }
if ($resolvedEasierConnect) {
    Copy-Item -LiteralPath $resolvedEasierConnect -Destination (Join-Path $runtimeDirectory "EasierConnect.exe")
    Write-Host "Included EasierConnect.exe from: $resolvedEasierConnect"
} else {
    Write-Warning "EasierConnect.exe was not included. Users can select it from the GUI settings page."
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "desktop\README.md") -Destination (Join-Path $publishDirectory "README.md")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "collect-crash-diagnostics.ps1") -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "enable-kernel-dump.ps1") -Destination $publishDirectory

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "Desktop package created:"
Write-Host "  Folder: $publishDirectory"
Write-Host "  ZIP:    $zipPath"
