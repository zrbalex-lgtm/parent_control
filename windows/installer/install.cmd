@echo off
rem Runs install.ps1; it asks for administrator rights itself.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
