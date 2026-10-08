@echo off
rem Add LunaBatteryTray to the current user's startup (HKCU Run).
rem Double-click to run. Change INTERVAL below to pick a refresh interval.
rem (Chinese notes: see README.md.)
setlocal
set INTERVAL=30
for %%I in ("%~dp0..\LunaBatteryTray.exe") do set EXE=%%~fI

if not exist "%EXE%" (
  echo [error] LunaBatteryTray.exe not found at "%EXE%"
  pause
  exit /b 1
)

reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v LunaBatteryTray /t REG_SZ /d "\"%EXE%\" --interval %INTERVAL%" /f
if errorlevel 1 (
  echo [failed] could not write the registry value.
  pause
  exit /b 1
)

echo [done] autostart enabled:
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v LunaBatteryTray
pause
