@echo off
setlocal
title Uninstall Net Summary Bar deskband
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
  echo Please right-click uninstall.bat and choose "Run as administrator".
  pause & exit /b 1
)
if exist "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" (
  set REGASM=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe
) else (
  set REGASM=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\regasm.exe
)
"%REGASM%" /nologo /unregister "%~dp0NetSummaryBarDeskBand.dll"
echo.
echo Unregistered. Untick the toolbar in the taskbar menu if it is still shown, then restart Explorer
echo before deleting the DLL (Explorer keeps it loaded until then).
pause
