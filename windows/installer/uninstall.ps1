<#
.SYNOPSIS
  Removes the Parent Control guard from this PC.

.EXAMPLE
  .\uninstall.ps1               # keep registration and local data
  .\uninstall.ps1 -RemoveData   # also delete C:\ProgramData\ParentControl
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ParentControl'),
    [switch]$RemoveData,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'ParentControlGuard'
$ProcessName = 'ParentControl.Guard'
$RunKey      = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$RunValue    = 'ParentControlTray'
$DataDir     = Join-Path $env:ProgramData 'ParentControl'

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Wait-End { if (-not $NoPause) { [void](Read-Host 'Нажми Enter, чтобы закрыть окно') } }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        if ($kv.Value -is [switch]) { if ($kv.Value) { $argList += "-$($kv.Key)" } }
        else { $argList += "-$($kv.Key)"; $argList += "`"$($kv.Value)`"" }
    }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit 0
}

try {
    Set-Location $env:TEMP   # never hold the install folder open

    Write-Step 'Останавливаю службу и трей'
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force

    if ($svc) {
        Write-Step 'Удаляю службу'
        & sc.exe delete $ServiceName | Out-Null
    }

    Write-Step 'Убираю автозапуск трея'
    Remove-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue

    if (Test-Path $InstallDir) {
        Write-Step "Удаляю $InstallDir"
        Start-Sleep -Seconds 1
        Remove-Item -Path $InstallDir -Recurse -Force
    }

    if ($RemoveData -and (Test-Path $DataDir)) {
        Write-Step "Удаляю данные $DataDir"
        Remove-Item -Path $DataDir -Recurse -Force
    }
    # Registry backup of the registration (restored automatically unless removed too).
    $BackupKey = 'HKLM:\SOFTWARE\ParentControl'
    if ($RemoveData -and (Test-Path $BackupKey)) {
        Write-Step "Удаляю резервную копию в реестре"
        Remove-Item -Path $BackupKey -Recurse -Force
    }
    if (-not $RemoveData -and (Test-Path $DataDir)) {
        Write-Host "    Данные и регистрация оставлены в $DataDir (удалить: -RemoveData)."
    }

    Write-Host ''
    Write-Host 'Удалено.' -ForegroundColor Green
    Write-Host 'Не забудь удалить или отключить этот ПК в админке (вкладка «Устройства»).'
    Wait-End
} catch {
    Write-Host "Ошибка: $($_.Exception.Message)" -ForegroundColor Red
    Wait-End
    exit 1
}
