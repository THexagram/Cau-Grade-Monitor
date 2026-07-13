param(
    [string]$TaskName = "CAU Grade Feishu Monitor"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$startScript = Join-Path $PSScriptRoot "start.ps1"
$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$startScript`"" `
    -WorkingDirectory $PSScriptRoot

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Days 3650) `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Description "Monitor CAU grades and notify Feishu." `
    -Force

Write-Host "Scheduled task created: $TaskName"
Write-Host "It starts when this Windows user logs on."
