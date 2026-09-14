#define RESOURCE_MANAGER_OPENGL_ROUTE_ENTRY RunOwnedOpenGlRoutes
#include "OpenGlContextRouteProbe.cpp"
#include <MinHook.h>
#include <atomic>
#include <cstdlib>

namespace {
Query originalSelectionQuery{};
GlInfo requestedIcd{};
HMODULE systemOpenGl{};
std::atomic<unsigned> replacements{0};
LONG WINAPI SelectIcd(const QueryInfo* request) {
    const void* caller = __builtin_return_address(0);
    const LONG result = originalSelectionQuery(request);
    MEMORY_BASIC_INFORMATION memory{};
    if (result >= 0 && request && request->type == 2 && request->data && request->size == sizeof(GlInfo)
        && VirtualQuery(caller, &memory, sizeof(memory)) && memory.AllocationBase == systemOpenGl) {
        const unsigned count = replacements.fetch_add(1) + 1;
        std::memcpy(request->data, &requestedIcd, sizeof(requestedIcd));
        std::printf("{\"icdReplacement\":%u,\"adapterHandle\":%u,\"nativeResult\":%ld}\n", count, request->adapter, result);
        std::fflush(stdout);
    }
    return result;
}
}

int wmain(int argc, wchar_t** argv) {
    SetErrorMode(32771);
    try {
        Check(argc == 3, "owned-output-and-explicit-target-luid");
        wchar_t* end = nullptr;
        const auto value = std::wcstoull(argv[2], &end, 10);
        Check(end && end != argv[2] && *end == L'\0' && value != 0, "exact-target-luid");
        HMODULE gdi = GetModuleHandleW(L"gdi32.dll");
        systemOpenGl = GetModuleHandleW(L"opengl32.dll");
        Check(gdi && systemOpenGl, "original-system-modules");
        const auto open = SystemEntry<OpenLuid>(gdi, "D3DKMTOpenAdapterFromLuid");
        const auto read = SystemEntry<Query>(gdi, "D3DKMTQueryAdapterInfo");
        const auto close = SystemEntry<CloseAdapter>(gdi, "D3DKMTCloseAdapter");
        OpenFromLuid adapter{{static_cast<DWORD>(value), static_cast<LONG>(value >> 32)}, 0};
        Check(open(&adapter) >= 0, "actual-target-adapter-opened");
        const QueryInfo request{adapter.adapter, 2, &requestedIcd, sizeof(requestedIcd)};
        const auto readStatus = read(&request);
        const auto closeStatus = close(&adapter.adapter);
        Check(readStatus >= 0 && closeStatus >= 0 && requestedIcd.filename[0]
            && std::find(std::begin(requestedIcd.filename), std::end(requestedIcd.filename), L'\0') != std::end(requestedIcd.filename), "actual-target-icd-read-and-handle-closed");
        std::printf("{\"requestedAdapterLuid\":%llu,\"requestedIcd\":%s,\"version\":%lu,\"flags\":%lu}\n",
            value, Json(Utf8(requestedIcd.filename).c_str()).c_str(), requestedIcd.version, requestedIcd.flags);
        Check(MH_Initialize() == MH_OK && MH_CreateHook(reinterpret_cast<void*>(read), reinterpret_cast<void*>(&SelectIcd),
            reinterpret_cast<void**>(&originalSelectionQuery)) == MH_OK && MH_EnableHook(reinterpret_cast<void*>(read)) == MH_OK, "owned-icd-candidate-installed");
        const int result = RunOwnedOpenGlRoutes(2, argv);
        std::printf("{\"selectionExperimentCompleted\":true,\"replacementCount\":%u,\"productionIntegration\":false,\"runtimeMigrationVerified\":false}\n", replacements.load());
        return result == 0 && replacements.load() > 0 ? 0 : 1;
    } catch (const std::exception& error) {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
