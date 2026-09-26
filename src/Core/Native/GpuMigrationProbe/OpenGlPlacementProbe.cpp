#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <GL/gl.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
unsigned checks = 0;
constexpr wchar_t ClassName[] = L"ResourceManager.OwnedOpenGlProbe";
constexpr wchar_t PreferenceKey[] = L"Software\\Microsoft\\DirectX\\UserGpuPreferences";
void Check(bool ok, const char* label)
{
    ++checks;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", label, ok ? "true" : "false");
    std::fflush(stdout);
    if (!ok) throw std::runtime_error(label);
}
std::string Json(const char* text)
{
    std::string result = "\"";
    if (text) for (const unsigned char* c = reinterpret_cast<const unsigned char*>(text); *c; ++c)
    {
        if (*c == '"' || *c == '\\') { result += '\\'; result += static_cast<char>(*c); }
        else if (*c < 32) { char encoded[7]{}; std::snprintf(encoded, sizeof(encoded), "\\u%04x", *c); result += encoded; }
        else result += static_cast<char>(*c);
    }
    return result + '"';
}
template<typename T> T Extension(const char* name)
{
    const auto entry = wglGetProcAddress(name);
    const auto value = reinterpret_cast<uintptr_t>(entry);
    T result = nullptr;
    if (value > 3 && value != UINTPTR_MAX) std::memcpy(&result, &entry, sizeof(result));
    return result;
}
bool Has(const std::string& text, const char* name)
{
    return (" " + text + " ").find(" " + std::string(name) + " ") != std::string::npos;
}
struct Window
{
    HWND window{};
    HDC dc{};
    int format = 0;
    Window(int x = 0, int y = 0)
    {
        window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, ClassName, L"Owned OpenGL probe",
            WS_POPUP | WS_CLIPSIBLINGS | WS_CLIPCHILDREN, x, y, 32, 32, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        Check(window != nullptr && !IsWindowVisible(window), "owned-hidden-window");
        dc = GetDC(window);
        Check(dc != nullptr, "window-client-dc");
        PIXELFORMATDESCRIPTOR desired{};
        desired.nSize = sizeof(desired);
        desired.nVersion = 1;
        desired.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
        desired.iPixelType = PFD_TYPE_RGBA;
        desired.cColorBits = 24;
        desired.cAlphaBits = 8;
        desired.iLayerType = PFD_MAIN_PLANE;
        format = ChoosePixelFormat(dc, &desired);
        Check(format != 0 && SetPixelFormat(dc, format, &desired), "pixel-format-set-once");
    }
    ~Window() { if (dc) ReleaseDC(window, dc); if (window) DestroyWindow(window); }
};
struct Context
{
    HGLRC context{};
    explicit Context(HDC dc, bool attributes = false, HGLRC shared = nullptr) {
        if (attributes) {
            using Create = HGLRC(WINAPI*)(HDC, HGLRC, const int*);
            const auto create = Extension<Create>("wglCreateContextAttribsARB");
            Check(create != nullptr, "attributes-entry-present");
            const int request[] = {0x2091, 3, 0x2092, 3, 0x9126, 2, 0};
            context = create(dc, shared, request);
        } else context = wglCreateContext(dc);
        Check(context != nullptr, attributes ? "attributes-context-created" : "ordinary-context-created");
    }
    ~Context() { if (context) { if (wglGetCurrentContext() == context) wglMakeCurrent(nullptr, nullptr); wglDeleteContext(context); } }
    void Close() { Check(wglMakeCurrent(nullptr, nullptr) && wglDeleteContext(context), "context-normally-deleted"); context = nullptr; }
};
void Entry(const char* name)
{
    const auto function = Extension<PROC>(name);
    MEMORY_BASIC_INFORMATION memory{};
    char module[32768]{};
    if (function && VirtualQuery(reinterpret_cast<const void*>(function), &memory, sizeof(memory)))
        GetModuleFileNameA(static_cast<HMODULE>(memory.AllocationBase), module, sizeof(module));
    std::printf("{\"entry\":%s,\"address\":\"%p\",\"module\":%s}\n", Json(name).c_str(),
        reinterpret_cast<void*>(function), Json(module).c_str());
}
void Inventory(HDC dc, HGLRC context, const char* phase)
{
    using Extensions = const char*(WINAPI*)(HDC);
    const auto getExtensions = Extension<Extensions>("wglGetExtensionsStringARB");
    const auto text = getExtensions ? getExtensions(dc) : nullptr;
    const std::string extensions = text ? text : "";
    std::printf("{\"phase\":%s,\"wglExtensions\":%s}\n", Json(phase).c_str(), Json(text).c_str());
    for (const char* name : {"wglCreateContextAttribsARB", "wglEnumGpusNV", "wglCreateAffinityDCNV",
        "wglGetGPUIDsAMD", "wglGetContextGPUIDAMD", "wglCreateAssociatedContextAMD"}) Entry(name);
    if (Has(extensions, "WGL_AMD_gpu_association"))
    {
        using Ids = UINT(WINAPI*)(UINT, UINT*);
        using Info = INT(WINAPI*)(UINT, INT, GLenum, UINT, void*);
        using ContextId = UINT(WINAPI*)(HGLRC);
        const auto ids = Extension<Ids>("wglGetGPUIDsAMD");
        const auto info = Extension<Info>("wglGetGPUInfoAMD");
        const auto contextId = Extension<ContextId>("wglGetContextGPUIDAMD");
        Check(ids && info && contextId, "advertised-amd-entrypoints");
        const UINT size = ids(0, nullptr);
        Check(size <= 16, "bounded-amd-inventory");
        std::vector<UINT> values(size);
        Check(size == 0 || ids(size, values.data()) == size, "complete-amd-inventory");
        std::printf("{\"phase\":%s,\"amdCurrentGpuId\":%u,\"gpuCount\":%u}\n", Json(phase).c_str(), contextId(context), size);
        for (UINT id : values)
        {
            char renderer[1024]{};
            const auto result = info(id, 0x1F01, GL_UNSIGNED_BYTE, sizeof(renderer), renderer);
            Check(result >= 0 && result < static_cast<int>(sizeof(renderer)), "amd-renderer-complete");
            std::printf("{\"phase\":%s,\"amdGpuId\":%u,\"renderer\":%s}\n", Json(phase).c_str(), id, Json(renderer).c_str());
        }
    }
    if (Has(extensions, "WGL_NV_gpu_affinity"))
    {
        using Enumerate = BOOL(WINAPI*)(UINT, HANDLE*);
        const auto enumerate = Extension<Enumerate>("wglEnumGpusNV");
        Check(enumerate != nullptr, "advertised-nv-enumerator");
        UINT index = 0;
        HANDLE gpu = nullptr;
        while (index < 16 && enumerate(index, &gpu))
        {
            std::printf("{\"phase\":%s,\"nvGpuIndex\":%u,\"handle\":\"%p\"}\n", Json(phase).c_str(), index, gpu);
            ++index;
        }
        Check(index < 16, "bounded-nv-inventory");
    }
}
void Draw(Window& window, Context& context, const wchar_t* directory, const char* phase, bool green)
{
    Check(wglMakeCurrent(window.dc, context.context), "context-made-current");
    const auto renderer = reinterpret_cast<const char*>(glGetString(GL_RENDERER));
    const auto vendor = reinterpret_cast<const char*>(glGetString(GL_VENDOR));
    const auto version = reinterpret_cast<const char*>(glGetString(GL_VERSION));
    Check(renderer && vendor && version, "real-gl-strings");
    PIXELFORMATDESCRIPTOR format{};
    Check(DescribePixelFormat(window.dc, GetPixelFormat(window.dc), sizeof(format), &format) != 0, "actual-format-description");
    std::printf("{\"phase\":%s,\"renderer\":%s,\"vendor\":%s,\"version\":%s,\"hwnd\":\"%p\",\"hdc\":\"%p\",\"hglrc\":\"%p\",\"pixelFormat\":%d,\"pixelFlags\":%lu}\n",
        Json(phase).c_str(), Json(renderer).c_str(), Json(vendor).c_str(), Json(version).c_str(),
        window.window, window.dc, context.context, GetPixelFormat(window.dc), format.dwFlags);
    glViewport(0, 0, 16, 16);
    glDisable(GL_DITHER);
    glDrawBuffer(GL_BACK);
    glReadBuffer(GL_BACK);
    glClearColor(1.0f, green ? 1.0f : 0.0f, 1.0f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);
    glFinish();
    std::array<unsigned char, 16 * 16 * 4> pixels{};
    glReadPixels(0, 0, 16, 16, GL_RGBA, GL_UNSIGNED_BYTE, pixels.data());
    Check(glGetError() == GL_NO_ERROR, "gl-draw-readback-no-error");
    bool matched = true;
    for (size_t i = 0; i < pixels.size(); i += 4)
        matched = matched && pixels[i] == 255 && pixels[i + 1] == (green ? 255 : 0) && pixels[i + 2] == 255 && pixels[i + 3] == 255;
    Check(matched, "all-256-real-pixels-match");
    std::wstring filename = std::wstring(directory) + L"/";
    for (const char* c = phase; *c; ++c) filename += static_cast<wchar_t>(*c);
    filename += L".rgba";
    const HANDLE file = CreateFileW(filename.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    Check(file != INVALID_HANDLE_VALUE, "fresh-pixel-file");
    DWORD written = 0;
    const bool saved = WriteFile(file, pixels.data(), static_cast<DWORD>(pixels.size()), &written, nullptr) && written == pixels.size();
    const bool closed = CloseHandle(file);
    Check(saved && closed, "pixels-saved");
    Check(SwapBuffers(window.dc), "hidden-window-swap-returned");
    Inventory(window.dc, context.context, phase);
    std::fflush(stdout);
}
void ChangeOwnPreference(const wchar_t* expected, const wchar_t* replacement)
{
    wchar_t path[32768]{};
    Check(GetModuleFileNameW(nullptr, path, 32768) > 0, "own-executable-path");
    HKEY key = nullptr;
    Check(RegOpenKeyExW(HKEY_CURRENT_USER, PreferenceKey, 0, KEY_QUERY_VALUE | KEY_SET_VALUE, &key) == ERROR_SUCCESS,
        "owned-preference-key-opened");
    wchar_t current[128]{};
    DWORD bytes = sizeof(current), kind = 0;
    const auto read = RegQueryValueExW(key, path, nullptr, &kind, reinterpret_cast<BYTE*>(current), &bytes);
    const bool matches = read == ERROR_SUCCESS && kind == REG_SZ && bytes <= sizeof(current) &&
        bytes >= sizeof(wchar_t) && current[(bytes / sizeof(wchar_t)) - 1] == 0 && std::wcscmp(current, expected) == 0;
    const auto result = matches ? RegSetValueExW(key, path, 0, REG_SZ, reinterpret_cast<const BYTE*>(replacement),
        static_cast<DWORD>((std::wcslen(replacement) + 1) * sizeof(wchar_t))) : ERROR_INVALID_DATA;
    RegCloseKey(key);
    Check(matches && result == ERROR_SUCCESS, "only-own-expected-preference-changed");
}
}

int wmain(int argc, wchar_t** argv)
{
    SetErrorMode(32771);
    try
    {
        Check(argc == 3 && (std::wcscmp(argv[2], L"1") == 0 || std::wcscmp(argv[2], L"2") == 0), "output-and-initial-preference");
        FILETIME birth{}, exit{}, kernel{}, user{};
        BOOL inJob = FALSE;
        Check(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user) &&
            IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) && inJob, "owned-native-identity");
        std::printf("{\"pid\":%lu,\"creationFileTimeUtc\":%llu,\"errorMode\":%u,\"inJob\":true}\n",
            GetCurrentProcessId(), (static_cast<unsigned long long>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime, GetErrorMode());
        WNDCLASSW windowClass{};
        windowClass.style = CS_OWNDC;
        windowClass.lpfnWndProc = DefWindowProcW;
        windowClass.hInstance = GetModuleHandleW(nullptr);
        windowClass.lpszClassName = ClassName;
        Check(RegisterClassW(&windowClass) != 0, "private-window-class");
        const bool low = argv[2][0] == L'1';
        const auto initial = low ? L"GpuPreference=1;" : L"GpuPreference=2;";
        const auto changed = low ? L"GpuPreference=2;" : L"GpuPreference=1;";
        {
            Window window;
            Context original(window.dc);
            Draw(window, original, argv[1], "initial", false);
            ChangeOwnPreference(initial, changed);
            Draw(window, original, argv[1], "old-after-change", true);
            {
                Context newContext(window.dc);
                Draw(window, newContext, argv[1], "new-context-old-dc", false);
                newContext.Close();
            }
            Check(ReleaseDC(window.window, window.dc) == 1, "old-window-dc-released");
            window.dc = GetDC(window.window);
            Check(window.dc && GetPixelFormat(window.dc) == window.format, "reacquired-dc-retains-window-format");
            {
                Context reacquired(window.dc);
                Draw(window, reacquired, argv[1], "new-context-reacquired-dc", true);
                reacquired.Close();
            }
            {
                Window fresh;
                Context freshContext(fresh.dc);
                Draw(fresh, freshContext, argv[1], "new-window", false);
                freshContext.Close();
            }
            Draw(window, original, argv[1], "retained-original", true);
            ChangeOwnPreference(changed, initial);
            original.Close();
        }
        Check(UnregisterClassW(ClassName, GetModuleHandleW(nullptr)), "all-owned-windows-destroyed");
        std::printf("{\"passed\":true,\"checks\":%u,\"selfOwned\":true,\"hiddenWindow\":true,\"independentLuidMeasured\":false,\"productionIntegration\":false}\n", checks);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
