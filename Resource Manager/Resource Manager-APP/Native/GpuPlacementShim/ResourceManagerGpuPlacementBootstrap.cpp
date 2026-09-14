#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <detours.h>

#include <array>
#include <string>
#include <vector>

const GUID DETOUR_EXE_RESTORE_GUID = {
    0x2ed7a3ff, 0x3339, 0x4a8d,
    {0x80, 0x5c, 0xd4, 0x98, 0x15, 0x3f, 0xc2, 0x8f}};

namespace
{
constexpr DWORD ResultSuccess = 1;

DWORD AdjustExecuteProtection(DWORD oldProtection, DWORD requestedProtection)
{
    constexpr DWORD executeProtection = PAGE_EXECUTE | PAGE_EXECUTE_READ
        | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    constexpr DWORD nonExecuteProtection = PAGE_NOACCESS | PAGE_READONLY
        | PAGE_READWRITE | PAGE_WRITECOPY;
    constexpr DWORD attributes = ~(executeProtection | nonExecuteProtection);

    const bool wasExecutable = (oldProtection & executeProtection) != 0;
    const bool willBeExecutable = (requestedProtection & executeProtection) != 0;
    if (wasExecutable && !willBeExecutable)
    {
        return ((requestedProtection & nonExecuteProtection) << 4)
            | (requestedProtection & attributes);
    }
    if (!wasExecutable && willBeExecutable)
    {
        return ((requestedProtection & executeProtection) >> 4)
            | (requestedProtection & attributes);
    }
    return requestedProtection;
}

bool TryConvertLoaderPath(const wchar_t* path, std::string& result)
{
    if (path == nullptr || path[0] == L'\0')
    {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }

    std::array<wchar_t, 32768> fullPath{};
    const DWORD fullLength = GetFullPathNameW(
        path,
        static_cast<DWORD>(fullPath.size()),
        fullPath.data(),
        nullptr);
    if (fullLength == 0 || fullLength >= fullPath.size())
    {
        SetLastError(ERROR_FILENAME_EXCED_RANGE);
        return false;
    }

    const wchar_t* loaderPath = fullPath.data();
    std::array<wchar_t, 32768> shortPath{};
    const DWORD shortLength = GetShortPathNameW(
        fullPath.data(),
        shortPath.data(),
        static_cast<DWORD>(shortPath.size()));
    if (shortLength > 0 && shortLength < shortPath.size())
    {
        loaderPath = shortPath.data();
    }

    BOOL usedDefaultCharacter = FALSE;
    const int byteCount = WideCharToMultiByte(
        CP_ACP,
        WC_NO_BEST_FIT_CHARS,
        loaderPath,
        -1,
        nullptr,
        0,
        nullptr,
        &usedDefaultCharacter);
    if (byteCount <= 1 || usedDefaultCharacter)
    {
        SetLastError(ERROR_NO_UNICODE_TRANSLATION);
        return false;
    }

    std::vector<char> bytes(static_cast<size_t>(byteCount));
    usedDefaultCharacter = FALSE;
    if (WideCharToMultiByte(
            CP_ACP,
            WC_NO_BEST_FIT_CHARS,
            loaderPath,
            -1,
            bytes.data(),
            byteCount,
            nullptr,
            &usedDefaultCharacter) <= 0
        || usedDefaultCharacter)
    {
        SetLastError(ERROR_NO_UNICODE_TRANSLATION);
        return false;
    }

    result.assign(bytes.data());
    return true;
}
}

extern "C" BOOL WINAPI DetourVirtualProtectSameExecuteEx(
    HANDLE process,
    PVOID address,
    SIZE_T size,
    DWORD requestedProtection,
    PDWORD oldProtection)
{
    MEMORY_BASIC_INFORMATION memory{};
    if (VirtualQueryEx(process, address, &memory, sizeof(memory)) == 0)
    {
        return FALSE;
    }
    return VirtualProtectEx(
        process,
        address,
        size,
        AdjustExecuteProtection(memory.Protect, requestedProtection),
        oldProtection);
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementBootstrapUpdate(
    HANDLE process,
    const wchar_t* providerPath,
    DWORD* win32Error)
{
    if (win32Error != nullptr)
    {
        *win32Error = ERROR_SUCCESS;
    }
    if (process == nullptr || process == INVALID_HANDLE_VALUE)
    {
        if (win32Error != nullptr)
        {
            *win32Error = ERROR_INVALID_HANDLE;
        }
        SetLastError(ERROR_INVALID_HANDLE);
        return 0;
    }

    std::string providerLoaderPath;
    if (!TryConvertLoaderPath(providerPath, providerLoaderPath))
    {
        const DWORD error = GetLastError();
        if (win32Error != nullptr)
        {
            *win32Error = error;
        }
        return 0;
    }

    const LPCSTR injectedDlls[] = { providerLoaderPath.c_str() };
    if (!DetourUpdateProcessWithDll(
            process,
            injectedDlls,
            1))
    {
        const DWORD error = GetLastError();
        if (win32Error != nullptr)
        {
            *win32Error = error;
        }
        return 0;
    }

    return ResultSuccess;
}
