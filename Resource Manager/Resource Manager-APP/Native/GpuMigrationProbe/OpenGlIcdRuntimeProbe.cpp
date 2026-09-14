#define RESOURCE_MANAGER_OPENGL_ROUTE_ENTRY UnusedOwnedOpenGlRoutes
#include "OpenGlContextRouteProbe.cpp"
#include <MinHook.h>
#include <atomic>
#include <cstdlib>

namespace {
Query runtimeOriginal{};
HMODULE runtimeOpenGl{};
std::atomic<const GlInfo*> selectedIcd{nullptr};
std::atomic<unsigned> runtimeReplacements{0};
LONG WINAPI RuntimeIcd(const QueryInfo* request) {
    const void* caller = __builtin_return_address(0);
    const LONG result = runtimeOriginal(request);
    MEMORY_BASIC_INFORMATION memory{};
    const GlInfo* selected = selectedIcd.load();
    if (selected && result >= 0 && request && request->type == 2 && request->data && request->size == sizeof(GlInfo)
        && VirtualQuery(caller, &memory, sizeof(memory)) && memory.AllocationBase == runtimeOpenGl) {
        std::memcpy(request->data, selected, sizeof(*selected));
        std::printf("{\"runtimeIcdReplacement\":%u,\"icd\":%s}\n", runtimeReplacements.fetch_add(1) + 1,
            Json(Utf8(selected->filename).c_str()).c_str());
        std::fflush(stdout);
    }
    return result;
}
GlInfo ActualIcd(unsigned long long value) {
    OpenFromLuid adapter{{static_cast<DWORD>(value), static_cast<LONG>(value >> 32)}, 0};
    Check(openLuid(&adapter) >= 0, "actual-runtime-adapter-opened");
    GlInfo info{};
    const QueryInfo request{adapter.adapter, 2, &info, sizeof(info)};
    const auto result = query(&request);
    const auto closed = closeAdapter(&adapter.adapter);
    Check(result >= 0 && closed >= 0 && info.filename[0]
        && std::find(std::begin(info.filename), std::end(info.filename), L'\0') != std::end(info.filename), "actual-runtime-icd-and-close");
    std::printf("{\"icdSourceLuid\":%llu,\"path\":%s}\n", value, Json(Utf8(info.filename).c_str()).c_str());
    return info;
}
void RenderIdentity(const char* phase, unsigned long long requested) {
    const auto raw = reinterpret_cast<const char*>(glGetString(GL_EXTENSIONS));
    Check(raw && glGetError() == GL_NO_ERROR, "actual-gl-extension-list");
    const bool supported = Has(raw, "GL_EXT_memory_object_win32") || Has(raw, "GL_EXT_semaphore_win32");
    if (!supported) {
        std::printf("{\"identityPhase\":%s,\"requestedLuid\":%llu,\"renderLuidAvailable\":false}\n", Json(phase).c_str(), requested);
        return;
    }
    using GetBytes = void(APIENTRY*)(GLenum, GLubyte*);
    const auto get = Extension<GetBytes>("glGetUnsignedBytevEXT");
    Check(get != nullptr, "advertised-luid-entry");
    LUID actual{}; GLint nodes = 0;
    get(0x9599, reinterpret_cast<GLubyte*>(&actual));
    const GLenum luidError = glGetError();
    glGetIntegerv(0x959A, &nodes);
    const GLenum nodeError = glGetError();
    Check(luidError == GL_NO_ERROR, "actual-context-luid-query");
    const std::string node = nodeError == GL_NO_ERROR ? std::to_string(nodes) : "null";
    std::printf("{\"identityPhase\":%s,\"requestedLuid\":%llu,\"renderLuidAvailable\":true,\"renderLuid\":%llu,\"nodeMask\":%s,\"nodeMaskError\":%u}\n",
        Json(phase).c_str(), requested, Value(actual), node.c_str(), nodeError);
}
void Observe(Window& window, Context& context, const wchar_t* root, const char* phase, unsigned long long requested) {
    Draw(window, context, root, phase, false);
    RenderIdentity(phase, requested);
    std::printf("{\"replacementCountPhase\":%s,\"replacementCount\":%u}\n", Json(phase).c_str(), runtimeReplacements.load());
}
}

int wmain(int argc, wchar_t** argv) {
    SetErrorMode(32771);
    try {
        Check(argc == 4, "output-and-two-explicit-luids");
        unsigned long long luids[2]{};
        for (unsigned i = 0; i < 2; ++i) {
            wchar_t* end = nullptr; luids[i] = std::wcstoull(argv[i + 2], &end, 10);
            Check(end && end != argv[i + 2] && !*end && luids[i], "explicit-runtime-luid");
        }
        Check(luids[0] != luids[1], "distinct-runtime-adapters");
        FILETIME birth{}, exit{}, kernel{}, user{}; BOOL job = FALSE;
        Check(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user)
            && IsProcessInJob(GetCurrentProcess(), nullptr, &job) && job, "owned-native-identity");
        std::printf("{\"pid\":%lu,\"creationFileTimeUtc\":%llu,\"errorMode\":%u,\"inJob\":true}\n", GetCurrentProcessId(),
            (static_cast<unsigned long long>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime, GetErrorMode());
        const HMODULE gdi = GetModuleHandleW(L"gdi32.dll"); runtimeOpenGl = GetModuleHandleW(L"opengl32.dll");
        Check(gdi && runtimeOpenGl, "original-system-modules");
        openLuid = SystemEntry<OpenLuid>(gdi, "D3DKMTOpenAdapterFromLuid");
        query = SystemEntry<Query>(gdi, "D3DKMTQueryAdapterInfo");
        closeAdapter = SystemEntry<CloseAdapter>(gdi, "D3DKMTCloseAdapter");
        // Immutable inputs outlive all hook calls through owned process exit.
        static const GlInfo first = ActualIcd(luids[0]);
        static const GlInfo second = ActualIcd(luids[1]);
        selectedIcd.store(&first);
        Check(MH_Initialize() == MH_OK && MH_CreateHook(reinterpret_cast<void*>(query), reinterpret_cast<void*>(&RuntimeIcd),
            reinterpret_cast<void**>(&runtimeOriginal)) == MH_OK && MH_EnableHook(reinterpret_cast<void*>(query)) == MH_OK, "owned-runtime-hook-installed");
        WNDCLASSW wc{}; wc.style = CS_OWNDC; wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = ClassName;
        Check(RegisterClassW(&wc), "owned-window-class");
        {
            Window oldWindow; Context oldContext(oldWindow.dc);
            Observe(oldWindow, oldContext, argv[1], "initial-a", luids[0]);
            selectedIcd.store(&second);
            {
                Window newWindow; Context newContext(newWindow.dc);
                Observe(newWindow, newContext, argv[1], "new-window-b-old-a-alive", luids[1]);
                newContext.Close();
            }
            Observe(oldWindow, oldContext, argv[1], "retained-a", luids[0]);
            oldContext.Close();
        }
        {
            Window window; Context context(window.dc);
            Observe(window, context, argv[1], "all-released-new-b", luids[1]); context.Close();
        }
        selectedIcd.store(&first);
        {
            Window window; Context context(window.dc);
            Observe(window, context, argv[1], "return-new-a", luids[0]); context.Close();
        }
        Check(UnregisterClassW(ClassName, GetModuleHandleW(nullptr)), "all-owned-windows-destroyed");
        std::printf("{\"passed\":true,\"checks\":%u,\"runtimeObservationCompleted\":true,\"contextsObserved\":5,\"replacementCount\":%u,\"productionIntegration\":false}\n", checks, runtimeReplacements.load());
        return 0;
    } catch (const std::exception& error) {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what()); return 1;
    }
}
