@echo off
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"

echo ========================================================
echo Building NetSummaryBar (Taskbar Network Monitor)
echo Using Compiler: %CSC%
echo ========================================================

"%CSC%" /target:winexe /win32icon:"%~dp0app.ico" /win32manifest:"%~dp0app.manifest" /out:"%~dp0NetSummaryBar.exe" /optimize+ /platform:anycpu /r:System.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Core.dll "%~dp0*.cs"

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ========================================================
    echo [SUCCESS] NetSummaryBar.exe compiled successfully!
    echo Location: %~dp0NetSummaryBar.exe
    echo ========================================================
) else (
    echo.
    echo [ERROR] Compilation failed with error code %ERRORLEVEL%.
)
