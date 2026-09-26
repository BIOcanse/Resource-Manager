@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "THIRD_PARTY=%SCRIPT_DIR%third_party\minhook"
set "DETOURS=%SCRIPT_DIR%third_party\detours\src"
set "OUT_DIR=%SCRIPT_DIR%bin\win-x64"
set "OBJ_DIR=%OUT_DIR%\obj"

if exist "%OBJ_DIR%" rmdir /s /q "%OBJ_DIR%"
if not exist "%OBJ_DIR%" mkdir "%OBJ_DIR%"

gcc -O2 -Wall -Wextra -I"%THIRD_PARTY%\include" -I"%THIRD_PARTY%\src" -I"%THIRD_PARTY%\src\hde" -c "%THIRD_PARTY%\src\buffer.c" -o "%OBJ_DIR%\buffer.o"
if errorlevel 1 exit /b %ERRORLEVEL%
gcc -O2 -Wall -Wextra -I"%THIRD_PARTY%\include" -I"%THIRD_PARTY%\src" -I"%THIRD_PARTY%\src\hde" -c "%THIRD_PARTY%\src\hook.c" -o "%OBJ_DIR%\hook.o"
if errorlevel 1 exit /b %ERRORLEVEL%
gcc -O2 -Wall -Wextra -I"%THIRD_PARTY%\include" -I"%THIRD_PARTY%\src" -I"%THIRD_PARTY%\src\hde" -c "%THIRD_PARTY%\src\trampoline.c" -o "%OBJ_DIR%\trampoline.o"
if errorlevel 1 exit /b %ERRORLEVEL%
gcc -O2 -Wall -Wextra -I"%THIRD_PARTY%\include" -I"%THIRD_PARTY%\src" -I"%THIRD_PARTY%\src\hde" -c "%THIRD_PARTY%\src\hde\hde64.c" -o "%OBJ_DIR%\hde64.o"
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -I"%THIRD_PARTY%\include" -I"%THIRD_PARTY%\src" -I"%THIRD_PARTY%\src\hde" -I"%SCRIPT_DIR%third_party\vulkan-headers\include" -c "%SCRIPT_DIR%ResourceManagerGpuPlacementShim.cpp" -o "%OBJ_DIR%\provider.o"
if errorlevel 1 exit /b %ERRORLEVEL%

g++ -std=c++17 -O2 -Wall -Wextra -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -c "%SCRIPT_DIR%OpenGlCallbackSource.cpp" -o "%OBJ_DIR%\opengl-source.o"
if errorlevel 1 exit /b %ERRORLEVEL%

g++ -std=c++17 -O2 -Wall -Wextra -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -c "%SCRIPT_DIR%OpenGlCallbackSourceCodec.cpp" -o "%OBJ_DIR%\opengl-codec.o"
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -c "%SCRIPT_DIR%DetoursRestore.cpp" -o "%OBJ_DIR%\detours-restore.o"
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -shared -static -o "%OUT_DIR%\ResourceManager.GpuPlacementShim.dll" "%OBJ_DIR%\provider.o" "%OBJ_DIR%\opengl-source.o" "%OBJ_DIR%\opengl-codec.o" "%OBJ_DIR%\detours-restore.o" "%OBJ_DIR%\buffer.o" "%OBJ_DIR%\hook.o" "%OBJ_DIR%\trampoline.o" "%OBJ_DIR%\hde64.o" -ld3d11 -ld3d12 -ldxgi -lbcrypt -lgdi32 -luser32
if errorlevel 1 exit /b %ERRORLEVEL%

g++ -std=c++17 -O2 -w -fpermissive -D_AMD64_ -D_MSC_VER=1900 -DDETOURS_MINIMAL_CREATE -DNONAMELESSUNION -I"%DETOURS%" -c "%DETOURS%\creatwth.cpp" -o "%OBJ_DIR%\detours-create.o"
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -w -fpermissive -D_AMD64_ -D_MSC_VER=1900 -DNONAMELESSUNION -I"%DETOURS%" -c "%SCRIPT_DIR%ResourceManagerGpuPlacementBootstrap.cpp" -o "%OBJ_DIR%\bootstrap.o"
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -shared -static -Wl,--gc-sections -o "%OUT_DIR%\ResourceManager.GpuPlacementBootstrap.dll" "%OBJ_DIR%\bootstrap.o" "%OBJ_DIR%\detours-create.o"
if errorlevel 1 exit /b %ERRORLEVEL%

g++ -std=c++17 -O2 -Wall -Wextra -shared -static -I"%SCRIPT_DIR%third_party\vulkan-headers\include" "%SCRIPT_DIR%VulkanPlacementLayer.cpp" -o "%OUT_DIR%\ResourceManager.VulkanPlacementLayer.dll" -ldxgi
if errorlevel 1 exit /b %ERRORLEVEL%
copy /y "%SCRIPT_DIR%ResourceManager.VulkanPlacementLayer.json" "%OUT_DIR%\ResourceManager.VulkanPlacementLayer.json" >nul
if errorlevel 1 exit /b %ERRORLEVEL%

exit /b 0
