@echo off
setlocal
cd /d "%~dp0"
"%~dp0DesktopPerfWidget.exe" --stop
timeout /t 1 /nobreak >nul
"%~dp0DesktopPerfWidget.exe"
echo.
echo Started in stable visible desktop mode.
echo Runtime log: %LOCALAPPDATA%\DesktopPerfWidget\DesktopPerfWidget.log
pause
