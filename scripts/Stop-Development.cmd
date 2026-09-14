@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Development.ps1" -Action Stop -Elevate %*
set "RM_EXIT=%ERRORLEVEL%"
if not "%RM_EXIT%"=="0" pause
exit /b %RM_EXIT%
