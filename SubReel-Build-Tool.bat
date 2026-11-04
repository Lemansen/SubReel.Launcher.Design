@echo off
setlocal EnableExtensions
 title SubReel Launcher Build Tool

for %%I in ("%~dp0.") do set "ROOT=%%~fI"
set "SCRIPT=%ROOT%\BuildTool.ps1"

if not exist "%SCRIPT%" (
    echo ERROR: BuildTool.ps1 was not found next to this BAT file.
    echo Extract BOTH files from the ZIP into the project root folder.
    pause
    exit /b 1
)

REM Prefer PowerShell 7, but support the Windows PowerShell shipped with Windows.
set "PS=powershell.exe"
where.exe pwsh.exe >nul 2>nul && set "PS=pwsh.exe"

"%PS%" -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "%SCRIPT%" -ProjectRoot "%ROOT%"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
    echo.
    echo SubReel Build Tool closed with exit code %RESULT%.
    pause
)
endlocal & exit /b %RESULT%
