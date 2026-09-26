#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cerrno>
#include <climits>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cwchar>

namespace
{
struct ProcessHandle
{
    HANDLE value;
    ~ProcessHandle() { if (value != nullptr) CloseHandle(value); }
};

bool ParseUnsigned(const wchar_t* text, unsigned long long& value)
{
    if (text == nullptr || *text == 0) return false;
    const size_t length = std::wcslen(text);
    if (length > 20) return false;
    for (size_t index = 0; index < length; ++index)
        if (text[index] < L'0' || text[index] > L'9') return false;
    errno = 0;
    wchar_t* end = nullptr;
    value = std::wcstoull(text, &end, 10);
    return errno == 0 && end != nullptr && *end == 0;
}

unsigned long long CreationTime(HANDLE process)
{
    FILETIME creation{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(process, &creation, &exit, &kernel, &user)) return 0;
    return (static_cast<unsigned long long>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;
}

bool Owns(HWND window, HANDLE process, DWORD processId)
{
    DWORD actual = 0;
    return WaitForSingleObject(process, 0) == WAIT_TIMEOUT
        && GetWindowThreadProcessId(window, &actual) != 0 && actual == processId;
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 4) return ERROR_INVALID_PARAMETER;
    unsigned long long rawWindow = 0, rawPid = 0, expectedCreation = 0;
    if (!ParseUnsigned(argv[1], rawWindow) || !ParseUnsigned(argv[2], rawPid)
        || !ParseUnsigned(argv[3], expectedCreation) || rawWindow == 0 || rawPid <= 4
        || rawPid > MAXDWORD || expectedCreation == 0) return ERROR_INVALID_PARAMETER;
    const HWND window = reinterpret_cast<HWND>(static_cast<uintptr_t>(rawWindow));
    const DWORD processId = static_cast<DWORD>(rawPid);
    ProcessHandle process{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, processId)};
    if (process.value == nullptr) return static_cast<int>(GetLastError());
    if (CreationTime(process.value) != expectedCreation || !Owns(window, process.value, processId))
        return ERROR_INVALID_HANDLE;
    RECT before{};
    if (!GetWindowRect(window, &before)) return static_cast<int>(GetLastError());
    const int64_t width = static_cast<int64_t>(before.right) - before.left;
    const int64_t height = static_cast<int64_t>(before.bottom) - before.top;
    if (width <= 0 || width >= INT_MAX || height <= 0 || height > INT_MAX) return ERROR_INVALID_PARAMETER;

    std::printf("{\"ready\":true,\"pid\":%lu,\"filetime\":%llu,\"targetPid\":%lu,\"width\":%lld,\"height\":%lld}\n",
        GetCurrentProcessId(), CreationTime(GetCurrentProcess()), processId,
        static_cast<long long>(width), static_cast<long long>(height));
    std::fflush(stdout);
    const UINT flags = SWP_NOMOVE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE;
    const BOOL changed = SetWindowPos(window, nullptr, 0, 0, static_cast<int>(width + 1), static_cast<int>(height), flags);
    const DWORD changeError = changed ? ERROR_SUCCESS : GetLastError();
    std::printf("{\"resizeAccepted\":%s,\"error\":%lu}\n", changed ? "true" : "false", changeError);
    std::fflush(stdout);
    if (!changed) return static_cast<int>(changeError == 0 ? ERROR_GEN_FAILURE : changeError);
    if (!Owns(window, process.value, processId)) return ERROR_INVALID_HANDLE;
    const BOOL restored = SetWindowPos(window, nullptr, 0, 0, static_cast<int>(width), static_cast<int>(height), flags);
    const DWORD restoreError = restored ? ERROR_SUCCESS : GetLastError();
    RECT after{};
    const bool confirmed = restored && GetWindowRect(window, &after)
        && after.left == before.left && after.top == before.top
        && after.right == before.right && after.bottom == before.bottom;
    std::printf("{\"restoreAccepted\":%s,\"restored\":%s,\"error\":%lu}\n",
        restored ? "true" : "false", confirmed ? "true" : "false", restoreError);
    std::fflush(stdout);
    return confirmed ? ERROR_SUCCESS : static_cast<int>(restoreError == 0 ? ERROR_GEN_FAILURE : restoreError);
}
