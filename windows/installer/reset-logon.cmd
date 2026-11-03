@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0reset-logon.ps1" %*
