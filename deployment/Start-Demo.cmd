@echo off
setlocal
cd /d "%~dp0"
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Debug.ps1" -Restart -Demo
if errorlevel 1 pause
