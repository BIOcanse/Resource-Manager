@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Register-ResourceManager.ps1" %*
set "RM_EXIT=%ERRORLEVEL%"
if not "%RM_EXIT%"=="0" pause
exit /b %RM_EXIT%
