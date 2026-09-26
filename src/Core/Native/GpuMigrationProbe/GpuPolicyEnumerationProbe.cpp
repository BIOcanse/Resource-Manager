#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dxgi.h>
#include <cstdio>
#include <stdexcept>

HRESULT WINAPI CreateTestFactory(REFIID, void**);
#define CreateDXGIFactory1 CreateTestFactory
#include "../GpuPlacementShim/GpuPlacementPolicy.h"
#undef CreateDXGIFactory1

namespace {
ULONG adapterReferences = 0, factoryReferences = 0;
UINT calls = 0, errorAt = 0;
bool nullSuccess = false;

class Adapter final : public IDXGIAdapter1 {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void**) override { return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++adapterReferences; }
    ULONG STDMETHODCALLTYPE Release() override { return --adapterReferences; }
    HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID, UINT, const void*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID, const IUnknown*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID, UINT*, void*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetParent(REFIID, void**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EnumOutputs(UINT, IDXGIOutput**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetDesc(DXGI_ADAPTER_DESC*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE CheckInterfaceSupport(REFGUID, LARGE_INTEGER*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetDesc1(DXGI_ADAPTER_DESC1* output) override {
        *output = {}; output->DedicatedVideoMemory = 1024; output->AdapterLuid.LowPart = 42; return S_OK;
    }
} adapter;

class Factory final : public IDXGIFactory1 {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void**) override { return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++factoryReferences; }
    ULONG STDMETHODCALLTYPE Release() override { return --factoryReferences; }
    HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID, UINT, const void*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID, const IUnknown*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID, UINT*, void*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetParent(REFIID, void**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EnumAdapters(UINT, IDXGIAdapter**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE MakeWindowAssociation(HWND, UINT) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetWindowAssociation(HWND*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE CreateSwapChain(IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE CreateSoftwareAdapter(HMODULE, IDXGIAdapter**) override { return E_NOTIMPL; }
    BOOL STDMETHODCALLTYPE IsCurrent() override { return TRUE; }
    HRESULT STDMETHODCALLTYPE EnumAdapters1(UINT index, IDXGIAdapter1** output) override {
        ++calls; *output = nullptr;
        if (index < errorAt) { adapter.AddRef(); *output = &adapter; return S_OK; }
        // The third fault terminates even the old implementation; no hung red test.
        if (index >= errorAt + 2) return DXGI_ERROR_NOT_FOUND;
        return nullSuccess ? S_OK : E_FAIL;
    }
} factory;
}

HRESULT WINAPI CreateTestFactory(REFIID, void** output) {
    factory.AddRef(); *output = &factory; return S_OK;
}

int main() {
    bool passed = true;
    for (UINT firstFault : {0U, 1U}) for (bool empty : {false, true}) {
        calls = 0; errorAt = firstFault; nullSuccess = empty;
        const ResourceManagerGpuPolicy::GpuShimPolicy policy{ResourceManagerGpuPolicy::GpuShimPolicyMode::HighPerformance, {}};
        auto* result = ResourceManagerGpuPolicy::SelectAdapter(policy);
        const bool rejected = result == nullptr;
        if (result) result->Release();
        const bool current = rejected && calls == firstFault + 1 && !adapterReferences && !factoryReferences;
        std::printf("{\"case\":\"%s\",\"candidateBeforeError\":%s,\"calls\":%u,\"rejected\":%s,\"adapterReferences\":%lu,\"factoryReferences\":%lu,\"passed\":%s}\n",
            empty ? "null-success" : "persistent-error", firstFault ? "true" : "false", calls,
            rejected ? "true" : "false", adapterReferences, factoryReferences, current ? "true" : "false");
        passed = passed && current;
    }
    return passed ? 0 : 1;
}
