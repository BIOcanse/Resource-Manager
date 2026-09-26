@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\release\Publish-ResourceManagerLocal.ps1" -Target All %*
set "RM_EXIT=%ERRORLEVEL%"
if not "%RM_EXIT%"=="0" pause
exit /b %RM_EXIT%
