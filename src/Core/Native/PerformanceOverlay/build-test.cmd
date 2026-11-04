@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "VULKAN=%SCRIPT_DIR%..\GpuPlacementShim\third_party\vulkan-headers\include"
set "OUT=%SCRIPT_DIR%bin\win-x64"
if not exist "%OUT%" mkdir "%OUT%"
g++ -std=c++17 -O2 -Wall -Wextra -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -static "%SCRIPT_DIR%D3D11IdleReleaseTest.cpp" -o "%OUT%\D3D11IdleReleaseTest.exe" -ld3d11 -ldxgi -luser32 -ladvapi32 -lpsapi
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -static "%SCRIPT_DIR%D3D12IdleReleaseTest.cpp" -o "%OUT%\D3D12IdleReleaseTest.exe" -ld3d12 -ldxgi -luser32 -ladvapi32 -lpsapi
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -static "%SCRIPT_DIR%ReaderTimingTest.cpp" -o "%OUT%\ReaderTimingTest.exe" -luser32 -ladvapi32 -lpsapi
if errorlevel 1 exit /b %ERRORLEVEL%
if /i "%~1"=="d3d-only" exit /b 0
g++ -std=c++17 -O2 -Wall -Wextra -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -static "%SCRIPT_DIR%GlIdleReleaseTest.cpp" -o "%OUT%\GlIdleReleaseTest.exe" -lopengl32 -lgdi32 -luser32 -ladvapi32 -lpsapi
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -Wno-missing-field-initializers -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -I"%VULKAN%" -static "%SCRIPT_DIR%VulkanIdleReleaseTest.cpp" -o "%OUT%\VulkanIdleReleaseTest.exe" -luser32 -ladvapi32 -lpsapi
if errorlevel 1 exit /b %ERRORLEVEL%
exit /b 0
