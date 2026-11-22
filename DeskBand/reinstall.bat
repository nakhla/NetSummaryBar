@echo off
setlocal EnableDelayedExpansion
title Net Summary Bar - clean reinstall
cd /d "%~dp0"

rem ---- elevate ----
net session >nul 2>&1
if errorlevel 1 (
  echo Requesting administrator rights...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

set "GUID={8E3B7C54-5C0A-4B63-9D0B-6A1F2C7D41E9}"
set "SRC=%~dp0NetSummaryBarDeskBand.dll"
if not exist "%SRC%" ( echo NetSummaryBarDeskBand.dll not found next to this script. & pause & exit /b 1 )

if exist "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" (
  set "REGASM=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"
) else (
  set "REGASM=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\regasm.exe"
)

echo.
echo [1/5] Stopping Explorer (this releases every loaded copy of the DLL)...
taskkill /f /im explorer.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/5] Removing every old registration...
for /f "tokens=2,*" %%a in ('reg query "HKCR\CLSID\%GUID%\InprocServer32" /v CodeBase /reg:64 2^>nul ^| find "CodeBase"') do echo       previous CodeBase: %%b
reg delete "HKCR\CLSID\%GUID%" /f /reg:64 >nul 2>&1
reg delete "HKCR\CLSID\%GUID%" /f /reg:32 >nul 2>&1
reg delete "HKCR\NetSummaryBar.NetSummaryDeskBand" /f >nul 2>&1

echo [3/5] Copying the DLL into a fresh folder (never reuses a locked file)...
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "STAMP=%%i"
set "DEST=%ProgramFiles%\NetSummaryBar\%STAMP%"
mkdir "%DEST%" >nul 2>&1
copy /y "%SRC%" "%DEST%\" >nul
if errorlevel 1 ( echo Copy failed. & start explorer.exe & pause & exit /b 1 )
echo       installed to: %DEST%

echo [4/5] Registering...
"%REGASM%" /nologo /codebase "%DEST%\NetSummaryBarDeskBand.dll"
if errorlevel 1 ( echo Registration FAILED. & start explorer.exe & pause & exit /b 1 )

echo [5/5] Starting Explorer...
start explorer.exe
timeout /t 8 /nobreak >nul

echo.
echo Now right-click the taskbar ^> Toolbars ^> tick "Net Summary Bar" (untick and tick again if it is already ticked).
echo Wait ~10 seconds, then press a key to show the last log lines.
pause >nul
echo.
echo ---- last lines of %APPDATA%\NetSummaryBar\error.log ----
powershell -NoProfile -Command "Get-Content -Tail 20 \"$env:APPDATA\NetSummaryBar\error.log\""
echo.
pause
