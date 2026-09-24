// Exercise the actual hooks without installing them or creating a graphics device.
#include "../GpuPlacementShim/ResourceManagerGpuPlacementShim.cpp"
#include <iostream>
#include <stdexcept>

namespace
{
unsigned checks = 0;
void Require(bool value)
{
    ++checks;
    if (!value) throw std::runtime_error("runtime boundary contract failed");
}

void PublishDuringForwarding()
{
    Require(g_insideGraphicsRuntime);
    // Model Configure publishing after an outer passive call made its decision.
    AcquireSRWLockExclusive(&g_policyLock);
    std::wcscpy(g_policyPath, L"contract-only-no-file");
    ReleaseSRWLockExclusive(&g_policyLock);
    Require(ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::D3D11));
    Require(ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::D3D12));
    Require(!DxgiRuntime::InspectCall(nullptr, DxgiRuntime::Method::Present));
}

HRESULT WINAPI Device11(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT,
    const D3D_FEATURE_LEVEL*, UINT, UINT, ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**)
{
    PublishDuringForwarding();
    return E_ABORT;
}
HRESULT WINAPI DeviceAndChain11(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT,
    const D3D_FEATURE_LEVEL*, UINT, UINT, const DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**,
    ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**)
{
    PublishDuringForwarding();
    return E_ABORT;
}
HRESULT WINAPI Device12(IUnknown*, D3D_FEATURE_LEVEL, REFIID, void**)
{
    PublishDuringForwarding();
    return E_ABORT;
}
HRESULT WINAPI Interface12(REFCLSID, REFIID, void**)
{
    PublishDuringForwarding();
    return E_ABORT;
}
void BeginPassive()
{
    Require(!g_insideGraphicsRuntime);
    g_policyPath[0] = L'\0';
}
HRESULT STDMETHODCALLTYPE Chain(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**)
{ PublishDuringForwarding(); return E_ABORT; }
HRESULT STDMETHODCALLTYPE Hwnd(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**)
{ PublishDuringForwarding(); return E_ABORT; }
HRESULT STDMETHODCALLTYPE Core(IDXGIFactory2*, IUnknown*, IUnknown*, const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**)
{ PublishDuringForwarding(); return E_ABORT; }
HRESULT STDMETHODCALLTYPE Composition(IDXGIFactory2*, IUnknown*, const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**)
{ PublishDuringForwarding(); return E_ABORT; }
}

int main()
{
    try
    {
        // Completed initialization is not successful hook initialization.
        g_initializationEvent = CreateEventW(nullptr, TRUE, TRUE, nullptr);
        Require(g_initializationEvent != nullptr);
        g_initializationRequested = g_initialized = 1;
        g_hooksEnabled = 0;
        Require(!DxgiRuntime::EnsureHooks());
        Require(ResourceManagerGpuPlacementConfigure(const_cast<wchar_t*>(L"contract-only")) == 0);
        Require(g_policyPath[0] == L'\0');
        g_hooksEnabled = 1;
        Require(!DxgiRuntime::EnsureHooks());
        CloseHandle(g_initializationEvent);
        g_initializationEvent = nullptr;

        g_realCreateDevice = &Device11;
        g_realCreateDeviceAndSwapChain = &DeviceAndChain11;
        g_realD3D12CreateDevice = &Device12;
        g_realD3D12GetInterface = &Interface12;
        BeginPassive();
        Require(HookedD3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
            nullptr, 0, D3D11_SDK_VERSION, nullptr, nullptr, nullptr) == E_ABORT);
        BeginPassive();
        Require(HookedD3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
            nullptr, 0, D3D11_SDK_VERSION, nullptr, nullptr, nullptr, nullptr, nullptr) == E_ABORT);
        BeginPassive();
        Require(HookedD3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_IUnknown, nullptr) == E_ABORT);
        BeginPassive();
        Require(HookedD3D12GetInterface(CLSID_D3D12SDKConfiguration, IID_IUnknown, nullptr) == E_ABORT);
        DxgiRuntime::realCreateChain = &Chain;
        DxgiRuntime::realCreateHwnd = &Hwnd;
        DxgiRuntime::realCreateCore = &Core;
        DxgiRuntime::realCreateComposition = &Composition;
        BeginPassive();
        Require(DxgiRuntime::CreateChain(nullptr, nullptr, nullptr, nullptr) == E_ABORT);
        BeginPassive();
        Require(DxgiRuntime::CreateHwnd(nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr) == E_ABORT);
        BeginPassive();
        Require(DxgiRuntime::CreateCore(nullptr, nullptr, nullptr, nullptr, nullptr, nullptr) == E_ABORT);
        BeginPassive();
        Require(DxgiRuntime::CreateComposition(nullptr, nullptr, nullptr, nullptr, nullptr) == E_ABORT);
        Require(!g_insideGraphicsRuntime);
        {
            const GraphicsRuntimeCall outer;
            { const GraphicsRuntimeCall inner; Require(g_insideGraphicsRuntime); }
            Require(g_insideGraphicsRuntime);
        }
        Require(!g_insideGraphicsRuntime);
        std::cout << "{\"passed\":true,\"checks\":" << checks << ",\"hardwareActions\":0}" << std::endl;
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << "; check=" << checks << std::endl;
        return 1;
    }
}
