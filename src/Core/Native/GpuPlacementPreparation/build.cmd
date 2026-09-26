@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "OUT_DIR=%SCRIPT_DIR%bin\win-x64"
set "SHIM_DIR=%SCRIPT_DIR%..\GpuPlacementShim"
set "MINHOOK=%SHIM_DIR%\third_party\minhook"
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
if errorlevel 1 exit /b %ERRORLEVEL%
for %%S in (buffer hook trampoline) do (
  gcc -O2 -c "%MINHOOK%\src\%%S.c" -o "%OUT_DIR%\%%S.o"
  if errorlevel 1 exit /b 1
)
gcc -O2 -c "%MINHOOK%\src\hde\hde64.c" -o "%OUT_DIR%\hde64.o"
if errorlevel 1 exit /b %ERRORLEVEL%
g++ -std=c++17 -O2 -Wall -Wextra -Werror -static -municode -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 "%SCRIPT_DIR%ResourceManagerGpuPlacementPreparation.cpp" "%SCRIPT_DIR%OpenGlCallbackCapture.cpp" "%SHIM_DIR%\OpenGlCallbackSource.cpp" "%SHIM_DIR%\OpenGlCallbackSourceCodec.cpp" "%OUT_DIR%\buffer.o" "%OUT_DIR%\hook.o" "%OUT_DIR%\trampoline.o" "%OUT_DIR%\hde64.o" -o "%OUT_DIR%\ResourceManager.GpuPlacementPreparation.exe" -lbcrypt -lopengl32 -lgdi32 -luser32
if errorlevel 1 exit /b %ERRORLEVEL%
exit /b 0
