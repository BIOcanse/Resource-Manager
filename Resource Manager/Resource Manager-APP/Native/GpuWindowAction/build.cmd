@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "OUT_DIR=%SCRIPT_DIR%bin\win-x64"

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
if errorlevel 1 exit /b %ERRORLEVEL%

g++ -std=c++17 -O2 -Wall -Wextra -Werror -static -municode "%SCRIPT_DIR%ResourceManagerGpuWindowAction.cpp" -o "%OUT_DIR%\ResourceManager.GpuWindowAction.exe" -luser32 -ladvapi32
if errorlevel 1 exit /b %ERRORLEVEL%

exit /b 0
