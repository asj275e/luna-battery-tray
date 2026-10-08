@echo off
rem Remove LunaBatteryTray from the current user's startup (HKCU Run).
rem (Chinese notes: see README.md.)
setlocal
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v LunaBatteryTray /f
if errorlevel 1 (
  echo [info] autostart was not enabled.
) else (
  echo [done] autostart disabled.
)
pause
