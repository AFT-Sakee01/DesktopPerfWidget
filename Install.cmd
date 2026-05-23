@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
echo.
echo Log: %LOCALAPPDATA%\DesktopPerfWidget\install.log
echo Runtime log: %LOCALAPPDATA%\DesktopPerfWidget\DesktopPerfWidget.log
pause
