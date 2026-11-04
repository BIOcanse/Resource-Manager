@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "OUT_DIR=%SCRIPT_DIR%bin\win-x64"
if not "%~1"=="" set "OUT_DIR=%~f1"
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
windres -I "%SCRIPT_DIR%..\..\..\Shared\Assets\ProductIcon" -i "%SCRIPT_DIR%..\..\..\Shared\Assets\ProductIcon\product.rc" -O coff -o "%OUT_DIR%\product-icon.o"
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -Werror -Wno-cast-function-type -static -municode "%SCRIPT_DIR%ResourceManagerGpuPlacementExternal.cpp" "%OUT_DIR%\product-icon.o" -o "%OUT_DIR%\ResourceManager.GpuPlacementExternal.exe" -ldxgi -ldxguid -lshell32
if errorlevel 1 exit /b %errorlevel%
g++ -std=c++17 -O2 -Wall -Wextra -Werror -Wno-unused-function -Wno-cast-function-type -static -municode "%SCRIPT_DIR%DxgiRendererController.cpp" "%OUT_DIR%\product-icon.o" -o "%OUT_DIR%\ResourceManager.GpuRendererExternal.exe" -ld3d11 -ld3d12 -ldxgi -ldxguid -luser32 -ldwmapi -lbcrypt
if errorlevel 1 exit /b %errorlevel%
