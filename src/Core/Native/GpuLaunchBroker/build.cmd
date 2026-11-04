@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "OUT_DIR=%SCRIPT_DIR%bin\win-x64"
if not "%~1"=="" set "OUT_DIR=%~f1"

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
windres -I "%SCRIPT_DIR%..\..\..\Shared\Assets\ProductIcon" -i "%SCRIPT_DIR%..\..\..\Shared\Assets\ProductIcon\product.rc" -O coff -o "%OUT_DIR%\product-icon.o"
if errorlevel 1 exit /b %ERRORLEVEL%

g++ -std=c++17 -O2 -Wall -Wextra -municode -mwindows -static -o "%OUT_DIR%\ResourceManager.GpuLaunchBroker.exe" "%SCRIPT_DIR%ResourceManagerGpuLaunchBroker.cpp" "%OUT_DIR%\product-icon.o" -lshell32 -ladvapi32
if errorlevel 1 exit /b %ERRORLEVEL%

g++ -std=c++17 -O2 -Wall -Wextra -municode -mwindows -static -o "%OUT_DIR%\ResourceManager.GpuLaunchTargetProbe.exe" "%SCRIPT_DIR%ResourceManagerGpuLaunchTargetProbe.cpp" "%OUT_DIR%\product-icon.o" -lshell32
if errorlevel 1 exit /b %ERRORLEVEL%

exit /b 0
