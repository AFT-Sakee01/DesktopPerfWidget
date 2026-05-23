@echo off
setlocal
cd /d "%~dp0"
"%~dp0DesktopPerfWidget.exe" --stop
timeout /t 1 /nobreak >nul
"%~dp0DesktopPerfWidget.exe" --desktop-parent
echo.
echo Started in experimental WorkerW desktop-parent mode.
echo Runtime log: %LOCALAPPDATA%\DesktopPerfWidget\DesktopPerfWidget.log
pause
