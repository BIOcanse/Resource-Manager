#define NOMINMAX
#include <windows.h>
#include <initguid.h>
#include <d3d9.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <climits>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iterator>
#include <stdexcept>
#include <string>
#include <vector>
#include "../GpuPlacementShim/Direct3D9AdapterSelection.h"

using Microsoft::WRL::ComPtr;
using namespace ResourceManagerGpuPolicy;
using namespace ResourceManagerD3D9;

namespace
{
unsigned checks = 0;
unsigned createdDevices = 0;
unsigned readbackPixels = 0;
bool allPathsChangedAdapter = true;
constexpr UINT Side = 16;
constexpr DWORD DeviceFlags = D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE;

void Require(bool value, const char* name)
{
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", name, value ? "true" : "false");
    std::fflush(stdout);
    if (!value) throw std::runtime_error(name);
    ++checks;
}

void Hr(HRESULT result, const char* name)
{
    if (FAILED(result))
    {
        std::fprintf(stderr, "%s HRESULT=0x%08lx\n", name, static_cast<unsigned long>(result));
        throw std::runtime_error(name);
    }
}

uint64_t Value(LUID luid)
{
    return (static_cast<uint64_t>(static_cast<uint32_t>(luid.HighPart)) << 32) | luid.LowPart;
}

std::string Json(const char* value)
{
    std::string result = "\"";
    for (const unsigned char* current = reinterpret_cast<const unsigned char*>(value); *current; ++current)
    {
        if (*current == '\\' || *current == '"') result.push_back('\\');
        if (*current < 0x20)
        {
            char escaped[7]{};
            std::snprintf(escaped, sizeof(escaped), "\\u%04x", *current);
            result += escaped;
        }
        else result.push_back(static_cast<char>(*current));
    }
    return result + "\"";
}

std::string Utf8(const wchar_t* value)
{
    const int length = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (length < 1) throw std::runtime_error("utf8-size");
    std::string result(static_cast<size_t>(length), '\0');
    if (WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), length, nullptr, nullptr) != length)
        throw std::runtime_error("utf8-value");
    result.pop_back();
    return result;
}

void Runtime(const wchar_t* name)
{
    wchar_t path[32768]{};
    const DWORD length = GetModuleFileNameW(GetModuleHandleW(name), path, static_cast<DWORD>(std::size(path)));
    Require(length > 0 && length < std::size(path), "loaded-runtime-path");
    std::printf("{\"runtime\":%s,\"path\":%s}\n", Json(Utf8(name).c_str()).c_str(), Json(Utf8(path).c_str()).c_str());
}

class OwnedWindow
{
    HWND handle_ = nullptr;
public:
    OwnedWindow()
    {
        handle_ = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, L"STATIC", L"RM D3D9 Selection Probe",
            WS_POPUP, 0, 0, Side, Side, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        Require(handle_ != nullptr, "own-hidden-window-created");
    }
    ~OwnedWindow() { if (handle_) DestroyWindow(handle_); }
    HWND Get() const { return handle_; }
    void Close()
    {
        Require(!IsWindowVisible(handle_) && GetForegroundWindow() != handle_, "own-window-never-presented");
        const HWND old = handle_;
        Require(DestroyWindow(old) != FALSE, "own-window-destroyed");
        handle_ = nullptr;
        Require(!IsWindow(old), "own-window-absent");
    }
};

struct Adapter
{
    UINT ordinal;
    LUID luid;
};

std::vector<LUID> HardwareAdapters()
{
    ComPtr<IDXGIFactory1> factory;
    Hr(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "dxgi-factory");
    std::vector<LUID> result;
    bool exhausted = false;
    for (UINT index = 0; index < MaximumAdapterCount; ++index)
    {
        ComPtr<IDXGIAdapter1> adapter;
        const HRESULT enumerated = factory->EnumAdapters1(index, &adapter);
        if (enumerated == DXGI_ERROR_NOT_FOUND) { exhausted = true; break; }
        Hr(enumerated, "dxgi-enumeration");
        DXGI_ADAPTER_DESC1 description{};
        Hr(adapter->GetDesc1(&description), "dxgi-description");
        if (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
        std::printf("{\"hardware\":true,\"dxgiOrdinal\":%u,\"luid\":\"%016llx\",\"description\":%s,\"vendorId\":%u,\"deviceId\":%u}\n",
            index, static_cast<unsigned long long>(Value(description.AdapterLuid)), Json(Utf8(description.Description).c_str()).c_str(),
            description.VendorId, description.DeviceId);
        result.push_back(description.AdapterLuid);
    }
    Require(exhausted, "dxgi-enumeration-bounded-and-complete");
    Require(result.size() >= 2, "two-real-hardware-adapters-required");
    return result;
}

std::vector<Adapter> FactoryAdapters(IDirect3D9* factory, const char* api, const std::vector<LUID>& hardware)
{
    AdapterIdentities identities(factory);
    Hr(identities.Open(Direct3DCreate9Ex), "open-adapter-identities");
    const UINT count = factory->GetAdapterCount();
    Require(count > 0 && count <= MaximumAdapterCount, "factory-enumeration-bounded");
    std::vector<Adapter> result;
    for (UINT index = 0; index < count; ++index)
    {
        LUID luid{};
        Hr(identities.Luid(index, luid), "factory-adapter-luid");
        D3DADAPTER_IDENTIFIER9 description{};
        Hr(factory->GetAdapterIdentifier(index, 0, &description), "factory-adapter-description");
        Require(std::memchr(description.DeviceName, '\0', sizeof(description.DeviceName)) != nullptr,
            "gdi-device-name-terminated");
        bool known = false;
        for (const LUID candidate : hardware) known |= SameLuid(candidate, luid);
        Require(known, "factory-luid-matches-dxgi-hardware");
        std::printf("{\"factory\":%s,\"ordinal\":%u,\"luid\":\"%016llx\",\"gdiDevice\":%s}\n",
            Json(api).c_str(), index, static_cast<unsigned long long>(Value(luid)), Json(description.DeviceName).c_str());
        result.push_back({index, luid});
    }
    return result;
}

bool HardwareTargets(IDirect3D9* factory, const char* api, const std::vector<Adapter>& adapters, const std::vector<LUID>& hardware)
{
    bool complete = true;
    for (const LUID expected : hardware)
    {
        bool found = false;
        for (const auto& candidate : adapters) found |= SameLuid(candidate.luid, expected);
        complete &= found;
        UINT selected = UINT_MAX;
        GpuShimPolicy policy{GpuShimPolicyMode::TargetLuid, expected};
        const HRESULT result = SelectAdapterOrdinal(factory, adapters.front().ordinal, D3DDEVTYPE_HAL,
            DeviceFlags, policy, Direct3DCreate9Ex, selected);
        std::printf("{\"hardwareTarget\":%s,\"luid\":\"%016llx\",\"enumerated\":%s,\"selectionHresult\":\"%08lx\",\"selectedOrdinal\":%u}\n",
            Json(api).c_str(), static_cast<unsigned long long>(Value(expected)), found ? "true" : "false",
            static_cast<unsigned long>(result), selected);
        if (found)
        {
            Hr(result, "enumerated-hardware-target-selection");
            Require(selected < adapters.size() && SameLuid(adapters[selected].luid, expected), "hardware-target-ordinal-matches-luid");
        }
        else Require(result == D3DERR_NOTAVAILABLE && selected == adapters.front().ordinal,
            "unexposed-real-hardware-target-rejected-no-fallback");
    }
    return complete;
}

GpuShimPolicy Target(LUID luid)
{
    return {GpuShimPolicyMode::TargetLuid, luid};
}

D3DPRESENT_PARAMETERS Parameters(HWND window)
{
    D3DPRESENT_PARAMETERS result{};
    result.BackBufferWidth = Side;
    result.BackBufferHeight = Side;
    result.BackBufferFormat = D3DFMT_A8R8G8B8;
    result.BackBufferCount = 1;
    result.MultiSampleType = D3DMULTISAMPLE_NONE;
    result.SwapEffect = D3DSWAPEFFECT_DISCARD;
    result.hDeviceWindow = window;
    result.Windowed = TRUE;
    result.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
    return result;
}

void Readback(IDirect3DDevice9* device)
{
    ComPtr<IDirect3DSurface9> original;
    Hr(device->GetRenderTarget(0, &original), "get-original-target");
    ComPtr<IDirect3DSurface9> target;
    Hr(device->CreateRenderTarget(Side, Side, D3DFMT_A8R8G8B8, D3DMULTISAMPLE_NONE, 0, FALSE, &target, nullptr), "create-render-target");
    Hr(device->SetRenderTarget(0, target.Get()), "set-render-target");
    Hr(device->Clear(0, nullptr, D3DCLEAR_TARGET, D3DCOLOR_ARGB(255, 64, 128, 191), 1.0f, 0), "gpu-clear");
    ComPtr<IDirect3DSurface9> readback;
    Hr(device->CreateOffscreenPlainSurface(Side, Side, D3DFMT_A8R8G8B8, D3DPOOL_SYSTEMMEM, &readback, nullptr), "create-readback");
    Hr(device->GetRenderTargetData(target.Get(), readback.Get()), "gpu-readback");
    D3DLOCKED_RECT locked{};
    Hr(readback->LockRect(&locked, nullptr, D3DLOCK_READONLY), "lock-readback");
    bool matched = locked.pBits && locked.Pitch >= static_cast<INT>(Side * 4);
    constexpr unsigned char expected[] = {191, 128, 64, 255};
    if (matched)
        for (UINT y = 0; y < Side; ++y)
            for (UINT x = 0; x < Side; ++x)
                matched &= std::memcmp(static_cast<const unsigned char*>(locked.pBits) + y * locked.Pitch + x * 4,
                    expected, sizeof(expected)) == 0;
    Hr(readback->UnlockRect(), "unlock-readback");
    Hr(device->SetRenderTarget(0, original.Get()), "restore-original-target");
    Require(matched, "all-256-gpu-pixels-match");
    readbackPixels += Side * Side;
}

void CheckDevice(IDirect3DDevice9* device, const char* api, UINT selected, LUID expected, HWND window)
{
    D3DDEVICE_CREATION_PARAMETERS created{};
    Hr(device->GetCreationParameters(&created), "device-creation-parameters");
    Require(created.AdapterOrdinal == selected && created.DeviceType == D3DDEVTYPE_HAL
        && created.BehaviorFlags == DeviceFlags && created.hFocusWindow == window, "original-device-parameters-preserved");
    ComPtr<IDirect3D9> parent;
    Hr(device->GetDirect3D(&parent), "actual-device-parent");
    AdapterIdentities identities(parent.Get());
    Hr(identities.Open(Direct3DCreate9Ex), "open-actual-device-identities");
    LUID actual{};
    Hr(identities.Luid(created.AdapterOrdinal, actual), "actual-device-adapter-luid");
    std::printf("{\"returnedDevice\":%s,\"ordinal\":%u,\"expectedLuid\":\"%016llx\",\"actualLuid\":\"%016llx\",\"identitySource\":\"returned-device-parent-adapter\"}\n",
        Json(api).c_str(), selected, static_cast<unsigned long long>(Value(expected)), static_cast<unsigned long long>(Value(actual)));
    Require(SameLuid(actual, expected), "returned-device-luid-matches-target");
    ComPtr<IDirect3DSwapChain9> swapChain;
    Hr(device->GetSwapChain(0, &swapChain), "returned-swapchain");
    D3DPRESENT_PARAMETERS presented{};
    Hr(swapChain->GetPresentParameters(&presented), "returned-present-parameters");
    Require(presented.BackBufferWidth == Side && presented.BackBufferHeight == Side && presented.BackBufferFormat == D3DFMT_A8R8G8B8
        && presented.Windowed && presented.hDeviceWindow == window && presented.SwapEffect == D3DSWAPEFFECT_DISCARD,
        "windowed-surface-parameters-preserved");
}

void Boundaries(IDirect3D9* factory, const char* api, const std::vector<Adapter>& adapters, HWND window, bool availableOnly)
{
    const UINT original = adapters.front().ordinal;
    UINT selected = UINT_MAX;
    GpuShimPolicy policy{GpuShimPolicyMode::Default, {}};
    Hr(SelectAdapterOrdinal(factory, original, D3DDEVTYPE_HAL, DeviceFlags, policy, nullptr, selected), "default-selection");
    Require(selected == original, "default-preserves-original-without-identity-query");
    policy = Target({0xffffffffu, static_cast<LONG>(0xffffffffu)});
    for (const auto& adapter : adapters) Require(!SameLuid(adapter.luid, policy.targetLuid), "missing-target-really-absent");
    for (const D3DDEVTYPE type : {D3DDEVTYPE_REF, D3DDEVTYPE_SW, D3DDEVTYPE_NULLREF})
    {
        Hr(SelectAdapterOrdinal(factory, original, type, DeviceFlags, policy, nullptr, selected), "non-hal-selection");
        Require(selected == original, "non-hal-preserves-original-without-identity-query");
    }
    Hr(SelectAdapterOrdinal(factory, UINT_MAX, D3DDEVTYPE_HAL, DeviceFlags, policy, nullptr, selected), "invalid-original-selection");
    Require(selected == UINT_MAX, "invalid-original-not-made-valid");
    D3DPRESENT_PARAMETERS parameters = Parameters(window);
    ComPtr<IDirect3DDevice9> invalid;
    const HRESULT invalidResult = factory->CreateDevice(selected, D3DDEVTYPE_HAL, window, DeviceFlags, &parameters, &invalid);
    Require(FAILED(invalidResult) && !invalid, "original-api-rejects-invalid-ordinal");
    const HRESULT missingResult = SelectAdapterOrdinal(factory, original, D3DDEVTYPE_HAL, DeviceFlags, policy, Direct3DCreate9Ex, selected);
    Require(missingResult == D3DERR_NOTAVAILABLE && selected == original, "missing-target-explicit-failure-no-fallback");
    Hr(SelectAdapterOrdinal(factory, original, D3DDEVTYPE_HAL, D3DCREATE_ADAPTERGROUP_DEVICE,
        Target(adapters.front().luid), Direct3DCreate9Ex, selected), "same-adapter-group-selection");
    Require(selected == original, "same-adapter-group-not-rewritten");
    bool differentFound = false;
    for (const auto& adapter : adapters)
    {
        if (SameLuid(adapter.luid, adapters.front().luid)) continue;
        const HRESULT grouped = SelectAdapterOrdinal(factory, original, D3DDEVTYPE_HAL, D3DCREATE_ADAPTERGROUP_DEVICE,
            Target(adapter.luid), Direct3DCreate9Ex, selected);
        Require(grouped == D3DERR_NOTAVAILABLE && selected == original, "different-adapter-group-rejected-before-array-read");
        differentFound = true;
        break;
    }
    if (!availableOnly) Require(differentFound, "cross-adapter-group-negative-exercised");
    std::printf("{\"boundariesCompleted\":%s,\"crossAdapterGroupCaseExercised\":%s}\n", Json(api).c_str(), differentFound ? "true" : "false");
}

void CreateOnAdapters(IDirect3D9* factory, IDirect3D9Ex* ex, const char* api,
    const std::vector<Adapter>& adapters, HWND window, bool availableOnly)
{
    struct Device { ComPtr<IDirect3DDevice9> value; UINT ordinal; LUID luid; };
    std::vector<Device> retained;
    for (const auto& adapter : adapters)
    {
        bool duplicate = false;
        for (const auto& existing : retained) duplicate |= SameLuid(existing.luid, adapter.luid);
        if (duplicate) continue;
        UINT selected = UINT_MAX;
        Hr(SelectAdapterOrdinal(factory, adapters.front().ordinal, D3DDEVTYPE_HAL, DeviceFlags,
            Target(adapter.luid), Direct3DCreate9Ex, selected), "target-selection");
        ComPtr<IDirect3DDevice9> device;
        D3DPRESENT_PARAMETERS parameters = Parameters(window);
        if (ex)
        {
            ComPtr<IDirect3DDevice9Ex> extended;
            Hr(ex->CreateDeviceEx(selected, D3DDEVTYPE_HAL, window, DeviceFlags, &parameters, nullptr, &extended), "create-device-ex");
            device = extended;
        }
        else Hr(factory->CreateDevice(selected, D3DDEVTYPE_HAL, window, DeviceFlags, &parameters, &device), "create-device");
        Require(device.Get() != nullptr, "real-device-returned");
        CheckDevice(device.Get(), api, selected, adapter.luid, window);
        Readback(device.Get());
        ++createdDevices;
        retained.push_back({device, selected, adapter.luid});
    }
    if (!availableOnly) Require(retained.size() >= 2, "two-hardware-devices-created-per-path");
    Require(!retained.empty(), "available-hardware-device-created");
    for (const auto& device : retained)
    {
        CheckDevice(device.value.Get(), api, device.ordinal, device.luid, window);
        Readback(device.value.Get());
    }
    allPathsChangedAdapter &= retained.size() >= 2;
    std::printf("{\"pathCompleted\":%s,\"distinctHardwareDevices\":%u,\"differentTargetRecheck\":%s}\n",
        Json(api).c_str(), static_cast<unsigned>(retained.size()), retained.size() >= 2 ? "true" : "false");
}
}

int main(int argc, char** argv)
{
    try
    {
        const bool availableOnly = argc == 2 && std::strcmp(argv[1], "--diagnose-available") == 0;
        Require(argc == 1 || availableOnly, "explicit-probe-scope");
        FILETIME creation{}, exit{}, kernel{}, user{};
        Require(GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user) != FALSE, "native-process-creation");
        BOOL inJob = FALSE;
        Require(IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) && inJob, "owned-job-membership");
        Require((GetErrorMode() & 0x8003) == 0x8003, "native-non-dialog-error-mode");
        std::printf("{\"processId\":%lu,\"processCreationFileTime\":%llu,\"errorMode\":%u,\"ownedJob\":true}\n",
            GetCurrentProcessId(), (static_cast<unsigned long long>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime, GetErrorMode());
        const auto hardware = HardwareAdapters();
        ComPtr<IDirect3D9> classic;
        classic.Attach(Direct3DCreate9(D3D_SDK_VERSION));
        Require(classic.Get() != nullptr, "classic-factory-created");
        ComPtr<IDirect3D9Ex> extended;
        Hr(Direct3DCreate9Ex(D3D_SDK_VERSION, &extended), "extended-factory-created");
        ComPtr<IDirect3D9Ex> classicAsEx;
        Require(classic->QueryInterface(IID_PPV_ARGS(&classicAsEx)) == E_NOINTERFACE && !classicAsEx,
            "classic-factory-needs-explicit-gdi-bridge");
        Runtime(L"d3d9.dll");
        Runtime(L"dxgi.dll");
        const auto classicAdapters = FactoryAdapters(classic.Get(), "D3D9", hardware);
        const auto exAdapters = FactoryAdapters(extended.Get(), "D3D9Ex", hardware);
        const bool classicComplete = HardwareTargets(classic.Get(), "D3D9", classicAdapters, hardware);
        const bool exComplete = HardwareTargets(extended.Get(), "D3D9Ex", exAdapters, hardware);
        if (!availableOnly) Require(classicComplete && exComplete, "both-factories-cover-every-dxgi-hardware-adapter");
        OwnedWindow window;
        Boundaries(classic.Get(), "D3D9", classicAdapters, window.Get(), availableOnly);
        Boundaries(extended.Get(), "D3D9Ex", exAdapters, window.Get(), availableOnly);
        CreateOnAdapters(classic.Get(), nullptr, "D3D9.CreateDevice", classicAdapters, window.Get(), availableOnly);
        CreateOnAdapters(extended.Get(), extended.Get(), "D3D9Ex.CreateDeviceEx", exAdapters, window.Get(), availableOnly);
        CreateOnAdapters(extended.Get(), nullptr, "D3D9Ex.CreateDevice", exAdapters, window.Get(), availableOnly);
        extended.Reset();
        classic.Reset();
        window.Close();
        std::printf("{\"passed\":true,\"scope\":\"%s\",\"qualificationPassed\":%s,\"crossAdapterSelectionProved\":%s,\"allHardwareCovered\":%s,\"checks\":%u,\"createdDevices\":%u,\"readbackPixels\":%u,\"presentCalls\":0,\"providerHooksInstalled\":false,\"existingDeviceMigrationProved\":false}\n",
            availableOnly ? "diagnose-available" : "qualify-two-adapters", availableOnly ? "false" : "true",
            allPathsChangedAdapter ? "true" : "false", classicComplete && exComplete ? "true" : "false", checks, createdDevices, readbackPixels);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::fprintf(stderr, "D3D9 selection probe failed: %s\n", error.what());
        return 1;
    }
}
