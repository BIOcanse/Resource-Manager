/* Build with zig cc -target x86_64-windows-gnu -municode -lshell32 -luser32 -ladvapi32. */
#include <stdio.h>
#define wWinMain ResourceManagerStartEntryUnderTest
#include "../../src/UI/Launcher/StartEntry.c"
#undef wWinMain

static int failures;
static unsigned int fixtureSequence;

static void Check(BOOL condition, const char *description)
{
    if (!condition)
    {
        fprintf(stderr, "%s\n", description);
        failures++;
    }
}

static void CheckArguments(const wchar_t *command, BOOL expected)
{
    BOOL service = !expected;
    Check(ReadServiceAction(command, &service), "Could not parse fixture command line");
    Check(service == expected, "Service action must be a complete argument");
}

static void CheckLocalizedErrors(void)
{
    const LANGID languages[] = { 0x0804, 0x0404, 0x0409, 0x0411, 0x0412,
        0x040c, 0x0407, 0x0c0a, 0x0419 };
    const wchar_t *tokens[] = { L"启动器", L"啟動器", L"launcher", L"ランチャー", L"실행기",
        L"lanceur", L"Startprogramm", L"lanzador", L"программа запуска" };
    const StartEntryErrorCopy *english = StartEntryTextForLanguage(0x0409);
    for (size_t index = 0; index < sizeof(languages) / sizeof(languages[0]); index++)
    {
        const StartEntryErrorCopy *text = StartEntryTextForLanguage(languages[index]);
        Check(text->missingLauncher[0] != L'\0' && text->createFailed[0] != L'\0',
            "Bootstrap errors must not be empty");
        Check(wcsstr(text->missingLauncher, tokens[index]) != NULL,
            "Bootstrap must display the requested language");
        if (index != 2)
        {
            Check(wcscmp(text->missingLauncher, english->missingLauncher) != 0
                && wcscmp(text->createFailed, english->createFailed) != 0,
                "Offered bootstrap languages must not inherit English errors");
        }
    }
    Check(StartEntryTextForLanguage(0x0c04) == StartEntryTextForLanguage(0x0404),
        "Hong Kong Chinese must use Traditional Chinese");
    Check(StartEntryTextForLanguage(0x1404) == StartEntryTextForLanguage(0x0404),
        "Macao Chinese must use Traditional Chinese");
    Check(StartEntryTextForLanguage(0x0809) == english,
        "Regional English must use delivered English copy");
    Check(StartEntryTextForLanguage(0x0416) == english,
        "Undelivered bootstrap languages must use the documented English fallback");
}

static BOOL SpawnFixture(const wchar_t *eventName, HANDLE releaseEvent,
    PROCESS_INFORMATION *process, HANDLE *observer)
{
    wchar_t path[32768];
    DWORD length = GetModuleFileNameW(NULL, path, 32768);
    if (length == 0 || length >= 32768) return FALSE;
    size_t capacity = (size_t)length + wcslen(eventName) + 64;
    wchar_t *command = malloc(capacity * sizeof(wchar_t));
    if (command == NULL) return FALSE;
    int count = _snwprintf(command, capacity, L"\"%ls\" --fixture \"%ls\"", path, eventName);
    if (count < 0 || (size_t)count >= capacity)
    {
        free(command);
        return FALSE;
    }
    STARTUPINFOW startup = { .cb = sizeof(startup) };
    BOOL started = CreateProcessW(path, command, NULL, NULL, FALSE, CREATE_NO_WINDOW,
        NULL, NULL, &startup, process);
    free(command);
    if (!started) return FALSE;
    if (!DuplicateHandle(GetCurrentProcess(), process->hProcess, GetCurrentProcess(),
        observer, 0, FALSE, DUPLICATE_SAME_ACCESS))
    {
        /* The fixture exits on its own; no other process is terminated. */
        SetEvent(releaseEvent);
        WaitForSingleObject(process->hProcess, 5000);
        CloseHandle(process->hThread);
        CloseHandle(process->hProcess);
        return FALSE;
    }
    return TRUE;
}

static void CheckLifecycle(BOOL service, DWORD timeout, DWORD expected, BOOL expectedAlive)
{
    wchar_t name[128];
    int count = _snwprintf(name, 128, L"Local\\ResourceManager.StartEntry.Tests.%lu.%llu.%u",
        (unsigned long)GetCurrentProcessId(), (unsigned long long)GetTickCount64(), ++fixtureSequence);
    if (count < 0 || count >= 128)
    {
        Check(FALSE, "Could not format owned fixture event name");
        return;
    }
    SetLastError(ERROR_SUCCESS);
    HANDLE releaseEvent = CreateEventW(NULL, TRUE, FALSE, name);
    if (releaseEvent == NULL || GetLastError() == ERROR_ALREADY_EXISTS)
    {
        if (releaseEvent != NULL) CloseHandle(releaseEvent);
        Check(FALSE, "Could not create a unique owned fixture event");
        return;
    }
    PROCESS_INFORMATION process = {0};
    HANDLE observer = NULL;
    if (!SpawnFixture(name, releaseEvent, &process, &observer))
    {
        CloseHandle(releaseEvent);
        Check(FALSE, "Could not start owned fixture child");
        return;
    }
    if (!expectedAlive) SetEvent(releaseEvent);
    DWORD result = FinishLauncherStart(&process, service, timeout);
    Check(result == expected, "Wrong launch completion result");
    DWORD childExit = 0;
    Check(GetExitCodeProcess(observer, &childExit), "Could not read owned fixture result");
    Check((childExit == STILL_ACTIVE) == expectedAlive,
        "Normal launch and service timeout must leave the child running");
    SetEvent(releaseEvent);
    Check(WaitForSingleObject(observer, 5000) == WAIT_OBJECT_0,
        "Owned fixture did not finish within its bounded lifetime");
    Check(GetExitCodeProcess(observer, &childExit) && childExit == 27,
        "Child lifetime or exit result was altered");
    CloseHandle(observer);
    CloseHandle(releaseEvent);
}

static void CheckInstalledRootResolution(void)
{
    const wchar_t root[] = L"D:\\Fixture\\Resource Manager";
    const wchar_t resolved[] = L"D:\\Fixture\\Resource Manager\\";
    wchar_t directory[32768];
    wcscpy(directory, L"D:\\Fixture\\Resource Manager\\Internal\\Management\\");
    Check(ResolveRegisteredInstallRoot(directory, root) && wcscmp(directory, resolved) == 0,
        "Maintenance Start must resolve the main root");
    wcscpy(directory, resolved);
    Check(ResolveRegisteredInstallRoot(directory, root), "Installed root Start must offer recovery");
    wcscpy(directory, L"D:\\Fixture\\Portable\\");
    Check(!ResolveRegisteredInstallRoot(directory, root)
        && wcscmp(directory, L"D:\\Fixture\\Portable\\") == 0, "Portable Start must retain its own root");
    wcscpy(directory, L"D:\\Fixture\\Resource Manager\\Internal\\Management-other\\");
    Check(!ResolveRegisteredInstallRoot(directory, root), "Sibling names must not match maintenance root");
}

int wmain(int argc, wchar_t **argv)
{
    /* Any non-test invocation exits instead of recursively running this suite. */
    if (argc != 1)
    {
        if (argc != 3 || wcscmp(argv[1], L"--fixture") != 0) return 2;
        const wchar_t prefix[] = L"Local\\ResourceManager.StartEntry.Tests.";
        if (wcsncmp(argv[2], prefix, wcslen(prefix)) != 0) return 2;
        HANDLE releaseEvent = OpenEventW(SYNCHRONIZE, FALSE, argv[2]);
        if (releaseEvent == NULL) return 2;
        DWORD waited = WaitForSingleObject(releaseEvent, 10000);
        CloseHandle(releaseEvent);
        return waited == WAIT_OBJECT_0 ? 27 : 2;
    }
    CheckArguments(L"\"D:\\Resource Manager\\Start.exe\" --service-action --restart", TRUE);
    CheckArguments(L"Start.exe \"--service-action\"", TRUE);
    CheckArguments(L"Start.exe --SERVICE-ACTION", TRUE);
    CheckArguments(L"Start.exe --background-startup", FALSE);
    CheckArguments(L"Start.exe \"--service-action extra\"", FALSE);
    CheckArguments(L"\"D:\\--service-action\\Start.exe\" --restart", FALSE);
    CheckArguments(L"Start.exe --service-action-other", FALSE);
    CheckInstalledRootResolution();
    CheckLocalizedErrors();
    CheckLifecycle(FALSE, 250, 0, TRUE);
    CheckLifecycle(TRUE, 2000, 27, FALSE);
    CheckLifecycle(TRUE, 25, ERROR_TIMEOUT, TRUE);
    if (failures == 0) puts("Start entry lifecycle checks passed");
    return failures != 0;
}
