@echo off
setlocal
cd /d "%~dp0"
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Debug.ps1" -Restart
if errorlevel 1 pause
