@echo off
rem play.bat - double-click wrapper for play.ps1 (starts JimsProxy, then WoW, stops the proxy when WoW closes).
rem Bypasses the PowerShell execution policy for this one script only. See docs\MANUAL-INSTALL.md.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0play.ps1" %*
if errorlevel 1 (
  echo.
  echo play.ps1 reported an error. Read the messages above.
  pause
)
