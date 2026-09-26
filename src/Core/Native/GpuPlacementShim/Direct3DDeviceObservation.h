#pragma once
#include "GpuPlacementDeviceObservation.h"

namespace ResourceManagerGpuObservation
{
inline void ObserveD3D11(Store& store, HRESULT result, ID3D11Device** device,
    ID3D11DeviceContext** context, IDXGISwapChain** swapChain = nullptr)
{
    if (FAILED(result)) return;
    ID3D11Device* returned = device ? *device : nullptr;
    bool releaseDevice = false;
    const bool hasObject = returned || (context && *context) || (swapChain && *swapChain);
    if (!hasObject) return;
    if (!returned && context && *context)
    {
        (*context)->GetDevice(&returned);
        releaseDevice = returned != nullptr;
    }
    if (!returned && swapChain && *swapChain)
    {
        if (SUCCEEDED((*swapChain)->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&returned))))
            releaseDevice = returned != nullptr;
    }
    LUID luid{};
    Identity identity = Identity::Unavailable;
    IDXGIDevice* dxgi = nullptr;
    if (returned && SUCCEEDED(returned->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgi))))
    {
        IDXGIAdapter* adapter = nullptr;
        if (dxgi && SUCCEEDED(dxgi->GetAdapter(&adapter)) && adapter)
        {
            DXGI_ADAPTER_DESC description{};
            if (SUCCEEDED(adapter->GetDesc(&description)))
            {
                identity = Identity::Adapter;
                luid = description.AdapterLuid;
            }
            adapter->Release();
        }
        if (dxgi) dxgi->Release();
    }
    if (releaseDevice) returned->Release();
    store.Publish(Api::D3D11, identity, luid);
}

inline void ObserveD3D12(Store& store, HRESULT result, void** output)
{
    if (FAILED(result) || !output || !*output) return;
    ID3D12Device* device = nullptr;
    LUID luid{};
    Identity identity = Identity::Unavailable;
    if (SUCCEEDED(static_cast<IUnknown*>(*output)->QueryInterface(__uuidof(ID3D12Device), reinterpret_cast<void**>(&device))) && device)
    {
        luid = device->GetAdapterLuid();
        identity = Identity::Adapter;
        device->Release();
    }
    store.Publish(Api::D3D12, identity, luid);
}
}
