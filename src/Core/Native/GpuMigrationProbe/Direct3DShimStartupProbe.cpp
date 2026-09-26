#define NOMINMAX
#define _WIN32_WINNT 0x0a00
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

int wmain(int argc, wchar_t** argv)
{
    const bool failBeforeResume = argc == 7 && std::wcscmp(argv[6], L"--fail-before-resume") == 0;
    if (argc != 6 && !failBeforeResume)
    {
        std::fprintf(stderr, "Usage: Direct3DShimStartupProbe <bootstrap> <provider> <probe> <policy> <target-luid-hex>\n");
        return 2;
    }
    SetErrorMode(32771);
    HMODULE bootstrap = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (bootstrap == nullptr) return 3;
    using Update = DWORD(WINAPI*)(HANDLE, const wchar_t*, DWORD*);
    const FARPROC address = GetProcAddress(bootstrap, "ResourceManagerGpuPlacementBootstrapUpdate");
    Update update = nullptr;
    static_assert(sizeof(update) == sizeof(address));
    std::memcpy(&update, &address, sizeof(update));
    if (update == nullptr) return 4;
    const std::wstring eventName = L"Local\\ResourceManager.Direct3DShimProbe." + std::to_wstring(GetCurrentProcessId());
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, eventName.c_str());
    if (ready == nullptr || GetLastError() == ERROR_ALREADY_EXISTS) return 5;
    if (!SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", argv[4]) ||
        !SetEnvironmentVariableW(L"RM_GPU_SHIM_READY_EVENT", eventName.c_str())) return 6;
    std::wstring command = L"\"" + std::wstring(argv[3]) + L"\" --startup-child " + argv[5];
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    if (job == nullptr) return 8;
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return 9;
    SIZE_T attributeBytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeBytes);
    if (attributeBytes == 0) return 10;
    std::vector<unsigned char> attributes(attributeBytes);
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    startup.StartupInfo.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    startup.StartupInfo.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
    if (!InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &attributeBytes) ||
        !UpdateProcThreadAttribute(startup.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &job, sizeof(job), nullptr, nullptr)) return 11;
    PROCESS_INFORMATION child{};
    const BOOL created = CreateProcessW(argv[3], command.data(), nullptr, nullptr, TRUE,
        CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &child);
    DeleteProcThreadAttributeList(startup.lpAttributeList);
    if (!created) return 7;
    SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", nullptr);
    SetEnvironmentVariableW(L"RM_GPU_SHIM_READY_EVENT", nullptr);
    FILETIME creation{}, exit{}, kernel{}, user{};
    const bool identityRead = GetProcessTimes(child.hProcess, &creation, &exit, &kernel, &user) != FALSE;
    const auto filetime = (static_cast<unsigned long long>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;
    BOOL childOwned = FALSE;
    const bool ownershipRead = IsProcessInJob(child.hProcess, job, &childOwned) != FALSE;
    std::printf("{\"startupChildPid\":%lu,\"creationFileTimeUtc\":%llu,\"identityRead\":%s,\"ownedBeforeResume\":%s}\n",
        child.dwProcessId, filetime, identityRead ? "true" : "false", ownershipRead && childOwned ? "true" : "false");
    std::fflush(stdout);
    DWORD injectionError = 0;
    const DWORD injected = identityRead && ownershipRead && childOwned && !failBeforeResume ? update(child.hProcess, argv[2], &injectionError) : 0;
    const DWORD resumed = injected == 1 ? ResumeThread(child.hThread) : static_cast<DWORD>(-1);
    const DWORD readyWait = resumed == 1 ? WaitForSingleObject(ready, 5000) : WAIT_FAILED;
    const DWORD exitWait = readyWait == WAIT_OBJECT_0 ? WaitForSingleObject(child.hProcess, 10000) : WAIT_FAILED;
    bool forcedCleanup = false;
    DWORD cleanupWait = exitWait;
    if (exitWait != WAIT_OBJECT_0)
    {
        forcedCleanup = TerminateJobObject(job, ERROR_PROCESS_ABORTED) != FALSE;
        cleanupWait = WaitForSingleObject(child.hProcess, 3000);
    }
    DWORD exitCode = STILL_ACTIVE;
    const bool exitRead = GetExitCodeProcess(child.hProcess, &exitCode) != FALSE;
    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
    bool accountingRead = false;
    const ULONGLONG accountingDeadline = GetTickCount64() + 3000;
    // Process exit can be signaled before the Job's asynchronous accounting reaches zero.
    while (true)
    {
        accountingRead = QueryInformationJobObject(job, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr) != FALSE;
        if (!accountingRead || accounting.ActiveProcesses == 0 || GetTickCount64() >= accountingDeadline) break;
        Sleep(10);
    }
    const bool cleaned = accountingRead && accounting.ActiveProcesses == 0 && cleanupWait == WAIT_OBJECT_0;
    const bool passed = identityRead && ownershipRead && childOwned && cleaned && exitRead &&
        (failBeforeResume ? (injected == 0 && forcedCleanup && exitCode == ERROR_PROCESS_ABORTED) :
        (injected == 1 && resumed == 1 && readyWait == WAIT_OBJECT_0 && exitWait == WAIT_OBJECT_0 && !forcedCleanup && exitCode == 0));
    std::printf("{\"passed\":%s,\"mode\":\"startup-parent\",\"injected\":%lu,\"injectionError\":%lu,\"readyWait\":%lu,\"exitWait\":%lu,\"childExitCode\":%lu,\"expectedFailure\":%s,\"forcedCleanup\":%s,\"childJobActiveProcesses\":%lu,\"cleanupCompleted\":%s}\n",
        passed ? "true" : "false", injected, injectionError, readyWait, exitWait, exitCode,
        failBeforeResume ? "true" : "false", forcedCleanup ? "true" : "false", accounting.ActiveProcesses, cleaned ? "true" : "false");
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    CloseHandle(ready);
    CloseHandle(job);
    FreeLibrary(bootstrap);
    return passed ? 0 : 1;
}
