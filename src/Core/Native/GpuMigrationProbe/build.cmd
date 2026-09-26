@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "OUT_DIR=%SCRIPT_DIR%bin\win-x64"
set "SHIM_DIR=%OUT_DIR%\shim"
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
if not exist "%SHIM_DIR%" mkdir "%SHIM_DIR%"
g++ -std=c++17 -O2 -Wall -Wextra -static-libgcc -static-libstdc++ -o "%OUT_DIR%\ResourceManager.GpuMigrationProbe.exe" "%SCRIPT_DIR%ResourceManagerGpuMigrationProbe.cpp" -ldxgi -ld3d11 -lole32
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -static-libgcc -static-libstdc++ -o "%OUT_DIR%\ResourceManager.D3D11WindowReselectProbe.exe" "%SCRIPT_DIR%D3D11WindowReselectProbe.cpp" -ldxgi -ld3d11 -lgdi32 -luser32
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -shared -static-libgcc -static-libstdc++ -o "%SHIM_DIR%\d3d11.dll" "%SCRIPT_DIR%D3D11GpuPreferenceShim.cpp" -ldxgi
if errorlevel 1 exit /b %ERRORLEVEL%
exit /b %ERRORLEVEL%
