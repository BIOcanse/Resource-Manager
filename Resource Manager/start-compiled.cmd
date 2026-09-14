@echo off
setlocal
set "RM_STARTER=%~dp0..\scripts\Start-ResourceManagerInstalled.ps1"
if not exist "%RM_STARTER%" goto missing_starter
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%RM_STARTER%" %*
set "RM_EXIT=%ERRORLEVEL%"
if not "%RM_EXIT%"=="0" pause
exit /b %RM_EXIT%

:missing_starter
echo [Resource Manager] Installed-product starter not found: %RM_STARTER%
exit /b 1
