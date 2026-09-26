@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "OUT_DIR=%SCRIPT_DIR%bin\win-x64"
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
gcc -shared -O2 -Wall -Wextra -static-libgcc -o "%OUT_DIR%\ResourceManager.AdlxBridge.dll" "%SCRIPT_DIR%ResourceManagerAdlxBridge.c"
exit /b %ERRORLEVEL%
