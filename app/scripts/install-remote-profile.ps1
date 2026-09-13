param(
    [Parameter(Mandatory=$true)][string]$EnvFile,
    [Parameter(Mandatory=$true)][string]$ServerExe,
    [string]$TaskName = 'CodexArchiveRemote-test',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
if ($TaskName -notmatch '^CodexArchiveRemote-test(?:-[A-Za-z0-9_-]+)?$') { throw 'Only isolated test task names are supported' }
$launcher = Join-Path $PSScriptRoot 'start-remote-profile.ps1'
& $launcher -EnvFile $EnvFile -ServerExe $ServerExe -ValidateOnly
if ($ValidateOnly) { exit 0 }
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { throw 'Task already exists; refusing to overwrite it' }
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$args = '-NoProfile -NonInteractive -WindowStyle Hidden -File "{0}" -EnvFile "{1}" -ServerExe "{2}"' -f $launcher,$EnvFile,$ServerExe
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $args -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Output "Installed isolated login autostart task: $TaskName"
