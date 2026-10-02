@echo off
rem JimsProxy quick-start installer. Double-click this file after extracting the zip.
setlocal
set "HERE=%~dp0"

rem Opened from inside the zip, Explorer copies only this file to a temporary folder, so
rem install.ps1 is missing next to it and the path contains ".zip\".
if not exist "%HERE%install.ps1" goto :inzip
if not "%HERE:.zip\=%"=="%HERE%" goto :inzip
if defined TEMP (
  call set "AFTER=%%HERE:*%TEMP%\=%%"
)
if defined AFTER if not "%AFTER%"=="%HERE%" goto :inzip

rem install.ps1 keeps the window open itself until a key is pressed (exit codes 0 to 6).
set "JIMSPROXY_QS_PAUSE=1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%install.ps1" %*
set "RC=%ERRORLEVEL%"
if %RC% GTR 6 goto :failed
if %RC% LSS 0 goto :failed
exit /b %RC%

:failed
rem Any other exit code means PowerShell could not run the installer at all.
echo.
echo The installer could not run (exit code %RC%).
pause
exit /b %RC%

:inzip
echo Extract the zip first, then run this file from the extracted folder.
echo.
echo In File Explorer: right-click JimsProxy-QuickStart.zip, select "Extract All...", then
echo "Extract", and double-click "Install JimsProxy.cmd" in the extracted folder.
echo.
pause
exit /b 2
