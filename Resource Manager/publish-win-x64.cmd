@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-all-win-x64.ps1" -Target Backend %*
exit /b %ERRORLEVEL%
