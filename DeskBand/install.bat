@echo off
setlocal
title Install Net Summary Bar deskband
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
  echo Please right-click install.bat and choose "Run as administrator".
  pause & exit /b 1
)
if exist "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" (
  set REGASM=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe
) else (
  set REGASM=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\regasm.exe
)
"%REGASM%" /nologo /codebase "%~dp0NetSummaryBarDeskBand.dll"
if errorlevel 1 ( echo Registration failed. & pause & exit /b 1 )
echo.
echo Done. Right-click the taskbar ^> Toolbars ^> "Net Summary Bar".
echo If it is not listed, restart Explorer (Task Manager ^> Windows Explorer ^> Restart) or sign out and in.
pause
