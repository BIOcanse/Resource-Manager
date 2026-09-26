#define wmain OriginalOpenGlPreferenceEntry
#include "OpenGlPlacementProbe.cpp"
#undef wmain
#include <dxgi.h>
#include <wrl/client.h>

namespace {
// Read-only native ABI from Windows SDK 10.0.26100.0/shared/d3dkmthk.h.
struct OpenFromLuid { LUID luid; UINT adapter; };
struct OpenFromDc { HDC dc; UINT adapter; LUID luid; UINT source; };
struct GlInfo { WCHAR filename[MAX_PATH]; ULONG version; ULONG flags; };
struct QueryInfo { UINT adapter; INT type; void* data; UINT size; };
static_assert(sizeof(OpenFromLuid) == 12 && sizeof(OpenFromDc) == 24 && sizeof(GlInfo) == 528 && sizeof(QueryInfo) == 24);
using OpenLuid = LONG(WINAPI*)(OpenFromLuid*);
using OpenDc = LONG(WINAPI*)(OpenFromDc*);
using Query = LONG(WINAPI*)(const QueryInfo*);
using CloseAdapter = LONG(WINAPI*)(const UINT*);
OpenLuid openLuid{}; OpenDc openDc{}; Query query{}; CloseAdapter closeAdapter{};
std::string Utf8(const wchar_t* text) {
    const int size = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
    Check(size > 0 && size < 65536, "bounded-text");
    std::string value(size, '\0');
    Check(WideCharToMultiByte(CP_UTF8, 0, text, -1, value.data(), size, nullptr, nullptr) == size, "utf8-text");
    value.pop_back(); return value;
}
unsigned long long Value(LUID luid) { return (static_cast<unsigned long long>(static_cast<UINT>(luid.HighPart)) << 32) | luid.LowPart; }
template<typename T> T SystemEntry(HMODULE module, const char* name) {
    const auto address = GetProcAddress(module, name); Check(address != nullptr, name);
    T result{}; std::memcpy(&result, &address, sizeof(result)); return result;
}
void Icd(UINT adapter, LUID luid, const char* source) {
    GlInfo info{};
    QueryInfo request{adapter, 2, &info, sizeof(info)};
    const LONG result = query(&request);
    const bool terminated = std::find(std::begin(info.filename), std::end(info.filename), L'\0') != std::end(info.filename);
    Check(terminated, "bounded-icd-name");
    std::printf("{\"icd\":%s,\"adapterLuid\":%llu,\"status\":%ld,\"path\":%s,\"version\":%lu,\"flags\":%lu}\n",
        Json(source).c_str(), Value(luid), result, Json(Utf8(info.filename).c_str()).c_str(), info.version, info.flags);
}
struct Monitor { MONITORINFOEXW info{}; };
BOOL CALLBACK MonitorEntry(HMONITOR monitor, HDC, LPRECT, LPARAM argument) {
    auto& monitors = *reinterpret_cast<std::vector<Monitor>*>(argument);
    if (monitors.size() >= 16) return FALSE;
    Monitor item; item.info.cbSize = sizeof(item.info);
    if (!GetMonitorInfoW(monitor, &item.info)) return FALSE;
    monitors.push_back(item); return TRUE;
}
}

#ifndef RESOURCE_MANAGER_OPENGL_ROUTE_ENTRY
#define RESOURCE_MANAGER_OPENGL_ROUTE_ENTRY wmain
#endif
int RESOURCE_MANAGER_OPENGL_ROUTE_ENTRY(int argc, wchar_t** argv) {
    SetErrorMode(32771);
    try {
        Check(argc == 2, "fresh-output-directory-argument");
        FILETIME birth{}, exit{}, kernel{}, user{}; BOOL job = FALSE;
        Check(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user)
            && IsProcessInJob(GetCurrentProcess(), nullptr, &job) && job, "owned-native-identity");
        std::printf("{\"pid\":%lu,\"creationFileTimeUtc\":%llu,\"errorMode\":%u,\"inJob\":true}\n", GetCurrentProcessId(),
            (static_cast<unsigned long long>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime, GetErrorMode());
        HMODULE gdi = GetModuleHandleW(L"gdi32.dll"); Check(gdi != nullptr, "system-gdi-loaded");
        openLuid = SystemEntry<OpenLuid>(gdi, "D3DKMTOpenAdapterFromLuid");
        openDc = SystemEntry<OpenDc>(gdi, "D3DKMTOpenAdapterFromHdc");
        query = SystemEntry<Query>(gdi, "D3DKMTQueryAdapterInfo");
        closeAdapter = SystemEntry<CloseAdapter>(gdi, "D3DKMTCloseAdapter");
        Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
        Check(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))), "dxgi-inventory");
        UINT adapterCount = 0;
        for (; adapterCount < 16; ++adapterCount) {
            Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
            const auto result = factory->EnumAdapters1(adapterCount, &adapter);
            if (result == DXGI_ERROR_NOT_FOUND) break;
            Check(SUCCEEDED(result), "dxgi-adapter");
            DXGI_ADAPTER_DESC1 desc{}; Check(SUCCEEDED(adapter->GetDesc1(&desc)), "dxgi-description");
            OpenFromLuid opened{desc.AdapterLuid, 0}; const auto status = openLuid(&opened);
            std::printf("{\"adapter\":%s,\"luid\":%llu,\"vendorId\":%u,\"flags\":%u,\"openStatus\":%ld}\n",
                Json(Utf8(desc.Description).c_str()).c_str(), Value(desc.AdapterLuid), desc.VendorId, desc.Flags, status);
            if (status >= 0) { Icd(opened.adapter, desc.AdapterLuid, "dxgi"); Check(closeAdapter(&opened.adapter) >= 0, "close-inventory-adapter"); }
        }
        Check(adapterCount > 0 && adapterCount < 16, "complete-bounded-adapters");
        std::vector<Monitor> monitors;
        Check(EnumDisplayMonitors(nullptr, nullptr, MonitorEntry, reinterpret_cast<LPARAM>(&monitors)) && !monitors.empty(), "bounded-active-monitors");
        WNDCLASSW wc{}; wc.style = CS_OWNDC; wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = ClassName;
        Check(RegisterClassW(&wc) != 0, "owned-window-class");
        unsigned index = 0;
        for (const auto& monitor : monitors) {
            const auto& info = monitor.info;
            HDC display = CreateDCW(L"DISPLAY", info.szDevice, nullptr, nullptr);
            Check(display != nullptr, "named-display-dc");
            OpenFromDc opened{}; opened.dc = display;
            const auto status = openDc(&opened);
            std::printf("{\"monitor\":%s,\"x\":%ld,\"y\":%ld,\"displayDcStatus\":%ld,\"displayAdapterLuid\":%llu,\"source\":%u}\n",
                Json(Utf8(info.szDevice).c_str()).c_str(), info.rcMonitor.left, info.rcMonitor.top, status, Value(opened.luid), opened.source);
            if (status >= 0) { Icd(opened.adapter, opened.luid, "display-dc"); Check(closeAdapter(&opened.adapter) >= 0, "close-display-adapter"); }
            Check(DeleteDC(display), "delete-named-display-dc");
            const std::string phase = "monitor-" + std::to_string(index++);
            {
                Window window(info.rcMonitor.left + 8, info.rcMonitor.top + 8);
                Context original(window.dc);
                Draw(window, original, argv[1], (phase + "-legacy").c_str(), false);
                GLuint texture = 0; glGenTextures(1, &texture); glBindTexture(GL_TEXTURE_2D, texture);
                const unsigned char expected[4] = {23, 67, 109, 255};
                glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, expected);
                Check(texture && glGetError() == GL_NO_ERROR, "original-shared-texture");
                {
                    Context attributes(window.dc, true);
                    Draw(window, attributes, argv[1], (phase + "-attributes").c_str(), true);
                    attributes.Close();
                }
                Check(wglMakeCurrent(window.dc, original.context), "restore-original-context");
                {
                    Context shared(window.dc, true, original.context);
                    Check(wglMakeCurrent(window.dc, shared.context), "shared-context-current");
                    Check(glIsTexture(texture), "real-shared-texture-visible");
                    glBindTexture(GL_TEXTURE_2D, texture); unsigned char actual[4]{};
                    glGetTexImage(GL_TEXTURE_2D, 0, GL_RGBA, GL_UNSIGNED_BYTE, actual);
                    Check(glGetError() == GL_NO_ERROR && std::memcmp(expected, actual, sizeof(actual)) == 0, "real-shared-texture-readback");
                    Draw(window, shared, argv[1], (phase + "-shared-attributes").c_str(), false);
                    shared.Close();
                }
                Check(wglMakeCurrent(window.dc, original.context), "original-for-texture-release");
                glDeleteTextures(1, &texture); Check(glGetError() == GL_NO_ERROR, "texture-normal-release");
                original.Close();
            }
        }
        Check(UnregisterClassW(ClassName, GetModuleHandleW(nullptr)), "all-owned-windows-destroyed");
        std::printf("{\"passed\":true,\"checks\":%u,\"monitorCount\":%u,\"selfOwned\":true,\"displayLuidIsRenderLuid\":false,\"productionIntegration\":false}\n", checks, index);
        return 0;
    } catch (const std::exception& error) {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
