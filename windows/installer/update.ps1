<#
.SYNOPSIS
  Unattended update, started by the guard service (as SYSTEM) from an unpacked release.

.DESCRIPTION
  Backs up the current installation, runs install.ps1 from this package, checks that the service
  runs the new version, and restores the backup if anything fails. Registration and data are kept.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [string]$Log
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'ParentControlGuard'
$ProcessName = 'ParentControl.Guard'
$TaskName    = 'ParentControl Watchdog'
$Backup      = Join-Path $env:ProgramData 'ParentControl\updates\backup'
$NewExe      = Join-Path $PSScriptRoot 'ParentControl.Guard.exe'

if ($Log) { Start-Transcript -Path $Log -Force | Out-Null }

# Native tools whose failure is fine must not turn into terminating errors (PS 5.1).
function Invoke-Quiet([scriptblock]$Command) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | Out-Null } finally { $ErrorActionPreference = $old }
}

$code = 0
try {
    $expected = (Get-Item $NewExe).VersionInfo.FileVersion
    Write-Host "Updating to $expected"

    if (Test-Path $Backup) { Remove-Item $Backup -Recurse -Force }
    Copy-Item -Path $InstallDir -Destination $Backup -Recurse -Force

    & (Join-Path $PSScriptRoot 'install.ps1') -NoRegister -NoPause -InstallDir $InstallDir
    if ($LASTEXITCODE) { throw "install.ps1 failed with code $LASTEXITCODE" }

    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Service $ServiceName).Status -ne 'Running') {
        if ((Get-Date) -gt $deadline) { throw 'service did not start' }
        Start-Sleep -Seconds 2
    }
    $installed = (Get-Item (Join-Path $InstallDir 'ParentControl.Guard.exe')).VersionInfo.FileVersion
    if ($installed -ne $expected) { throw "installed version is $installed, expected $expected" }
    Write-Host 'update ok'
} catch {
    $code = 1
    $ErrorActionPreference = 'Continue'
    Write-Host "update failed: $($_.Exception.Message); rolling back"
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if (Test-Path $Backup) {
        Copy-Item -Path (Join-Path $Backup '*') -Destination $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Invoke-Quiet { & schtasks.exe /Change /TN $TaskName /ENABLE }
    Write-Host 'rollback done'
} finally {
    if ($Log) { Stop-Transcript | Out-Null }
}
exit $code
