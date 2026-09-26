#define RESOURCE_MANAGER_OPENGL_ROUTE_ENTRY UnusedFormatRouteEntry
#include "OpenGlContextRouteProbe.cpp"
#include <GL/wglext.h>
#include <MinHook.h>
#include <cstdlib>
#include <climits>

namespace {
Query inventoryOriginalQuery{};
GlInfo inventoryIcd{};
HMODULE inventoryOpenGl{};
unsigned inventorySelections{};
LONG WINAPI InventorySelect(const QueryInfo* request) {
    const void* caller = __builtin_return_address(0);
    const auto result = inventoryOriginalQuery(request);
    const auto error = GetLastError();
    MEMORY_BASIC_INFORMATION memory{};
    if (result >= 0 && request && request->type == 2 && request->data &&
        request->size == sizeof(GlInfo) && VirtualQuery(caller, &memory, sizeof(memory)) &&
        memory.AllocationBase == inventoryOpenGl) {
        std::memcpy(request->data, &inventoryIcd, sizeof(inventoryIcd));
        ++inventorySelections;
    }
    SetLastError(error);
    return result;
}
struct FormatKey { int value; const char* name; };
#define FORMAT_KEY(name) {name, #name}
std::vector<FormatKey> Keys(const std::string& extensions) {
    std::vector<FormatKey> result{
        FORMAT_KEY(WGL_DRAW_TO_WINDOW_ARB), FORMAT_KEY(WGL_DRAW_TO_BITMAP_ARB),
        FORMAT_KEY(WGL_ACCELERATION_ARB), FORMAT_KEY(WGL_NEED_PALETTE_ARB),
        FORMAT_KEY(WGL_NEED_SYSTEM_PALETTE_ARB), FORMAT_KEY(WGL_SWAP_LAYER_BUFFERS_ARB),
        FORMAT_KEY(WGL_SWAP_METHOD_ARB), FORMAT_KEY(WGL_NUMBER_OVERLAYS_ARB),
        FORMAT_KEY(WGL_NUMBER_UNDERLAYS_ARB), FORMAT_KEY(WGL_TRANSPARENT_ARB),
        FORMAT_KEY(WGL_SUPPORT_GDI_ARB), FORMAT_KEY(WGL_SUPPORT_OPENGL_ARB),
        FORMAT_KEY(WGL_DOUBLE_BUFFER_ARB), FORMAT_KEY(WGL_STEREO_ARB),
        FORMAT_KEY(WGL_PIXEL_TYPE_ARB), FORMAT_KEY(WGL_COLOR_BITS_ARB),
        FORMAT_KEY(WGL_RED_BITS_ARB), FORMAT_KEY(WGL_RED_SHIFT_ARB),
        FORMAT_KEY(WGL_GREEN_BITS_ARB), FORMAT_KEY(WGL_GREEN_SHIFT_ARB),
        FORMAT_KEY(WGL_BLUE_BITS_ARB), FORMAT_KEY(WGL_BLUE_SHIFT_ARB),
        FORMAT_KEY(WGL_ALPHA_BITS_ARB), FORMAT_KEY(WGL_ALPHA_SHIFT_ARB),
        FORMAT_KEY(WGL_ACCUM_BITS_ARB), FORMAT_KEY(WGL_ACCUM_RED_BITS_ARB),
        FORMAT_KEY(WGL_ACCUM_GREEN_BITS_ARB), FORMAT_KEY(WGL_ACCUM_BLUE_BITS_ARB),
        FORMAT_KEY(WGL_ACCUM_ALPHA_BITS_ARB), FORMAT_KEY(WGL_DEPTH_BITS_ARB),
        FORMAT_KEY(WGL_STENCIL_BITS_ARB), FORMAT_KEY(WGL_AUX_BUFFERS_ARB)};
    if (Has(extensions, "WGL_ARB_multisample") || Has(extensions, "WGL_EXT_multisample")) {
        result.push_back(FORMAT_KEY(WGL_SAMPLE_BUFFERS_ARB));
        result.push_back(FORMAT_KEY(WGL_SAMPLES_ARB));
    }
    if (Has(extensions, "WGL_ARB_framebuffer_sRGB") || Has(extensions, "WGL_EXT_framebuffer_sRGB"))
        result.push_back(FORMAT_KEY(WGL_FRAMEBUFFER_SRGB_CAPABLE_ARB));
    if (Has(extensions, "WGL_NV_multisample_coverage"))
        result.push_back(FORMAT_KEY(WGL_COLOR_SAMPLES_NV));
    if (Has(extensions, "WGL_EXT_depth_float"))
        result.push_back(FORMAT_KEY(WGL_DEPTH_FLOAT_EXT));
    return result;
}
#undef FORMAT_KEY

void Formats(HDC dc, const std::string& extensions) {
    Check(Has(extensions, "WGL_ARB_pixel_format"), "arb-format-advertised");
    const auto queryFormat = Extension<PFNWGLGETPIXELFORMATATTRIBIVARBPROC>("wglGetPixelFormatAttribivARB");
    Check(queryFormat != nullptr, "native-format-query-entry");
    const int countKey = WGL_NUMBER_PIXEL_FORMATS_ARB;
    int count{};
    Check(queryFormat(dc, 0, 0, 1, &countKey, &count) && count > 0 && count <= 8192, "bounded-native-format-count");
    const auto keys = Keys(extensions);
    std::vector<int> names, values(keys.size());
    for (const auto& key : keys) names.push_back(key.value);
    std::printf("{\"formatCount\":%d,\"selectedFormat\":%d,\"keys\":[", count, GetPixelFormat(dc));
    for (size_t i = 0; i < keys.size(); ++i)
        std::printf("%s{\"key\":%d,\"name\":%s}", i ? "," : "", keys[i].value, Json(keys[i].name).c_str());
    std::printf("]}\n");
    for (int offset = 0; offset < count; ++offset) {
        std::fill(values.begin(), values.end(), INT_MIN);
        SetLastError(0);
        const BOOL okay = queryFormat(dc, offset + 1, 0, static_cast<UINT>(names.size()), names.data(), values.data());
        const DWORD error = GetLastError();
        std::printf("{\"format\":%d,\"returned\":%s,\"error\":%lu,\"values\":[", offset + 1, okay ? "true" : "false", error);
        for (size_t i = 0; i < values.size(); ++i) std::printf("%s%d", i ? "," : "", values[i]);
        std::printf("]}\n");
        Check(okay != FALSE, "native-format-query-completed");
    }
    int after{};
    Check(queryFormat(dc, 0, 0, 1, &countKey, &after) && after == count, "native-format-count-stable");
}
}

int wmain(int argc, wchar_t** argv) {
    SetErrorMode(32771);
    try {
        Check(argc == 3, "output-and-target-luid");
        wchar_t* end{};
        const auto wanted = std::wcstoull(argv[2], &end, 10);
        Check(end && end != argv[2] && !*end && wanted, "exact-target-luid");
        FILETIME birth{}, exit{}, kernel{}, user{}; BOOL job{};
        Check(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user) &&
            IsProcessInJob(GetCurrentProcess(), nullptr, &job) && job, "owned-native-identity");
        std::printf("{\"pid\":%lu,\"creationFileTimeUtc\":%llu,\"errorMode\":%u,\"inJob\":true}\n",
            GetCurrentProcessId(), (static_cast<unsigned long long>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime, GetErrorMode());
        const HWND foreground = GetForegroundWindow();
        auto gdi = GetModuleHandleW(L"gdi32.dll");
        inventoryOpenGl = GetModuleHandleW(L"opengl32.dll");
        const auto open = SystemEntry<OpenLuid>(gdi, "D3DKMTOpenAdapterFromLuid");
        const auto read = SystemEntry<Query>(gdi, "D3DKMTQueryAdapterInfo");
        const auto close = SystemEntry<CloseAdapter>(gdi, "D3DKMTCloseAdapter");
        OpenFromLuid adapter{{static_cast<DWORD>(wanted), static_cast<LONG>(wanted >> 32)}, 0};
        Check(open(&adapter) >= 0, "open-selected-adapter");
        QueryInfo request{adapter.adapter, 2, &inventoryIcd, sizeof(inventoryIcd)};
        const auto queried = read(&request), closed = close(&adapter.adapter);
        Check(queried >= 0 && closed >= 0 && inventoryIcd.filename[0] &&
            std::wmemchr(inventoryIcd.filename, 0, MAX_PATH), "read-icd-and-close-adapter");
        std::printf("{\"requestedAdapterLuid\":%llu,\"requestedIcd\":%s,\"version\":%lu}\n",
            wanted, Json(Utf8(inventoryIcd.filename).c_str()).c_str(), inventoryIcd.version);
        Check(MH_Initialize() == MH_OK && MH_CreateHook(reinterpret_cast<void*>(read),
            reinterpret_cast<void*>(&InventorySelect), reinterpret_cast<void**>(&inventoryOriginalQuery)) == MH_OK &&
            MH_EnableHook(reinterpret_cast<void*>(read)) == MH_OK, "owned-process-selection-hook");
        WNDCLASSW wc{}; wc.style = CS_OWNDC; wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = ClassName;
        Check(RegisterClassW(&wc) != 0, "register-owned-hidden-class");
        {
            Window window;
            Context context(window.dc);
            Check(wglMakeCurrent(window.dc, context.context), "own-context-bound");
            using GetBytes = void(APIENTRY*)(GLenum, GLubyte*);
            const auto bytes = Extension<GetBytes>("glGetUnsignedBytevEXT");
            Check(bytes != nullptr, "actual-render-luid-entry");
            LUID actual{}; bytes(0x9599, reinterpret_cast<GLubyte*>(&actual));
            Check(glGetError() == GL_NO_ERROR && Value(actual) == wanted, "actual-render-luid-matches");
            const auto extensionsEntry = Extension<PFNWGLGETEXTENSIONSSTRINGARBPROC>("wglGetExtensionsStringARB");
            Check(extensionsEntry != nullptr, "native-extensions-entry");
            const char* extensionText = extensionsEntry(window.dc);
            Check(extensionText != nullptr, "native-extensions-returned");
            const std::string extensions(extensionText);
            std::printf("{\"renderLuid\":%llu,\"renderer\":%s,\"extensions\":%s}\n", Value(actual),
                Json(reinterpret_cast<const char*>(glGetString(GL_RENDERER))).c_str(), Json(extensions.c_str()).c_str());
            Formats(window.dc, extensions);
            Draw(window, context, argv[1], "inventory", false);
            context.Close();
            Check(ReleaseDC(window.window, window.dc) == 1, "own-dc-released");
            window.dc = nullptr;
            Check(DestroyWindow(window.window), "own-window-destroyed");
            window.window = nullptr;
        }
        Check(UnregisterClassW(ClassName, wc.hInstance), "hidden-window-dc-context-cleaned");
        Check(MH_DisableHook(reinterpret_cast<void*>(read)) == MH_OK &&
            MH_RemoveHook(reinterpret_cast<void*>(read)) == MH_OK && MH_Uninitialize() == MH_OK, "owned-hook-removed");
        Check(inventorySelections > 0 && GetForegroundWindow() == foreground, "selection-observed-foreground-unchanged");
        std::printf("{\"passed\":true,\"checks\":%u,\"registryChanged\":false,\"deployed\":false,\"runtimeMigrationVerified\":false}\n", checks);
        return 0;
    } catch (const std::exception& error) {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":%s}\n", checks, Json(error.what()).c_str());
        return 1;
    }
}
