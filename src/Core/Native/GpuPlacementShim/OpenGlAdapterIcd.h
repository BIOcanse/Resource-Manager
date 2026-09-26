#pragma once
#include <windows.h>
#include <cstring>
#include <cwchar>

namespace ResourceManagerOpenGl
{
// Windows SDK d3dkmthk.h query ABI, shared with the callback preparation worker.
struct OpenFromLuid { LUID luid; UINT adapter; };
struct GlInfo { WCHAR filename[MAX_PATH]; ULONG version, flags; };
struct QueryInfo { UINT adapter; INT type; void* data; UINT size; };
static_assert(sizeof(OpenFromLuid) == 12 && sizeof(GlInfo) == 528);
static_assert(sizeof(QueryInfo) == (sizeof(void*) == 8 ? 24 : 16));
using OpenAdapter = LONG(WINAPI*)(OpenFromLuid*);
using CloseAdapter = LONG(WINAPI*)(const UINT*);
using QueryAdapter = LONG(WINAPI*)(const QueryInfo*);
using StatusError = ULONG(WINAPI*)(LONG);

struct AdapterIcdQueryResult
{
    DWORD error{}, cleanupError{};
    bool Succeeded() const noexcept { return !error && !cleanupError; }
};

// Explicit read only. No module loading, context creation, or driver registration.
inline AdapterIcdQueryResult QueryAdapterIcd(LUID selected, GlInfo& output)
{
    const DWORD priorError = GetLastError();
    const auto entry = [](HMODULE module, const char* name, auto& function) {
        const auto address = module ? GetProcAddress(module, name) : nullptr;
        static_assert(sizeof(function) == sizeof(address));
        std::memcpy(&function, &address, sizeof(function));
        return address != nullptr;
    };
    const auto gdi = GetModuleHandleW(L"gdi32.dll");
    const auto ntdll = GetModuleHandleW(L"ntdll.dll");
    OpenAdapter open{}; CloseAdapter close{}; QueryAdapter query{}; StatusError statusError{};
    AdapterIcdQueryResult result;
    if (!entry(gdi, "D3DKMTOpenAdapterFromLuid", open)
        || !entry(gdi, "D3DKMTCloseAdapter", close)
        || !entry(gdi, "D3DKMTQueryAdapterInfo", query)
        || !entry(ntdll, "RtlNtStatusToDosError", statusError)) {
        result.error = ERROR_PROC_NOT_FOUND;
    } else {
        OpenFromLuid adapter{selected, 0};
        const auto opened = open(&adapter);
        if (opened < 0) result.error = statusError(opened);
        else {
            GlInfo candidate{};
            const QueryInfo request{adapter.adapter, 2, &candidate, sizeof(candidate)};
            const auto queried = query(&request);
            const auto closed = close(&adapter.adapter);
            if (queried < 0) result.error = statusError(queried);
            if (closed < 0) result.cleanupError = statusError(closed);
            if (result.Succeeded() && (!candidate.filename[0] || !std::wmemchr(candidate.filename, L'\0', MAX_PATH)))
                result.error = ERROR_INVALID_DATA;
            if (result.Succeeded()) output = candidate;
        }
    }
    SetLastError(priorError);
    return result;
}
}
