@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "MINHOOK=%SCRIPT_DIR%..\GpuPlacementShim\third_party\minhook"
set "VULKAN=%SCRIPT_DIR%..\GpuPlacementShim\third_party\vulkan-headers\include"
set "OUT=%SCRIPT_DIR%bin\win-x64"
set "OBJ=%OUT%\obj"
if not exist "%OBJ%" mkdir "%OBJ%"
gcc -O2 -Wall -Wextra -I"%MINHOOK%\include" -I"%MINHOOK%\src" -I"%MINHOOK%\src\hde" -c "%MINHOOK%\src\buffer.c" -o "%OBJ%\buffer.o"
if errorlevel 1 exit /b %ERRORLEVEL%
gcc -O2 -Wall -Wextra -I"%MINHOOK%\include" -I"%MINHOOK%\src" -I"%MINHOOK%\src\hde" -c "%MINHOOK%\src\hook.c" -o "%OBJ%\hook.o"
if errorlevel 1 exit /b %ERRORLEVEL%
gcc -O2 -Wall -Wextra -I"%MINHOOK%\include" -I"%MINHOOK%\src" -I"%MINHOOK%\src\hde" -c "%MINHOOK%\src\trampoline.c" -o "%OBJ%\trampoline.o"
if errorlevel 1 exit /b %ERRORLEVEL%
gcc -O2 -Wall -Wextra -I"%MINHOOK%\include" -I"%MINHOOK%\src" -I"%MINHOOK%\src\hde" -c "%MINHOOK%\src\hde\hde64.c" -o "%OBJ%\hde64.o"
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -I"%MINHOOK%\include" -c "%SCRIPT_DIR%ResourceManagerPerformanceOverlay.cpp" -o "%OBJ%\overlay.o"
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -shared -static -o "%OUT%\ResourceManager.PerformanceOverlay.dll" "%OBJ%\overlay.o" "%OBJ%\buffer.o" "%OBJ%\hook.o" "%OBJ%\trampoline.o" "%OBJ%\hde64.o" -ld3d11 -ldxgi -lopengl32 -lgdi32 -luser32
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -Wno-missing-field-initializers -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -I"%VULKAN%" -shared -static "%SCRIPT_DIR%VulkanOverlayLayer.cpp" -o "%OUT%\ResourceManager.VulkanPerformanceOverlayLayer.dll" -luser32
if errorlevel 1 exit /b %ERRORLEVEL%
copy /y "%SCRIPT_DIR%ResourceManager.VulkanPerformanceOverlayLayer.json" "%OUT%\ResourceManager.VulkanPerformanceOverlayLayer.json" >nul
if errorlevel 1 exit /b %ERRORLEVEL%
exit /b 0
