#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>
#include <stdlib.h>

/* A small user-facing EXE. The managed launcher retains service/UI ownership. */
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR ignored, int show)
{
    (void)instance;
    (void)previous;
    (void)ignored;
    (void)show;

    wchar_t ownPath[32768];
    DWORD length = GetModuleFileNameW(NULL, ownPath, 32768);
    if (length == 0 || length >= 32768) return 1;
    wchar_t *separator = wcsrchr(ownPath, L'\\');
    if (separator == NULL) return 1;
    *(separator + 1) = L'\0';

    const wchar_t suffix[] = L"Bin\\ResourceManagerLauncher\\ResourceManager.Launcher.exe";
    size_t directoryLength = wcslen(ownPath);
    size_t targetLength = directoryLength + wcslen(suffix) + 1;
    if (targetLength >= 32768) return 1;
    wchar_t *target = (wchar_t *)malloc(targetLength * sizeof(wchar_t));
    if (target == NULL) return 1;
    wcscpy(target, ownPath);
    wcscat(target, suffix);

    DWORD attributes = GetFileAttributesW(target);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
    {
        MessageBoxW(NULL, L"发行包内的启动器不存在。请重新解压完整发行包。",
            L"Resource Manager", MB_OK | MB_ICONERROR);
        free(target);
        return 1;
    }

    const wchar_t *tail = GetCommandLineW();
    if (*tail == L'"')
    {
        tail++;
        while (*tail != L'\0' && *tail != L'"') tail++;
        if (*tail == L'"') tail++;
    }
    else
    {
        while (*tail != L'\0' && *tail != L' ' && *tail != L'\t') tail++;
    }

    size_t commandLength = targetLength + wcslen(tail) + 3;
    wchar_t *command = (wchar_t *)malloc(commandLength * sizeof(wchar_t));
    if (command == NULL) { free(target); return 1; }
    command[0] = L'"';
    wcscpy(command + 1, target);
    wcscat(command, L"\"");
    wcscat(command, tail);

    STARTUPINFOW startup = { .cb = sizeof(startup) };
    PROCESS_INFORMATION process = {0};
    BOOL started = CreateProcessW(target, command, NULL, NULL, FALSE, 0,
        NULL, ownPath, &startup, &process);
    free(command);
    if (!started)
    {
        MessageBoxW(NULL, L"无法启动 Resource Manager。", L"Resource Manager", MB_OK | MB_ICONERROR);
        free(target);
        return 1;
    }
    free(target);
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exitCode = 1;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return (int)exitCode;
}
