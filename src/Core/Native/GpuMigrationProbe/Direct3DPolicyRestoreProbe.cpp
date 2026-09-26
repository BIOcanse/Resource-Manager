#define main OriginalGraphicsApiProbeEntry
#include "GraphicsApiDetectionProbe.cpp"
#undef main
#include "../GpuPlacementShim/GpuPlacementPolicy.h"
#include <string>

namespace {
unsigned policyChecks = 0;
void Require(bool ok, const char* label) {
    ++policyChecks;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", label, ok ? "true" : "false");
    std::fflush(stdout);
    if (!ok) throw std::runtime_error(label);
}
void Publish(const std::wstring& path, const std::string& text, bool fresh) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
        fresh ? CREATE_NEW : TRUNCATE_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    Require(file != INVALID_HANDLE_VALUE, "owned-policy-open");
    DWORD written = 0;
    const bool saved = WriteFile(file, text.data(), static_cast<DWORD>(text.size()), &written, nullptr)
        && written == text.size();
    const bool closed = CloseHandle(file);
    Require(saved && closed, "owned-policy-published");
}
std::string Token(uint64_t value) {
    char text[22]{};
    std::snprintf(text, sizeof(text), "0x%08x_0x%08x", static_cast<UINT>(value >> 32), static_cast<UINT>(value));
    return text;
}
uint64_t Bits(LUID value) { return (static_cast<uint64_t>(static_cast<UINT>(value.HighPart)) << 32) | value.LowPart; }
}

int wmain(int argc, wchar_t** argv) {
    SetErrorMode(32771);
    HWND window = nullptr;
    HMODULE apiModule = nullptr;
    try {
        Require(argc == 4, "provider-root-api-arguments");
        const bool d9 = std::wcscmp(argv[3], L"d3d9") == 0;
        const bool d12 = std::wcscmp(argv[3], L"d3d12") == 0;
        Require(d9 || d12 || std::wcscmp(argv[3], L"d3d11") == 0, "known-api");
        FILETIME birth{}, exit{}, kernel{}, user{};
        BOOL inJob = FALSE;
        Require(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user)
            && IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) && inJob, "owned-process");
        std::printf("{\"pid\":%lu,\"creationFileTimeUtc\":%llu,\"errorMode\":%u,\"inJob\":true}\n",
            GetCurrentProcessId(), (static_cast<unsigned long long>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime, GetErrorMode());
        const std::wstring startup = std::wstring(argv[2]) + L".startup";
        const std::wstring runtime = std::wstring(argv[2]) + L".runtime";
        Require(GetFileAttributesW(startup.c_str()) == INVALID_FILE_ATTRIBUTES
            && GetFileAttributesW(runtime.c_str()) == INVALID_FILE_ATTRIBUTES, "fresh-owned-files");
        Require(SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", nullptr), "initial-no-policy");
        apiModule = LoadLibraryExW(d9 ? L"d3d9.dll" : d12 ? L"d3d12.dll" : L"d3d11.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(apiModule != nullptr, "system-api-loaded");
        if (d9) cachedCreate9Ex = Function<Create9Ex>(apiModule, "Direct3DCreate9Ex");
        window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, L"STATIC", L"Owned policy restore",
            WS_POPUP, 0, 0, 16, 16, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        Require(window && !IsWindowVisible(window), "owned-hidden-window");
        const auto create = [&]() {
            if (d9) return Direct3D9Adapter(CreateDirect3D9Device(window).Get());
            Microsoft::WRL::ComPtr<IUnknown> device;
            device.Attach(CreateDirect3DDevice(apiModule, d12));
            return DeviceAdapter(device.Get(), d12);
        };
        const auto original = create();
        Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
        Require(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))), "native-adapter-inventory");
        uint64_t alternate = 0;
        for (UINT index = 0; index < 16; ++index) {
            Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
            const auto result = factory->EnumAdapters1(index, &adapter);
            if (result == DXGI_ERROR_NOT_FOUND) break;
            Require(SUCCEEDED(result), "adapter-query");
            DXGI_ADAPTER_DESC1 desc{};
            Require(SUCCEEDED(adapter->GetDesc1(&desc)), "adapter-description");
            if (!(desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) && Bits(desc.AdapterLuid) != original) { alternate = Bits(desc.AdapterLuid); break; }
        }
        Require(alternate != 0 && alternate != original, "discriminating-startup-target");
        Publish(startup, Token(alternate), true);
        Require(SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", startup.c_str()), "startup-bound");
        HMODULE provider = LoadLibraryW(argv[1]);
        Require(provider != nullptr, "actual-provider-loaded");
        const auto configure = Function<DWORD(WINAPI*)(LPVOID)>(provider, "ResourceManagerGpuPlacementConfigure");
        const DWORD required = d9 ? 11 : 7;
        Require((configure(const_cast<wchar_t*>(startup.c_str())) & required) == required, "startup-provider-ready");
        const auto selected = [&](uint64_t expected, const char* phase) {
            const auto actual = create();
            std::printf("{\"phase\":\"%s\",\"actualLuid\":%llu,\"expectedLuid\":%llu}\n", phase,
                static_cast<unsigned long long>(actual), static_cast<unsigned long long>(expected));
            Require(actual == expected, phase);
        };
        selected(alternate, "startup-alternate");
        Publish(runtime, Token(original), true);
        Require((configure(const_cast<wchar_t*>(runtime.c_str())) & required) == required, "runtime-provider-ready");
        selected(original, "runtime-original");
        Require(DeleteFileW(runtime.c_str()), "restore-runtime-absence");
        selected(alternate, "file-only-restore-startup");
        Publish(runtime, "default", true);
        selected(original, "explicit-default-overrides-startup");
        HANDLE locked = CreateFileW(runtime.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        Require(locked != INVALID_HANDLE_VALUE, "owned-read-error-control");
        selected(original, "read-error-is-not-absence");
        Require(CloseHandle(locked) && DeleteFileW(runtime.c_str()), "owned-lock-and-runtime-cleanup");
        selected(alternate, "second-file-only-restore");
        Require(DeleteFileW(startup.c_str()), "owned-startup-cleanup");
        selected(original, "both-absent-native-default");
        Require(DestroyWindow(window), "normal-window-cleanup"); window = nullptr;
        Require(FreeLibrary(apiModule), "normal-api-reference-release"); apiModule = nullptr;
        // The installed detours remain process-owned; do not unload their containing image.
        std::printf("{\"passed\":true,\"checks\":%u,\"selfOwned\":true,\"newDeviceIdentityOnly\":true}\n", policyChecks);
        return 0;
    } catch (const std::exception& error) {
        if (window) DestroyWindow(window);
        if (apiModule) FreeLibrary(apiModule);
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", policyChecks, error.what());
        return 1;
    }
}
