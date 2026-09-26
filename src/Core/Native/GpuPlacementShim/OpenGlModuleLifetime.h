#pragma once
#include <windows.h>
#include <cstdint>

namespace ResourceManagerOpenGl
{
// Explicit installation operation. It never loads a module and cannot be undone.
inline BOOL PinEntryModule(PROC entry)
{
    const DWORD error = GetLastError();
    const auto address = reinterpret_cast<INT_PTR>(entry);
    if (!address || address == 1 || address == 2 || address == 3 || address == -1) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    HMODULE module{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(entry), &module)) return FALSE;
    SetLastError(error);
    return TRUE;
}
}
