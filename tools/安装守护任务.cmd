@echo off
rem Register the watchdog scheduled task (every 5 minutes).
rem It restarts LunaBatteryTray if it is gone, but never while game anti-cheat is running.
rem (Chinese notes: see README.md.)
setlocal
set EXE=%~dp0..\LunaBatteryTray.Watchdog.exe
if not exist "%EXE%" (
  echo [error] LunaBatteryTray.Watchdog.exe not found
  pause
  exit /b 1
)

"%EXE%" --install
echo.
pause
