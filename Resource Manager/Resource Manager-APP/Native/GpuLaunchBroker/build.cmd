@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "OUT_DIR=%SCRIPT_DIR%bin\win-x64"

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

g++ -std=c++17 -O2 -Wall -Wextra -municode -mwindows -static-libgcc -static-libstdc++ -o "%OUT_DIR%\ResourceManager.GpuLaunchBroker.exe" "%SCRIPT_DIR%ResourceManagerGpuLaunchBroker.cpp" -lwinhttp -lshell32 -ladvapi32
if errorlevel 1 exit /b %ERRORLEVEL%

g++ -std=c++17 -O2 -Wall -Wextra -municode -mwindows -static-libgcc -static-libstdc++ -o "%OUT_DIR%\ResourceManager.GpuLaunchTargetProbe.exe" "%SCRIPT_DIR%ResourceManagerGpuLaunchTargetProbe.cpp" -lshell32
if errorlevel 1 exit /b %ERRORLEVEL%

exit /b 0
