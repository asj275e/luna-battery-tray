@echo off
rem Remove the watchdog scheduled task.
rem (Chinese notes: see README.md.)
setlocal
set EXE=%~dp0..\LunaBatteryTray.Watchdog.exe
if not exist "%EXE%" (
  echo [error] LunaBatteryTray.Watchdog.exe not found
  pause
  exit /b 1
)

"%EXE%" --uninstall
echo.
pause
