<#
.SYNOPSIS
  Lets every local account sign in at any hour again (undoes the guard's re-login block).
  Run as administrator; the installer and uninstaller run it too.
#>
[CmdletBinding()]
param(
    [switch]$NoPause,
    [switch]$KeepGuardBlocks   # installer: only clear Windows settings, the service re-applies running blocks
)

$ErrorActionPreference = 'Continue'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($NoPause) { $argList += '-NoPause' }
    if ($KeepGuardBlocks) { $argList += '-KeepGuardBlocks' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit 0
}

# Tell a running guard to forget its blocks, so it does not put them back after a restart.
$dataDir = Join-Path $env:ProgramData 'ParentControl'
if ((Test-Path $dataDir) -and -not $KeepGuardBlocks) {
    Set-Content -Path (Join-Path $dataDir 'unblock-logon.flag') -Value $env:USERNAME -ErrorAction SilentlyContinue
}

foreach ($u in Get-LocalUser | Where-Object { $_.Enabled }) {
    & net.exe user $u.Name /times:all 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host ("  {0,-24} вход разрешён в любое время" -f $u.Name) }
    else { Write-Host ("  {0,-24} не удалось (код {1})" -f $u.Name, $LASTEXITCODE) -ForegroundColor Yellow }
}

if (-not $NoPause) { [void](Read-Host 'Готово. Нажми Enter, чтобы закрыть окно') }
exit 0
