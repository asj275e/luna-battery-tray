@echo off
rem Build LunaBatteryTray.exe with the .NET Framework compiler shipped with Windows.
rem (Chinese notes: see README.md. This file is ASCII-only so it works under any codepage.)
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [error] csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ ^
  /out:LunaBatteryTray.exe ^
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\LunaBatteryTray.cs

if errorlevel 1 (
  echo [failed] build failed
  exit /b 1
)

echo [done] built %CD%\LunaBatteryTray.exe
