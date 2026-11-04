#define RESOURCE_MANAGER_OPENGL_ROUTE_ENTRY RunOwnedOpenGlRoutes
#include "OpenGlContextRouteProbe.cpp"
#include <MinHook.h>
#include <atomic>

namespace {
Query originalQuery{};
std::atomic<unsigned> queryCalls{0};
LONG WINAPI ObserveQuery(const QueryInfo* request) {
    const void* caller = __builtin_return_address(0);
    const LONG result = originalQuery(request);
    const unsigned sequence = queryCalls.fetch_add(1) + 1;
    if (sequence <= 512) {
        MEMORY_BASIC_INFORMATION memory{};
        char module[32768]{};
        if (VirtualQuery(caller, &memory, sizeof(memory)))
            GetModuleFileNameA(static_cast<HMODULE>(memory.AllocationBase), module, sizeof(module));
        char filename[2048]{};
        if (request && request->type == 2 && result >= 0 && request->data && request->size >= sizeof(GlInfo)) {
            const auto info = static_cast<const GlInfo*>(request->data);
            if (std::find(std::begin(info->filename), std::end(info->filename), L'\0') != std::end(info->filename))
                WideCharToMultiByte(CP_UTF8, 0, info->filename, -1, filename, sizeof(filename), nullptr, nullptr);
        }
        std::printf("{\"querySequence\":%u,\"threadId\":%lu,\"callerModule\":%s,\"adapterHandle\":%u,\"queryType\":%d,\"result\":%ld,\"icdPath\":%s}\n",
            sequence, GetCurrentThreadId(), Json(module).c_str(), request ? request->adapter : 0,
            request ? request->type : -1, result, Json(filename).c_str());
        std::fflush(stdout);
    }
    return result;
}
}

int wmain(int argc, wchar_t** argv) {
    SetErrorMode(32771);
    const auto module = GetModuleHandleW(L"gdi32.dll");
    const auto entry = module ? GetProcAddress(module, "D3DKMTQueryAdapterInfo") : nullptr;
    if (!entry || MH_Initialize() != MH_OK
        || MH_CreateHook(reinterpret_cast<void*>(entry), reinterpret_cast<void*>(&ObserveQuery), reinterpret_cast<void**>(&originalQuery)) != MH_OK
        || MH_EnableHook(reinterpret_cast<void*>(entry)) != MH_OK) {
        std::fprintf(stderr, "Owned read-only query observation could not be installed.\n");
        return 1;
    }
    const int result = RunOwnedOpenGlRoutes(argc, argv);
    // No callback code or trampoline is unloaded under a driver's live call.
    const unsigned calls = queryCalls.load();
    std::printf("{\"queryTraceCompleted\":true,\"calls\":%u,\"overflow\":%s,\"selectionChanged\":false,\"hooksEndWithOwnedProcess\":true}\n", calls, calls > 512 ? "true" : "false");
    return result == 0 && calls <= 512 ? 0 : 1;
}
