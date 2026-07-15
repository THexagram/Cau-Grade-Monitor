#Requires -RunAsAdministrator

[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet("Kernel", "Automatic", "Small")]
    [string]$DumpType = "Kernel",
    [switch]$ConfigureSystemManagedPagefile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$dumpValues = @{
    Kernel = 2
    Small = 3
    Automatic = 7
}
$crashControlPath = "HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl"
$dumpValue = $dumpValues[$DumpType]

if ($PSCmdlet.ShouldProcess("Windows CrashControl", "Enable $DumpType crash dumps")) {
    New-Item -Path $crashControlPath -Force | Out-Null
    New-ItemProperty -Path $crashControlPath -Name "CrashDumpEnabled" -Value $dumpValue -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $crashControlPath -Name "DumpFile" -Value "%SystemRoot%\MEMORY.DMP" -PropertyType ExpandString -Force | Out-Null
    New-ItemProperty -Path $crashControlPath -Name "MinidumpDir" -Value "%SystemRoot%\Minidump" -PropertyType ExpandString -Force | Out-Null
    New-ItemProperty -Path $crashControlPath -Name "LogEvent" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $crashControlPath -Name "Overwrite" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $crashControlPath -Name "AlwaysKeepMemoryDump" -Value 1 -PropertyType DWord -Force | Out-Null
}

if ($ConfigureSystemManagedPagefile) {
    $computerSystem = Get-CimInstance Win32_ComputerSystem
    if ($PSCmdlet.ShouldProcess($computerSystem.Name, "Enable a system-managed pagefile")) {
        Set-CimInstance -InputObject $computerSystem -Property @{ AutomaticManagedPagefile = $true } | Out-Null
    }
}

$crashControl = Get-ItemProperty -LiteralPath $crashControlPath
$computer = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
$systemDrive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($env:SystemDrive)'"
$pagefileSettings = @(Get-CimInstance Win32_PageFileSetting | Select-Object Name, InitialSize, MaximumSize)
$pagefileUsage = @(Get-CimInstance Win32_PageFileUsage | Select-Object Name, AllocatedBaseSize, CurrentUsage, PeakUsage)

Write-Host "Crash dump configuration:"
[pscustomobject]@{
    DumpType = $DumpType
    CrashDumpEnabled = $crashControl.CrashDumpEnabled
    DumpFile = $crashControl.DumpFile
    AlwaysKeepMemoryDump = $crashControl.AlwaysKeepMemoryDump
    AutomaticManagedPagefile = $computer.AutomaticManagedPagefile
    PhysicalMemoryGB = [Math]::Round($computer.TotalPhysicalMemory / 1GB, 2)
    SystemDriveFreeGB = [Math]::Round($systemDrive.FreeSpace / 1GB, 2)
    LastBoot = $os.LastBootUpTime
} | Format-List

Write-Host "Pagefile settings:"
if ($pagefileSettings.Count) { $pagefileSettings | Format-Table -AutoSize } else { Write-Warning "No Win32_PageFileSetting entries were found." }
Write-Host "Current pagefile usage:"
if ($pagefileUsage.Count) { $pagefileUsage | Format-Table -AutoSize } else { Write-Warning "No active pagefile was found." }

if (-not $computer.AutomaticManagedPagefile) {
    Write-Warning "The pagefile is not system-managed. Re-run with -ConfigureSystemManagedPagefile unless a sufficiently large custom pagefile is intentional."
}
if ($systemDrive.FreeSpace -lt 5GB) {
    Write-Warning "The system drive has less than 5 GB free. A kernel dump may not be written successfully."
}

Write-Host "Configuration complete. Pagefile changes, when requested, take effect after a Windows restart."
