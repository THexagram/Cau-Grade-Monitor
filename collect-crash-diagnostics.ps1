[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $env:USERPROFILE "Desktop"),
    [ValidateRange(1, 30)]
    [int]$Days = 7
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) "CauGradeMonitor-Diagnostics-$timestamp"
$zipPath = Join-Path $outputRoot "CauGradeMonitor-Diagnostics-$timestamp.zip"
$errors = [Collections.Generic.List[string]]::new()

function Invoke-DiagnosticStep {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [scriptblock]$Action
    )

    try {
        & $Action
    } catch {
        $errors.Add("$Name`: $($_.Exception.Message)")
    }
}

function Export-JsonFile {
    param(
        [Parameter(Mandatory)] $InputObject,
        [Parameter(Mandatory)] [string]$Path,
        [int]$Depth = 5
    )

    $InputObject | ConvertTo-Json -Depth $Depth | Set-Content -LiteralPath $Path -Encoding utf8
}

try {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    $startTime = (Get-Date).AddDays(-$Days)

    Invoke-DiagnosticStep "System crash events" {
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName = "System"
            StartTime = $startTime
            Id = @(41, 46, 161, 1001, 6008)
        } -ErrorAction SilentlyContinue | Select-Object TimeCreated, Id, ProviderName, LevelDisplayName, Message)
        if ($events.Count) {
            $events | Export-Csv -LiteralPath (Join-Path $stagingRoot "system-crash-events.csv") -NoTypeInformation -Encoding utf8
            $events | Format-List | Out-File -LiteralPath (Join-Path $stagingRoot "system-crash-events.txt") -Encoding utf8 -Width 300
        } else {
            "No matching System crash events were found in the selected time window." |
                Set-Content -LiteralPath (Join-Path $stagingRoot "system-crash-events.txt") -Encoding utf8
        }
    }

    Invoke-DiagnosticStep "Windows Error Reporting events" {
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName = "Application"
            ProviderName = "Windows Error Reporting"
            StartTime = $startTime
            Id = 1001
        } -ErrorAction SilentlyContinue | Select-Object TimeCreated, Id, ProviderName, LevelDisplayName, Message)
        if ($events.Count) {
            $events | Export-Csv -LiteralPath (Join-Path $stagingRoot "windows-error-reporting-events.csv") -NoTypeInformation -Encoding utf8
        }
    }

    Invoke-DiagnosticStep "Hardware error events" {
        $events = @(Get-WinEvent -FilterHashtable @{
            LogName = "System"
            ProviderName = "Microsoft-Windows-WHEA-Logger"
            StartTime = $startTime
        } -ErrorAction SilentlyContinue | Select-Object TimeCreated, Id, ProviderName, LevelDisplayName, Message)
        if ($events.Count) {
            $events | Export-Csv -LiteralPath (Join-Path $stagingRoot "whea-events.csv") -NoTypeInformation -Encoding utf8
        }
    }

    Invoke-DiagnosticStep "CrashControl registry" {
        $crashControl = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl"
        Export-JsonFile ($crashControl | Select-Object CrashDumpEnabled, DumpFile, MinidumpDir, LogEvent, AutoReboot, Overwrite, AlwaysKeepMemoryDump) (Join-Path $stagingRoot "crash-control.json")
    }

    Invoke-DiagnosticStep "Dump metadata" {
        $dumpFiles = [Collections.Generic.List[IO.FileInfo]]::new()
        $memoryDump = Join-Path $env:SystemRoot "MEMORY.DMP"
        if (Test-Path -LiteralPath $memoryDump) {
            $dumpFiles.Add((Get-Item -LiteralPath $memoryDump))
        }
        $dumpDirectories = @(
            (Join-Path $env:SystemRoot "Minidump"),
            (Join-Path $env:SystemRoot "LiveKernelReports")
        )
        foreach ($dumpDirectory in $dumpDirectories) {
            if (Test-Path -LiteralPath $dumpDirectory) {
                try {
                    Get-ChildItem -LiteralPath $dumpDirectory -Filter "*.dmp" -File -Recurse -ErrorAction Stop |
                        Sort-Object LastWriteTime -Descending |
                        Select-Object -First 20 |
                        ForEach-Object { $dumpFiles.Add($_) }
                } catch {
                    $errors.Add("Dump metadata ($dumpDirectory): $($_.Exception.Message)")
                }
            }
        }
        if ($dumpFiles.Count) {
            $dumpFiles |
                Select-Object FullName, @{Name="SizeMB"; Expression={[Math]::Round($_.Length / 1MB, 2)}}, CreationTime, LastWriteTime |
                Export-Csv -LiteralPath (Join-Path $stagingRoot "dump-files-metadata.csv") -NoTypeInformation -Encoding utf8
        } else {
            "No accessible crash dump files were found." |
                Set-Content -LiteralPath (Join-Path $stagingRoot "dump-files-metadata.txt") -Encoding utf8
        }
    }

    Invoke-DiagnosticStep "Operating system and hardware" {
        $os = Get-CimInstance Win32_OperatingSystem |
            Select-Object Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, TotalVisibleMemorySize, FreePhysicalMemory, TotalVirtualMemorySize, FreeVirtualMemorySize
        $computer = Get-CimInstance Win32_ComputerSystem |
            Select-Object Manufacturer, Model, NumberOfLogicalProcessors, TotalPhysicalMemory, AutomaticManagedPagefile
        $disks = Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" |
            Select-Object DeviceID, VolumeName, FileSystem, Size, FreeSpace
        Export-JsonFile ([ordered]@{ CollectedAt = (Get-Date); OperatingSystem = $os; ComputerSystem = $computer; Disks = @($disks) }) (Join-Path $stagingRoot "system-summary.json") 6
    }

    Invoke-DiagnosticStep "Pagefile configuration" {
        $settings = @(Get-CimInstance Win32_PageFileSetting | Select-Object Name, InitialSize, MaximumSize)
        $usage = @(Get-CimInstance Win32_PageFileUsage | Select-Object Name, AllocatedBaseSize, CurrentUsage, PeakUsage, TempPageFile)
        Export-JsonFile ([ordered]@{ Settings = $settings; Usage = $usage }) (Join-Path $stagingRoot "pagefile.json") 5
    }

    Invoke-DiagnosticStep "Installed drivers" {
        Get-CimInstance Win32_PnPSignedDriver |
            Select-Object DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, InfName, IsSigned |
            Sort-Object DeviceClass, DeviceName |
            Export-Csv -LiteralPath (Join-Path $stagingRoot "signed-drivers.csv") -NoTypeInformation -Encoding utf8

        Get-CimInstance Win32_SystemDriver |
            Select-Object Name, DisplayName, State, StartMode, ServiceType, PathName |
            Sort-Object Name |
            Export-Csv -LiteralPath (Join-Path $stagingRoot "system-drivers.csv") -NoTypeInformation -Encoding utf8
    }

    Invoke-DiagnosticStep "Network adapters" {
        Get-NetAdapter -IncludeHidden |
            Select-Object Name, InterfaceDescription, Status, LinkSpeed, DriverDescription, DriverVersion, DriverDate |
            Export-Csv -LiteralPath (Join-Path $stagingRoot "network-adapters.csv") -NoTypeInformation -Encoding utf8
    }

    Invoke-DiagnosticStep "Recent hotfixes" {
        Get-HotFix |
            Sort-Object InstalledOn -Descending |
            Select-Object -First 100 -Property Source, Description, HotFixID, InstalledBy, InstalledOn |
            Export-Csv -LiteralPath (Join-Path $stagingRoot "hotfixes.csv") -NoTypeInformation -Encoding utf8
    }

    Invoke-DiagnosticStep "Monitor telemetry" {
        $monitorLogs = Join-Path $env:LOCALAPPDATA "CauGradeMonitor\logs"
        if (Test-Path -LiteralPath $monitorLogs) {
            $telemetryDirectory = Join-Path $stagingRoot "resource-telemetry"
            New-Item -ItemType Directory -Path $telemetryDirectory -Force | Out-Null
            Get-ChildItem -LiteralPath $monitorLogs -Filter "resources-*.csv" -File |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 7 |
                Copy-Item -Destination $telemetryDirectory

            Get-ChildItem -LiteralPath $monitorLogs -Filter "app-*.log" -File |
                Select-Object Name, Length, CreationTime, LastWriteTime |
                Export-Csv -LiteralPath (Join-Path $stagingRoot "monitor-log-metadata.csv") -NoTypeInformation -Encoding utf8
        }
    }

    @"
CAU Grade Monitor crash diagnostics
Collected: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz")
Window: last $Days day(s)

Included:
- Windows System crash events
- Crash dump configuration and dump file metadata
- OS, memory, disk, pagefile, driver, network adapter, and hotfix metadata
- Bounded resource telemetry from CAU Grade Monitor when available

Intentionally excluded:
- MEMORY.DMP and minidump contents
- VPN credentials and process command lines
- Feishu webhook/secret
- Browser profile, cookies, monitor configuration, grade state, and grade-bearing app log contents
"@ | Set-Content -LiteralPath (Join-Path $stagingRoot "README.txt") -Encoding utf8

    if ($errors.Count) {
        $errors | Set-Content -LiteralPath (Join-Path $stagingRoot "collection-errors.txt") -Encoding utf8
    }

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Diagnostics package created: $zipPath"
    if ($errors.Count) {
        Write-Warning "$($errors.Count) diagnostic step(s) could not be collected. See collection-errors.txt in the ZIP."
    }
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
    if ((Test-Path -LiteralPath $resolvedStaging) -and $resolvedStaging.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
