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

rem install.ps1 keeps the window open itself until a key is pressed and leaves a marker file
rem when it did. Without the marker, PowerShell did not get that far (blocked by policy or
rem antivirus, or a damaged file), so a failure pauses here instead.
set "JIMSPROXY_QS_PAUSE=1"
set "PAUSED=%TEMP%\jimsproxy-quickstart.paused"
if exist "%PAUSED%" del /q "%PAUSED%" >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%install.ps1" %*
set "RC=%ERRORLEVEL%"
if exist "%PAUSED%" (
  del /q "%PAUSED%" >nul 2>&1
  exit /b %RC%
)
if "%RC%"=="0" exit /b 0
echo.
echo The installer stopped with exit code %RC%. The messages above explain why.
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
