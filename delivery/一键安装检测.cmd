@echo off
chcp 65001 >nul
title PeisSelfPrint Setup
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Kiosk.ps1" -CheckOnly
if errorlevel 1 (
  echo.
  echo Check failed, opening setup UI...
  powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Kiosk.ps1"
)
