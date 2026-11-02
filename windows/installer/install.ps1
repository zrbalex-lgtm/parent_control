<#
.SYNOPSIS
  Installs or updates the Parent Control guard on this PC (Windows PowerShell 5.1+).

.DESCRIPTION
  - stops the running service and tray apps, copies the files to Program Files
  - creates/updates the Windows service (auto start, restart on failure)
  - starts the tray app for every user at sign-in
  - registers the PC with a one-time code from the admin app (first install)
  Run again with a newer build to update; registration is kept.

.EXAMPLE
  .\install.ps1                 # interactive, asks for the code if needed
  .\install.ps1 -Code ABCD-EFGH
#>
[CmdletBinding()]
param(
    [string]$Code,
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ParentControl'),
    [switch]$NoRegister,   # skip registration (CI, or register later)
    [switch]$NoStart,      # do not start the service
    [switch]$NoPause       # do not wait for Enter at the end
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'ParentControlGuard'
$ProcessName = 'ParentControl.Guard'
$ExeName     = 'ParentControl.Guard.exe'
$RunKey      = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$RunValue    = 'ParentControlTray'
$DataDir     = Join-Path $env:ProgramData 'ParentControl'
$TaskName    = 'ParentControl Watchdog'
$Source      = $PSScriptRoot

# Runs a native command whose failure is fine (e.g. the task does not exist yet); PS 5.1 would turn its stderr into an error.
function Invoke-Quiet([scriptblock]$Command) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command 2>&1 | Out-Null } finally { $ErrorActionPreference = $old }
}

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Warn([string]$Text) { Write-Host "    $Text" -ForegroundColor Yellow }

function Test-Admin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-End {
    if (-not $NoPause) { [void](Read-Host 'Нажми Enter, чтобы закрыть окно') }
}

# Re-launch elevated, forwarding the parameters.
if (-not (Test-Admin)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        if ($kv.Value -is [switch]) { if ($kv.Value) { $argList += "-$($kv.Key)" } }
        else { $argList += "-$($kv.Key)"; $argList += "`"$($kv.Value)`"" }
    }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit 0
}

try {
    if (-not (Test-Path (Join-Path $Source $ExeName))) {
        throw "Рядом со скриптом нет $ExeName. Запускай install.ps1 из распакованной папки сборки."
    }

    # ---------------------------------------------------------------- stop
    Write-Step 'Останавливаю службу и трей'
    # The watchdog would start the service again in the middle of the update.
    Invoke-Quiet { & schtasks.exe /Change /TN $TaskName /DISABLE }
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1

    # ---------------------------------------------------------------- copy
    $src = (Resolve-Path $Source).Path.TrimEnd('\')
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $dst = (Resolve-Path $InstallDir).Path.TrimEnd('\')
    if ($src -ne $dst) {
        Write-Step "Копирую файлы в $dst"
        $copied = $false
        for ($i = 1; $i -le 10 -and -not $copied; $i++) {
            try {
                Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force
                $copied = $true
            } catch {
                # A tray in another session may still be releasing the exe.
                Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
                Start-Sleep -Seconds 2
            }
        }
        if (-not $copied) { throw 'Не удалось заменить файлы: они заняты. Перезагрузи ПК и запусти установку ещё раз.' }
    }
    Get-ChildItem -Path $dst -Recurse -File | Unblock-File
    $exe = Join-Path $dst $ExeName

    # ---------------------------------------------------------------- service
    Write-Step 'Настраиваю службу'
    $binPath = "`"$exe`""
    if ($svc) {
        $cim = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
        if ($cim.PathName -ne $binPath) {
            $r = Invoke-CimMethod -InputObject $cim -MethodName Change -Arguments @{ PathName = $binPath }
            if ($r.ReturnValue -ne 0) { throw "Не удалось обновить путь службы (код $($r.ReturnValue))" }
        }
        Set-Service -Name $ServiceName -StartupType Automatic
    } else {
        New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName 'Parent Control Guard' `
            -Description 'Экранное время: учёт и ограничение времени за компьютером' -StartupType Automatic | Out-Null
    }
    # Restart after 5 s, 5 s, then 30 s; reset the failure count daily. Also on non-crash failures.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null

    # ---------------------------------------------------------------- tray
    Write-Step 'Включаю автозапуск трея'
    Set-ItemProperty -Path $RunKey -Name $RunValue -Value "`"$exe`" tray"

    # ---------------------------------------------------------------- register
    $registered = Test-Path (Join-Path $DataDir 'device.json')
    if ($NoRegister) {
        Write-Warn 'Регистрация пропущена (-NoRegister).'
    } elseif ($registered -and -not $Code) {
        Write-Step 'ПК уже зарегистрирован — регистрацию пропускаю'
    } else {
        if (-not $Code) {
            $Code = Read-Host 'Код из админки (Устройства → Добавить компьютер)'
        }
        Write-Step 'Регистрирую ПК'
        & $exe register $Code.Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Регистрация не удалась. Проверь код (он действует 24 часа) и интернет.' }
    }

    # ---------------------------------------------------------------- protection
    # Folder/registry ACLs, service permissions, Safe Mode, watchdog task. Run by us = trusted baseline.
    Write-Step 'Включаю защиту от вмешательства'
    & $exe harden
    if ($LASTEXITCODE -ne 0) { throw "Не удалось включить защиту (код $LASTEXITCODE)" }

    # ---------------------------------------------------------------- start
    if (-not $NoStart) {
        Write-Step 'Запускаю службу'
        Start-Service -Name $ServiceName
    }

    # ---------------------------------------------------------------- check accounts
    Write-Step 'Учётные записи на этом ПК'
    try {
        $adminsSid = 'S-1-5-32-544'
        $admins = @(Get-LocalGroupMember -SID $adminsSid -ErrorAction Stop | ForEach-Object { ($_.Name -split '\\')[-1] })
        foreach ($u in Get-LocalUser | Where-Object { $_.Enabled }) {
            if ($admins -contains $u.Name) {
                Write-Host ("    {0,-24} администратор" -f $u.Name) -ForegroundColor Yellow
            } else {
                Write-Host ("    {0,-24} обычная" -f $u.Name)
            }
        }
        Write-Warn 'У детей должны быть ОБЫЧНЫЕ учётки: администратор может остановить службу.'
    } catch {
        Write-Warn 'Не удалось прочитать список учёток (это не мешает работе).'
    }

    Write-Host ''
    Write-Host "Готово. Версия: $((Get-Item $exe).VersionInfo.ProductVersion). Папка: $dst" -ForegroundColor Green
    Write-Host 'Дальше: в админке на вкладке «Устройства» привяжи учётку ребёнка, когда он первый раз войдёт.'
    Wait-End
} catch {
    Write-Host ''
    Write-Host "Ошибка: $($_.Exception.Message)" -ForegroundColor Red
    Wait-End
    exit 1
}
