#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <wchar.h>
#include <stdlib.h>
#include <stdio.h>
#include "StartEntryText.h"

static void ReportStartError(const wchar_t *message, BOOL serviceAction)
{
    if (serviceAction)
        fwprintf(stderr, L"%ls\n", message);
    else
        MessageBoxW(NULL, message, L"Resource Manager", MB_OK | MB_ICONERROR);
}

static BOOL ReadServiceAction(const wchar_t *commandLine, BOOL *serviceAction)
{
    int count = 0;
    wchar_t **arguments = CommandLineToArgvW(commandLine, &count);
    if (arguments == NULL) return FALSE;
    *serviceAction = FALSE;
    for (int index = 1; index < count; index++)
    {
        if (_wcsicmp(arguments[index], L"--service-action") == 0)
            *serviceAction = TRUE;
    }
    LocalFree(arguments);
    return TRUE;
}

/* Installed maintenance entries resolve the registered product root.
   A portable root entry still resolves its own package directory. */
static BOOL ResolveRegisteredInstallRoot(wchar_t *directory, const wchar_t *registeredRoot)
{
    wchar_t root[32768], expected[32768];
    if (wcslen(registeredRoot) + 64 >= 32768) return FALSE;
    wcscpy(root, registeredRoot);
    size_t length = wcslen(root);
    if (length == 0 || length + 64 >= 32768) return FALSE;
    if (root[length - 1] == L'\\') root[--length] = L'\0';
    wcscpy(expected, root);
    wcscat(expected, L"\\");
    if (_wcsicmp(directory, expected) != 0)
    {
        wcscat(expected, L"Internal\\Management\\");
        if (_wcsicmp(directory, expected) != 0) return FALSE;
    }
    wcscpy(directory, root);
    wcscat(directory, L"\\");
    return TRUE;
}

static BOOL ReadRegisteredInstallRoot(wchar_t *directory)
{
    wchar_t root[32768];
    DWORD bytes = sizeof(root);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, L"Software\\ResourceManager", L"InstallRoot",
        RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, NULL, root, &bytes) != ERROR_SUCCESS) return FALSE;
    return ResolveRegisteredInstallRoot(directory, root);
}

static void OpenRecovery(const wchar_t *root, const StartEntryErrorCopy *text)
{
    wchar_t manager[32768];
    if (wcslen(root) + 96 >= 32768) return;
    wcscpy(manager, root);
    size_t length = wcslen(manager);
    if (length && manager[length - 1] == L'\\') manager[--length] = L'\0';
    wcscat(manager, L"\\Internal\\Management\\ResourceManager.UpdateManager.exe");
    if (MessageBoxW(NULL, text->recovery, L"Resource Manager", MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2) == IDYES)
        ShellExecuteW(NULL, L"open", manager, NULL, NULL, SW_SHOWNORMAL);
}

static DWORD FinishLauncherStart(PROCESS_INFORMATION *process, BOOL serviceAction, DWORD timeout)
{
    DWORD exitCode = 0;
    if (serviceAction)
    {
        DWORD waited = WaitForSingleObject(process->hProcess, timeout);
        if (waited == WAIT_OBJECT_0)
        {
            if (!GetExitCodeProcess(process->hProcess, &exitCode))
                exitCode = GetLastError();
        }
        else
        {
            exitCode = waited == WAIT_TIMEOUT ? ERROR_TIMEOUT
                : waited == WAIT_FAILED ? GetLastError() : ERROR_GEN_FAILURE;
        }
    }
    // Ordinary launch succeeds when the internal launcher has been created.
    // Its error dialog and UI lifetime must not hold this entry's caller open.
    CloseHandle(process->hThread);
    CloseHandle(process->hProcess);
    return exitCode;
}

/* A small user-facing EXE. The managed launcher retains service/UI ownership. */
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR ignored, int show)
{
    (void)instance;
    (void)previous;
    (void)ignored;
    (void)show;

    BOOL serviceAction = FALSE;
    if (!ReadServiceAction(GetCommandLineW(), &serviceAction)) return 1;
    const StartEntryErrorCopy *text = StartEntryTextForLanguage(GetUserDefaultUILanguage());

    wchar_t ownPath[32768];
    DWORD length = GetModuleFileNameW(NULL, ownPath, 32768);
    if (length == 0 || length >= 32768) return 1;
    wchar_t *separator = wcsrchr(ownPath, L'\\');
    if (separator == NULL) return 1;
    *(separator + 1) = L'\0';
    BOOL registered = ReadRegisteredInstallRoot(ownPath);

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
        if (registered && !serviceAction) OpenRecovery(ownPath, text);
        else ReportStartError(text->missingLauncher, serviceAction);
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
        ReportStartError(text->createFailed, serviceAction);
        free(target);
        return 1;
    }
    free(target);
    return (int)FinishLauncherStart(&process, serviceAction, 100000);
}
