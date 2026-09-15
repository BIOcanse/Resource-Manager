@echo off
setlocal
cd /d "%~dp0"
if "%~1"=="" goto :usage
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\New-ResourceManagerReleasePackage.ps1" -Version %*
set "RM_EXIT=%ERRORLEVEL%"
if not "%RM_EXIT%"=="0" pause
exit /b %RM_EXIT%

:usage
echo Usage: Release.cmd ^<version^> [extra arguments for New-ResourceManagerReleasePackage.ps1]
echo Example: Release.cmd 0.2.1-beta.20260915.1
exit /b 2
