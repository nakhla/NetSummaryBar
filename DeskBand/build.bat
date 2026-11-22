@echo off
rem Rebuilds the DLL. CSDeskBand needs C# 6+, so use the Roslyn compiler (Visual Studio / Build Tools),
rem not the old csc.exe in C:\Windows\Microsoft.NET.
setlocal enabledelayedexpansion
set CSC=
for /r "%ProgramFiles%\Microsoft Visual Studio" %%f in (csc.exe) do if exist "%%f" if "!CSC!"=="" set CSC=%%f
if "%CSC%"=="" for /r "%ProgramFiles(x86)%\Microsoft Visual Studio" %%f in (csc.exe) do if exist "%%f" if "!CSC!"=="" set CSC=%%f
if "%CSC%"=="" ( echo Roslyn csc.exe not found. Install "Build Tools for Visual Studio" or use the prebuilt DLL. & pause & exit /b 1 )
"%CSC%" /nologo /target:library /out:NetSummaryBarDeskBand.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll *.cs
pause
