#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dxgi.h>
#include <dxgi1_4.h>
#include <d3d11.h>
#include <initguid.h>
#include <d3d12.h>
#include <d3d9.h>

#include <MinHook.h>
#include "GpuPlacementPolicy.h"
#include "Direct3DDeviceObservation.h"
#include "GpuPlacementApiObservation.h"
#include "Direct3D9AdapterSelection.h"
#include "Direct3D9HybridEnumeration.h"
#include "VulkanDeviceSelection.h"
#include "OpenGlConfigureArguments.h"
#include "OpenGlProviderConfiguration.h"
#include <mutex>

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <unordered_map>

using D3D11CreateDeviceFn = HRESULT(WINAPI*)(
    IDXGIAdapter*,
    D3D_DRIVER_TYPE,
    HMODULE,
    UINT,
    const D3D_FEATURE_LEVEL*,
    UINT,
    UINT,
    ID3D11Device**,
    D3D_FEATURE_LEVEL*,
    ID3D11DeviceContext**);

using D3D11CreateDeviceAndSwapChainFn = HRESULT(WINAPI*)(
    IDXGIAdapter*,
    D3D_DRIVER_TYPE,
    HMODULE,
    UINT,
    const D3D_FEATURE_LEVEL*,
    UINT,
    UINT,
    const DXGI_SWAP_CHAIN_DESC*,
    IDXGISwapChain**,
    ID3D11Device**,
    D3D_FEATURE_LEVEL*,
    ID3D11DeviceContext**);

using D3D12CreateDeviceFn = HRESULT(WINAPI*)(
    IUnknown*,
    D3D_FEATURE_LEVEL,
    REFIID,
    void**);

bool RestoreDetoursStartupHeaders();

namespace
{
constexpr DWORD StatusConfigured = 1;
constexpr DWORD StatusInitialized = 2;
constexpr DWORD StatusHooksEnabled = 4;
constexpr DWORD StatusD3D9HooksEnabled = 8;
constexpr DWORD StatusVulkanHooksEnabled = 16;
constexpr DWORD StatusOpenGlHooksEnabled = 32;
constexpr size_t PolicyPathCapacity = 2048;
constexpr size_t ReadyEventNameCapacity = 260;

SRWLOCK g_policyLock = SRWLOCK_INIT;
wchar_t g_policyPath[PolicyPathCapacity]{};
HANDLE g_initializationEvent = nullptr;
volatile LONG g_initializationRequested = 0;
volatile LONG g_initialized = 0;
volatile LONG g_hooksEnabled = 0;
volatile LONG g_detoursHeadersRestored = 0;
ResourceManagerGpuObservation::Store g_deviceObservations;
ResourceManagerGpuObservation::CallStore g_apiObservations;
thread_local bool g_dxgiBootstrap = false;
thread_local bool g_insideGraphicsRuntime = false;
struct GraphicsRuntimeCall
{
    const bool previous = g_insideGraphicsRuntime;
    GraphicsRuntimeCall() { g_insideGraphicsRuntime = true; }
    ~GraphicsRuntimeCall() { g_insideGraphicsRuntime = previous; }
};
D3D11CreateDeviceFn g_realCreateDevice = nullptr;
D3D11CreateDeviceAndSwapChainFn g_realCreateDeviceAndSwapChain = nullptr;
D3D12CreateDeviceFn g_realD3D12CreateDevice = nullptr;
PFN_D3D12_GET_INTERFACE g_realD3D12GetInterface = nullptr;

// Fixed COM ABI slots: IUnknown + SetSDKVersion; IUnknown + six factory methods.
constexpr size_t ConfigurationCreateFactorySlot = 4;
constexpr size_t FactoryCreateDeviceSlot = 9;
using CreateFactoryFn = HRESULT(STDMETHODCALLTYPE*)(ID3D12SDKConfiguration1*, UINT, const char*, REFIID, void**);
using FactoryCreateDeviceFn = HRESULT(STDMETHODCALLTYPE*)(ID3D12DeviceFactory*, IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**);
using FactoryQueryInterfaceFn = HRESULT(STDMETHODCALLTYPE*)(IUnknown*, REFIID, void**);
SRWLOCK g_factoryHookLock = SRWLOCK_INIT;
std::unordered_map<void**, void*> g_configurationMethods;
std::unordered_map<void**, void*> g_factoryMethods;
std::unordered_map<void**, void*> g_factoryQueryMethods;

using namespace ResourceManagerGpuPolicy;

bool ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi api)
{
    if (g_dxgiBootstrap || g_insideGraphicsRuntime) return true;
    const DWORD error = GetLastError();
    g_apiObservations.Record(api);
    AcquireSRWLockShared(&g_policyLock);
    const bool passive = g_policyPath[0] == L'\0';
    ReleaseSRWLockShared(&g_policyLock);
    SetLastError(error);
    return passive;
}

std::wstring ReadConfiguredPolicyPath()
{
    wchar_t localPath[PolicyPathCapacity]{};
    AcquireSRWLockShared(&g_policyLock);
    std::wcsncpy(localPath, g_policyPath, PolicyPathCapacity - 1);
    ReleaseSRWLockShared(&g_policyLock);

    return localPath;
}

GpuShimPolicy ReadPolicyFile()
{
    return ResourceManagerGpuPolicy::ReadCurrentPolicy(ReadConfiguredPolicyPath());
}

bool IsHardwareAdapter(IUnknown* adapter)
{
    if (adapter == nullptr)
    {
        return true;
    }

    IDXGIAdapter1* dxgiAdapter = nullptr;
    if (FAILED(adapter->QueryInterface(__uuidof(IDXGIAdapter1), reinterpret_cast<void**>(&dxgiAdapter))))
    {
        return false;
    }
    DXGI_ADAPTER_DESC1 description{};
    const HRESULT result = dxgiAdapter->GetDesc1(&description);
    dxgiAdapter->Release();
    return SUCCEEDED(result) && (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0;
}

HRESULT SelectD3D11ReplacementAdapter(
    IDXGIAdapter* adapter, D3D_DRIVER_TYPE driverType, IDXGIAdapter1*& replacement)
{
    replacement = nullptr;
    const bool hardwareRequest = (adapter == nullptr && driverType == D3D_DRIVER_TYPE_HARDWARE)
        || (adapter != nullptr && driverType == D3D_DRIVER_TYPE_UNKNOWN && IsHardwareAdapter(adapter));
    if (!hardwareRequest)
    {
        return S_OK;
    }

    const GpuShimPolicy policy = ReadPolicyFile();
    replacement = SelectAdapter(policy);
    if (adapter != nullptr && replacement != nullptr)
    {
        DXGI_ADAPTER_DESC original{};
        DXGI_ADAPTER_DESC1 selected{};
        if (SUCCEEDED(adapter->GetDesc(&original)) && SUCCEEDED(replacement->GetDesc1(&selected))
            && SameLuid(original.AdapterLuid, selected.AdapterLuid))
        {
            // Preserve the caller's adapter/factory when it already selected the target,
            // including nested device creation inside CreateDeviceAndSwapChain.
            replacement->Release();
            replacement = nullptr;
            return S_OK;
        }
    }
    return replacement == nullptr && policy.mode == GpuShimPolicyMode::TargetLuid ? DXGI_ERROR_NOT_FOUND : S_OK;
}

HRESULT WINAPI HookedD3D11CreateDevice(
    IDXGIAdapter* adapter,
    D3D_DRIVER_TYPE driverType,
    HMODULE software,
    UINT flags,
    const D3D_FEATURE_LEVEL* featureLevels,
    UINT featureLevelCount,
    UINT sdkVersion,
    ID3D11Device** device,
    D3D_FEATURE_LEVEL* featureLevel,
    ID3D11DeviceContext** immediateContext)
{
    const bool passThrough = ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::D3D11);
    const GraphicsRuntimeCall runtimeCall;
    if (passThrough)
        return g_realCreateDevice(adapter, driverType, software, flags, featureLevels,
            featureLevelCount, sdkVersion, device, featureLevel, immediateContext);
    IDXGIAdapter1* replacement = nullptr;
    const HRESULT selection = SelectD3D11ReplacementAdapter(adapter, driverType, replacement);
    if (FAILED(selection))
    {
        if (device != nullptr) *device = nullptr;
        if (immediateContext != nullptr) *immediateContext = nullptr;
        return selection;
    }
    if (replacement != nullptr)
    {
        adapter = replacement;
        driverType = D3D_DRIVER_TYPE_UNKNOWN;
    }

    const HRESULT result = g_realCreateDevice(
        adapter,
        driverType,
        software,
        flags,
        featureLevels,
        featureLevelCount,
        sdkVersion,
        device,
        featureLevel,
        immediateContext);
    ResourceManagerGpuObservation::ObserveD3D11(g_deviceObservations, result, device, immediateContext);
    if (replacement != nullptr)
    {
        replacement->Release();
    }

    return result;
}

HRESULT WINAPI HookedD3D11CreateDeviceAndSwapChain(
    IDXGIAdapter* adapter,
    D3D_DRIVER_TYPE driverType,
    HMODULE software,
    UINT flags,
    const D3D_FEATURE_LEVEL* featureLevels,
    UINT featureLevelCount,
    UINT sdkVersion,
    const DXGI_SWAP_CHAIN_DESC* swapChainDescription,
    IDXGISwapChain** swapChain,
    ID3D11Device** device,
    D3D_FEATURE_LEVEL* featureLevel,
    ID3D11DeviceContext** immediateContext)
{
    const bool passThrough = ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::D3D11);
    const GraphicsRuntimeCall runtimeCall;
    if (passThrough)
        return g_realCreateDeviceAndSwapChain(adapter, driverType, software, flags, featureLevels,
            featureLevelCount, sdkVersion, swapChainDescription, swapChain, device, featureLevel, immediateContext);
    IDXGIAdapter1* replacement = nullptr;
    const HRESULT selection = SelectD3D11ReplacementAdapter(adapter, driverType, replacement);
    if (FAILED(selection))
    {
        if (swapChain != nullptr) *swapChain = nullptr;
        if (device != nullptr) *device = nullptr;
        if (immediateContext != nullptr) *immediateContext = nullptr;
        return selection;
    }
    if (replacement != nullptr)
    {
        adapter = replacement;
        driverType = D3D_DRIVER_TYPE_UNKNOWN;
    }

    const HRESULT result = g_realCreateDeviceAndSwapChain(
        adapter,
        driverType,
        software,
        flags,
        featureLevels,
        featureLevelCount,
        sdkVersion,
        swapChainDescription,
        swapChain,
        device,
        featureLevel,
        immediateContext);
    ResourceManagerGpuObservation::ObserveD3D11(g_deviceObservations, result, device, immediateContext, swapChain);
    if (replacement != nullptr)
    {
        replacement->Release();
    }

    return result;
}

template<typename Create>
HRESULT WithD3D12Adapter(IUnknown* adapter, void** device, const Create& create)
{
    const GraphicsRuntimeCall runtimeCall;
    if (!IsHardwareAdapter(adapter))
    {
        return create(adapter);
    }

    const GpuShimPolicy policy = ReadPolicyFile();
    IDXGIAdapter1* replacement = SelectAdapter(policy);
    if (replacement == nullptr && policy.mode == GpuShimPolicyMode::TargetLuid)
    {
        if (device != nullptr)
        {
            *device = nullptr;
        }
        return DXGI_ERROR_NOT_FOUND;
    }

    const HRESULT result = create(replacement != nullptr ? replacement : adapter);
    if (replacement != nullptr)
    {
        replacement->Release();
    }
    return result;
}

HRESULT WINAPI HookedD3D12CreateDevice(
    IUnknown* adapter, D3D_FEATURE_LEVEL minimumFeatureLevel, REFIID iid, void** device)
{
    const bool passThrough = ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::D3D12);
    const GraphicsRuntimeCall runtimeCall;
    if (passThrough)
        return g_realD3D12CreateDevice(adapter, minimumFeatureLevel, iid, device);
    return WithD3D12Adapter(adapter, device, [&](IUnknown* selected) {
        const HRESULT result = g_realD3D12CreateDevice(selected, minimumFeatureLevel, iid, device);
        ResourceManagerGpuObservation::ObserveD3D12(g_deviceObservations, result, device);
        return result;
    });
}

bool InstallFactoryMethod(IUnknown* object, size_t slot, void* hook, std::unordered_map<void**, void*>& methods)
{
    void** table = *reinterpret_cast<void***>(object);
    AcquireSRWLockExclusive(&g_factoryHookLock);
    bool installed = false;
    try
    {
        void* current = table[slot];
        if (current == hook)
        {
            installed = methods.find(table) != methods.end();
        }
        else if (methods.find(table) == methods.end() || methods.at(table) == current)
        {
            // Reinstall an unchanged SDK method after reload, but never re-chain an
            // intervening hook that may already delegate to ours (which would recurse).
            methods.insert_or_assign(table, current);
            DWORD protection = 0;
            if (VirtualProtect(&table[slot], sizeof(void*), PAGE_READWRITE, &protection))
            {
                InterlockedExchangePointer(&table[slot], hook);
                DWORD ignored = 0;
                installed = VirtualProtect(&table[slot], sizeof(void*), protection, &ignored) != FALSE;
                if (!installed) InterlockedExchangePointer(&table[slot], current);
            }
        }
    }
    catch (...)
    {
        // Do not let an allocation exception cross the native COM boundary.
    }
    ReleaseSRWLockExclusive(&g_factoryHookLock);
    if (!installed) InterlockedExchange(&g_hooksEnabled, -1);
    return installed;
}

void* OriginalFactoryMethod(IUnknown* object, const std::unordered_map<void**, void*>& methods)
{
    void** table = *reinterpret_cast<void***>(object);
    AcquireSRWLockShared(&g_factoryHookLock);
    const auto found = methods.find(table);
    void* original = found != methods.end() ? found->second : nullptr;
    ReleaseSRWLockShared(&g_factoryHookLock);
    return original;
}

HRESULT AttachFactoryInterface(HRESULT result, REFIID iid, void** output);

HRESULT STDMETHODCALLTYPE HookedFactoryQueryInterface(IUnknown* object, REFIID iid, void** output)
{
    const auto original = reinterpret_cast<FactoryQueryInterfaceFn>(OriginalFactoryMethod(object, g_factoryQueryMethods));
    if (original == nullptr)
    {
        if (output != nullptr) *output = nullptr;
        InterlockedExchange(&g_hooksEnabled, -1);
        return E_UNEXPECTED;
    }
    return AttachFactoryInterface(original(object, iid, output), iid, output);
}

HRESULT STDMETHODCALLTYPE HookedFactoryCreateDevice(
    ID3D12DeviceFactory* factory, IUnknown* adapter, D3D_FEATURE_LEVEL minimumFeatureLevel, REFIID iid, void** device)
{
    const auto original = reinterpret_cast<FactoryCreateDeviceFn>(OriginalFactoryMethod(factory, g_factoryMethods));
    if (original == nullptr)
    {
        if (device != nullptr) *device = nullptr;
        InterlockedExchange(&g_hooksEnabled, -1);
        return E_UNEXPECTED;
    }
    if (g_insideGraphicsRuntime || g_dxgiBootstrap) return original(factory, adapter, minimumFeatureLevel, iid, device);
    return WithD3D12Adapter(adapter, device, [&](IUnknown* selected) {
        const HRESULT result = original(factory, selected, minimumFeatureLevel, iid, device);
        ResourceManagerGpuObservation::ObserveD3D12(g_deviceObservations, result, device);
        return result;
    });
}

HRESULT STDMETHODCALLTYPE HookedCreateDeviceFactory(
    ID3D12SDKConfiguration1* configuration, UINT sdkVersion, const char* sdkPath, REFIID iid, void** output)
{
    const auto original = reinterpret_cast<CreateFactoryFn>(OriginalFactoryMethod(configuration, g_configurationMethods));
    if (original == nullptr)
    {
        if (output != nullptr) *output = nullptr;
        InterlockedExchange(&g_hooksEnabled, -1);
        return E_UNEXPECTED;
    }
    return AttachFactoryInterface(original(configuration, sdkVersion, sdkPath, iid, output), iid, output);
}

HRESULT AttachFactoryInterface(HRESULT result, REFIID iid, void** output)
{
    if (FAILED(result) || output == nullptr || *output == nullptr) return result;
    auto* returned = static_cast<IUnknown*>(*output);
    bool installed = true;
    if (IsEqualGUID(iid, __uuidof(ID3D12SDKConfiguration1)))
        installed = InstallFactoryMethod(returned, ConfigurationCreateFactorySlot,
            reinterpret_cast<void*>(&HookedCreateDeviceFactory), g_configurationMethods);
    else if (IsEqualGUID(iid, __uuidof(ID3D12DeviceFactory)))
        installed = InstallFactoryMethod(returned, FactoryCreateDeviceSlot,
            reinterpret_cast<void*>(&HookedFactoryCreateDevice), g_factoryMethods);
    // Patch the actual returned view, including future QI views, without a second QI
    // that could produce a different tear-off object or introduce an allocation failure.
    if (installed) installed = InstallFactoryMethod(returned, 0,
        reinterpret_cast<void*>(&HookedFactoryQueryInterface), g_factoryQueryMethods);
    if (!installed)
    {
        returned->Release();
        *output = nullptr;
        return E_FAIL;
    }
    return result;
}

HRESULT WINAPI HookedD3D12GetInterface(REFCLSID clsid, REFIID iid, void** output)
{
    const bool passThrough = ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::D3D12);
    const GraphicsRuntimeCall runtimeCall;
    if (passThrough)
        return g_realD3D12GetInterface(clsid, iid, output);
    const HRESULT result = g_realD3D12GetInterface(clsid, iid, output);
    if (FAILED(result) || output == nullptr || *output == nullptr || !IsEqualGUID(clsid, CLSID_D3D12SDKConfiguration))
        return result;
    return AttachFactoryInterface(result, iid, output);
}

bool CreateAndEnableHook(FARPROC target, LPVOID detour, LPVOID* original)
{
    if (target == nullptr)
    {
        return false;
    }

    const MH_STATUS createStatus = MH_CreateHook(
        reinterpret_cast<LPVOID>(target),
        detour,
        original);
    if (createStatus != MH_OK && createStatus != MH_ERROR_ALREADY_CREATED)
    {
        return false;
    }

    const MH_STATUS enableStatus = MH_EnableHook(reinterpret_cast<LPVOID>(target));
    return enableStatus == MH_OK || enableStatus == MH_ERROR_ENABLED;
}

DWORD ReadStatus();
void SignalStartupReadyIfConfigured();

#include "DxgiRuntimeRecreation.h"
#include "VulkanRuntimeHooks.h"

void InitializePolicyPathFromEnvironment()
{
    wchar_t policyPath[PolicyPathCapacity]{};
    const DWORD length = GetEnvironmentVariableW(
        L"RM_GPU_SHIM_POLICY_FILE",
        policyPath,
        static_cast<DWORD>(PolicyPathCapacity));
    if (length == 0 || length >= PolicyPathCapacity)
    {
        return;
    }

    AcquireSRWLockExclusive(&g_policyLock);
    std::wcsncpy(g_policyPath, policyPath, PolicyPathCapacity - 1);
    g_policyPath[PolicyPathCapacity - 1] = L'\0';
    ReleaseSRWLockExclusive(&g_policyLock);
    // Other startup providers read this same per-process configuration later.
}

bool IsStartupHandshakeRequested()
{
    return GetEnvironmentVariableW(L"RM_GPU_SHIM_READY_EVENT", nullptr, 0) > 1;
}

void CompleteInitialization()
{
    InterlockedExchange(&g_initialized, 1);
    if (g_initializationEvent != nullptr)
    {
        SetEvent(g_initializationEvent);
    }
    SignalStartupReadyIfConfigured();
}

void InitializeProviderCore(bool allowLibraryLoad)
{
    HMODULE d3d11 = GetModuleHandleW(L"d3d11.dll");
    HMODULE d3d12 = GetModuleHandleW(L"d3d12.dll");
    if ((d3d11 == nullptr || d3d12 == nullptr) && allowLibraryLoad)
    {
        wchar_t systemDirectory[MAX_PATH]{};
        const UINT systemDirectoryLength = GetSystemDirectoryW(systemDirectory, MAX_PATH);
        if (systemDirectoryLength > 0 && systemDirectoryLength < MAX_PATH)
        {
            if (d3d11 == nullptr)
            {
                d3d11 = LoadLibraryW((std::wstring(systemDirectory) + L"\\d3d11.dll").c_str());
            }
            if (d3d12 == nullptr)
            {
                d3d12 = LoadLibraryW((std::wstring(systemDirectory) + L"\\d3d12.dll").c_str());
            }
        }
    }

    const MH_STATUS initializeStatus = MH_Initialize();
    if (d3d11 != nullptr && d3d12 != nullptr
        && (initializeStatus == MH_OK || initializeStatus == MH_ERROR_ALREADY_INITIALIZED))
    {
        const bool createDeviceHooked = CreateAndEnableHook(
            GetProcAddress(d3d11, "D3D11CreateDevice"),
            reinterpret_cast<LPVOID>(&HookedD3D11CreateDevice),
            reinterpret_cast<LPVOID*>(&g_realCreateDevice));
        const bool createSwapChainHooked = CreateAndEnableHook(
            GetProcAddress(d3d11, "D3D11CreateDeviceAndSwapChain"),
            reinterpret_cast<LPVOID>(&HookedD3D11CreateDeviceAndSwapChain),
            reinterpret_cast<LPVOID*>(&g_realCreateDeviceAndSwapChain));
        const bool createD3D12DeviceHooked = CreateAndEnableHook(
            GetProcAddress(d3d12, "D3D12CreateDevice"),
            reinterpret_cast<LPVOID>(&HookedD3D12CreateDevice),
            reinterpret_cast<LPVOID*>(&g_realD3D12CreateDevice));
        const FARPROC getInterface = GetProcAddress(d3d12, "D3D12GetInterface");
        const bool getInterfaceHooked = getInterface == nullptr || CreateAndEnableHook(
            getInterface,
            reinterpret_cast<LPVOID>(&HookedD3D12GetInterface),
            reinterpret_cast<LPVOID*>(&g_realD3D12GetInterface));
        if (createDeviceHooked && createSwapChainHooked && createD3D12DeviceHooked && getInterfaceHooked)
        {
            // A concurrent factory installation failure (-1) is terminal for this load.
            InterlockedCompareExchange(&g_hooksEnabled, 1, 0);
        }
    }

    CompleteInitialization();
}

DWORD WINAPI InitializeProvider(LPVOID)
{
    InitializeProviderCore(true);
    return 0;
}

bool EnsureProviderInitialized()
{
    if (g_initializationEvent == nullptr) return false;
    if (InterlockedCompareExchange(&g_initializationRequested, 1, 0) == 0)
        InitializeProviderCore(true);
    // The original remote-call owner retains a caller still executing initialization.
    return WaitForSingleObject(g_initializationEvent, 5000) == WAIT_OBJECT_0;
}

DWORD ReadStatus()
{
    DWORD status = 0;
    AcquireSRWLockShared(&g_policyLock);
    if (g_policyPath[0] != L'\0')
    {
        status |= StatusConfigured;
    }
    ReleaseSRWLockShared(&g_policyLock);

    if (InterlockedCompareExchange(&g_initialized, 0, 0) != 0)
    {
        status |= StatusInitialized;
    }
    if (InterlockedCompareExchange(&g_hooksEnabled, 0, 0) == 1)
    {
        status |= StatusHooksEnabled;
    }
    if (InterlockedCompareExchange(&VulkanProvider::ready, 0, 0) == 1)
        status |= StatusVulkanHooksEnabled;
    if (InterlockedCompareExchange(&ResourceManagerOpenGl::Provider::ready, 0, 0) == 1)
        status |= StatusOpenGlHooksEnabled;
    return status;
}

void SignalStartupReadyIfConfigured()
{
    if ((ReadStatus() & (StatusConfigured | StatusInitialized | StatusHooksEnabled))
        != (StatusConfigured | StatusInitialized | StatusHooksEnabled))
    {
        return;
    }

    wchar_t eventName[ReadyEventNameCapacity]{};
    const DWORD length = GetEnvironmentVariableW(
        L"RM_GPU_SHIM_READY_EVENT",
        eventName,
        static_cast<DWORD>(ReadyEventNameCapacity));
    if (length == 0 || length >= ReadyEventNameCapacity)
    {
        return;
    }
    if (InterlockedCompareExchange(&g_detoursHeadersRestored, 0, 0) == 0)
    {
        return;
    }

    const HANDLE readyEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, eventName);
    if (readyEvent != nullptr)
    {
        SetEvent(readyEvent);
        CloseHandle(readyEvent);
    }
    SetEnvironmentVariableW(L"RM_GPU_SHIM_READY_EVENT", nullptr);
}
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementReadDeviceObservations(LPVOID parameter)
{
    return g_deviceObservations.CopyTo(parameter);
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementArmRecreation(LPVOID parameter)
{
    return EnsureProviderInitialized() ? DxgiRuntime::Arm(parameter) : 0;
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementFinishRecreation(LPVOID parameter)
{
    return DxgiRuntime::Finish(parameter, false);
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementCancelRecreation(LPVOID parameter)
{
    return DxgiRuntime::Finish(parameter, true);
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementStartApiObservation(LPVOID parameter)
{
    if (!parameter) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
    const auto request = *static_cast<const ResourceManagerGpuObservation::CallObservationRequest*>(parameter);
    if (request.byteSize != sizeof(request) || request.version != 1 || !request.apis || !request.durationMilliseconds) {
        SetLastError(ERROR_INVALID_PARAMETER); return 0;
    }
    constexpr uint32_t supportedApis = static_cast<uint32_t>(ResourceManagerGpuObservation::CallApi::D3D11)
        | static_cast<uint32_t>(ResourceManagerGpuObservation::CallApi::D3D12)
        | static_cast<uint32_t>(ResourceManagerGpuObservation::CallApi::Vulkan)
        | static_cast<uint32_t>(ResourceManagerGpuObservation::CallApi::OpenGL);
    if (request.apis & ~supportedApis) { SetLastError(ERROR_NOT_SUPPORTED); return 0; }
    AcquireSRWLockShared(&g_policyLock);
    bool configured = g_policyPath[0] != L'\0';
    ReleaseSRWLockShared(&g_policyLock);
    if (configured) { SetLastError(ERROR_INVALID_STATE); return 0; }
    std::unique_lock<std::mutex> openGlLock(ResourceManagerOpenGl::Provider::configurationLock, std::defer_lock);
    if (request.apis & static_cast<uint32_t>(ResourceManagerGpuObservation::CallApi::OpenGL)) openGlLock.lock();
    if (!EnsureProviderInitialized() || !(ReadStatus() & StatusHooksEnabled)) {
        SetLastError(ERROR_NOT_READY); return 0;
    }
    if ((request.apis & 3u) && !DxgiRuntime::EnsureHooks()) {
        SetLastError(ERROR_NOT_READY); return 0;
    }
    if (request.apis & static_cast<uint32_t>(ResourceManagerGpuObservation::CallApi::Vulkan)) {
        if (!GetModuleHandleW(L"vulkan-1.dll")) { SetLastError(ERROR_MOD_NOT_FOUND); return 0; }
        if (!VulkanProvider::EnsureEntryHooks()) {
            SetLastError(ERROR_NOT_READY); return 0;
        }
    }
    if (openGlLock.owns_lock()) {
        ResourceManagerOpenGl::Runtime::apiObservations.store(&g_apiObservations);
        if (!ResourceManagerOpenGl::Runtime::InstallPublicCore(GetModuleHandleW(L"opengl32.dll"))) return 0;
    }
    // Configure publishes under this same lock and stops collection before selection.
    AcquireSRWLockShared(&g_policyLock);
    configured = g_policyPath[0] != L'\0';
    const DWORD error = configured ? ERROR_INVALID_STATE
        : g_apiObservations.Start(request.apis, request.durationMilliseconds);
    ReleaseSRWLockShared(&g_policyLock);
    SetLastError(error);
    return error == ERROR_SUCCESS ? 1 : 0;
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementReadApiObservation(LPVOID parameter)
{
    const DWORD error = g_apiObservations.CopyTo(parameter);
    SetLastError(error);
    return error == ERROR_SUCCESS ? 1 : 0;
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementStopApiObservation(LPVOID parameter)
{
    const DWORD error = g_apiObservations.CopyTo(parameter, true);
    SetLastError(error);
    return error == ERROR_SUCCESS ? 1 : 0;
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementConfigure(LPVOID parameter)
{
    const auto* policyPath = static_cast<const wchar_t*>(parameter);
    if (policyPath == nullptr || policyPath[0] == L'\0')
    {
        return ReadStatus();
    }

    // Presentation may create internal bridge devices on hybrid systems. Install
    // its runtime-call boundary before selection, even with a saved API identity.
    if (!EnsureProviderInitialized() || !DxgiRuntime::EnsureHooks()) return 0;
    AcquireSRWLockExclusive(&g_policyLock);
    g_apiObservations.Stop();
    std::wcsncpy(g_policyPath, policyPath, PolicyPathCapacity - 1);
    g_policyPath[PolicyPathCapacity - 1] = L'\0';
    ReleaseSRWLockExclusive(&g_policyLock);

    SignalStartupReadyIfConfigured();
    return ReadStatus();
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementConfigureVulkan(LPVOID parameter)
{
    const auto* policyPath = static_cast<const wchar_t*>(parameter);
    if (policyPath == nullptr || policyPath[0] == L'\0') return ReadStatus();
    AcquireSRWLockExclusive(&g_policyLock);
    g_apiObservations.Stop();
    std::wcsncpy(g_policyPath, policyPath, PolicyPathCapacity - 1);
    g_policyPath[PolicyPathCapacity - 1] = L'\0';
    ReleaseSRWLockExclusive(&g_policyLock);
    if (!EnsureProviderInitialized()) return 0;
    VulkanProvider::Configure();
    return ReadStatus();
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementConfigureOpenGl(LPVOID parameter)
{
    if (!parameter) { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
    uint32_t length{};
    std::memcpy(&length, parameter, sizeof(length));
    if (length < ResourceManagerOpenGl::ConfigureHeaderBytes
        || length > ResourceManagerOpenGl::MaximumConfigureArgumentBytes) {
        SetLastError(ERROR_INVALID_DATA); return 0;
    }
    try {
        ResourceManagerOpenGl::ConfigureArguments arguments;
        if (!ResourceManagerOpenGl::DecodeConfigureArguments(static_cast<const unsigned char*>(parameter), length, arguments))
            return 0;
        std::lock_guard<std::mutex> configureLock(ResourceManagerOpenGl::Provider::configurationLock);
        ResourceManagerOpenGl::ResolvedIcdCallbacks callbacks;
        if (!ResourceManagerOpenGl::ResolveIcdCallbackSource(arguments.source, callbacks)) return 0;
        const auto policy = ResourceManagerGpuPolicy::ReadCurrentPolicy(arguments.policyPath);
        auto* adapter = ResourceManagerGpuPolicy::SelectAdapter(policy);
        if (!adapter) { SetLastError(ERROR_NOT_SUPPORTED); return 0; }
        DXGI_ADAPTER_DESC1 description{};
        const HRESULT described = adapter->GetDesc1(&description);
        adapter->Release();
        if (FAILED(described)) { SetLastError(ERROR_GEN_FAILURE); return 0; }
        AcquireSRWLockExclusive(&g_policyLock);
        g_apiObservations.Stop();
        std::wcsncpy(g_policyPath, arguments.policyPath.c_str(), PolicyPathCapacity - 1);
        g_policyPath[PolicyPathCapacity - 1] = L'\0';
        ReleaseSRWLockExclusive(&g_policyLock);
        if (!EnsureProviderInitialized()) { SetLastError(ERROR_NOT_READY); return 0; }

        // These settings create only Configure's temporary context, never an application context.
        PIXELFORMATDESCRIPTOR format{};
        format.nSize = sizeof(format); format.nVersion = 1;
        format.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
        format.iPixelType = PFD_TYPE_RGBA; format.cColorBits = 24; format.cAlphaBits = 8; format.cDepthBits = 24;
        const ResourceManagerOpenGl::Provider::ConfigurationOptions options{format, {16, 16}, 8192, 4096, 2000};
        ResourceManagerOpenGl::Runtime::ConfigureDeviceObservations(g_deviceObservations, {256, 1048576, 4096});
        if (!ResourceManagerOpenGl::Provider::Configure(std::move(callbacks), description.AdapterLuid, options, &ReadPolicyFile)) return 0;
        return ReadStatus();
    } catch (const std::bad_alloc&) { SetLastError(ERROR_NOT_ENOUGH_MEMORY); return 0; }
      catch (...) { SetLastError(ERROR_UNHANDLED_EXCEPTION); return 0; }
}

extern "C" __declspec(dllexport) DWORD WINAPI ResourceManagerGpuPlacementGetStatus(LPVOID)
{
    return ReadStatus();
}

extern "C" __declspec(dllexport) DWORD_PTR WINAPI ResourceManagerGpuPlacementGetD3D11Dependency()
{
    return reinterpret_cast<DWORD_PTR>(&D3D11CreateDevice);
}

extern "C" __declspec(dllexport) DWORD_PTR WINAPI ResourceManagerGpuPlacementGetD3D12Dependency()
{
    return reinterpret_cast<DWORD_PTR>(&D3D12CreateDevice);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        g_initializationEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!IsStartupHandshakeRequested()) return TRUE;
        if (RestoreDetoursStartupHeaders())
        {
            InterlockedExchange(&g_detoursHeadersRestored, 1);
        }
        InitializePolicyPathFromEnvironment();
        const DWORD readyNameLength = GetEnvironmentVariableW(L"RM_GPU_SHIM_READY_EVENT", nullptr, 0);
        if (g_policyPath[0] == L'\0' || readyNameLength > ReadyEventNameCapacity
            || g_initializationEvent == nullptr) return TRUE;
        InterlockedExchange(&g_initializationRequested, 1);
        // Static imports make both creation APIs available before the startup handshake.
        if (GetModuleHandleW(L"d3d11.dll") != nullptr && GetModuleHandleW(L"d3d12.dll") != nullptr)
        {
            InitializeProviderCore(false);
        }
        else
        {
            const HANDLE initializationThread = CreateThread(
                nullptr,
                0,
                InitializeProvider,
                nullptr,
                0,
                nullptr);
            if (initializationThread != nullptr)
            {
                CloseHandle(initializationThread);
            }
            else
            {
                CompleteInitialization();
            }
        }
    }

    return TRUE;
}
