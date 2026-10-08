@echo off
rem Build both executables with the .NET Framework compiler shipped with Windows.
rem (Chinese notes: see README.md. This file is ASCII-only so it works under any codepage.)
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [error] csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)

rem --- tray app ---
"%CSC%" /nologo /target:winexe /optimize+ ^
  /out:LunaBatteryTray.exe ^
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\LunaBatteryTray.cs src\AppLog.cs src\AntiCheat.cs

if errorlevel 1 (
  echo [failed] tray build failed
  echo          is LunaBatteryTray.exe still running? exit it from the tray menu and retry.
  exit /b 1
)

rem --- watchdog (no WinForms on purpose) ---
"%CSC%" /nologo /target:winexe /optimize+ ^
  /out:LunaBatteryTray.Watchdog.exe ^
  /r:System.dll ^
  src\Watchdog.cs src\AppLog.cs src\AntiCheat.cs

if errorlevel 1 (
  echo [failed] watchdog build failed
  exit /b 1
)

echo [done] built %CD%\LunaBatteryTray.exe
echo [done] built %CD%\LunaBatteryTray.Watchdog.exe
