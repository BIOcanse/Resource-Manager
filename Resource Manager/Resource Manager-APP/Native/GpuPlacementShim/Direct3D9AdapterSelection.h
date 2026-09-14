#pragma once
#include <d3d9.h>
#include <wrl/client.h>
#include <cstring>
#include "GpuPlacementPolicy.h"

namespace ResourceManagerD3D9
{
using Create9Ex = HRESULT(WINAPI*)(UINT, IDirect3D9Ex**);
constexpr UINT MaximumAdapterCount = 64;

class AdapterIdentities
{
    IDirect3D9* original_;
    Microsoft::WRL::ComPtr<IDirect3D9Ex> luidSource_;
    bool sameFactory_ = false;
public:
    explicit AdapterIdentities(IDirect3D9* original) : original_(original) {}

    HRESULT Open(Create9Ex createEx)
    {
        if (!original_) return E_POINTER;
        const HRESULT queried = original_->QueryInterface(__uuidof(IDirect3D9Ex),
            reinterpret_cast<void**>(luidSource_.GetAddressOf()));
        sameFactory_ = SUCCEEDED(queried) && luidSource_;
        if (!sameFactory_)
        {
            if (queried != E_NOINTERFACE) return FAILED(queried) ? queried : E_UNEXPECTED;
            if (!createEx) return E_NOINTERFACE;
            const HRESULT created = createEx(D3D_SDK_VERSION, luidSource_.GetAddressOf());
            if (FAILED(created)) return created;
            if (!luidSource_) return E_UNEXPECTED;
        }
        return original_->GetAdapterCount() <= MaximumAdapterCount
            && luidSource_->GetAdapterCount() <= MaximumAdapterCount ? S_OK : D3DERR_NOTAVAILABLE;
    }

    HRESULT Luid(UINT ordinal, LUID& output) const
    {
        if (!luidSource_) return E_UNEXPECTED;
        if (sameFactory_) return luidSource_->GetAdapterLUID(ordinal, &output);
        D3DADAPTER_IDENTIFIER9 requested{};
        HRESULT result = original_->GetAdapterIdentifier(ordinal, 0, &requested);
        if (FAILED(result)) return result;
        if (!requested.DeviceName[0] || !std::memchr(requested.DeviceName, '\0', sizeof(requested.DeviceName)))
            return D3DERR_NOTAVAILABLE;
        bool found = false;
        LUID selected{};
        const UINT count = luidSource_->GetAdapterCount();
        if (count > MaximumAdapterCount) return D3DERR_NOTAVAILABLE;
        for (UINT index = 0; index < count; ++index)
        {
            D3DADAPTER_IDENTIFIER9 candidate{};
            result = luidSource_->GetAdapterIdentifier(index, 0, &candidate);
            if (FAILED(result)) return result;
            if (std::strncmp(requested.DeviceName, candidate.DeviceName, sizeof(requested.DeviceName)) != 0) continue;
            // Hybrid routing can reuse the GDI name for different hardware in a newer factory.
            if (requested.VendorId != candidate.VendorId || requested.DeviceId != candidate.DeviceId
                || requested.SubSysId != candidate.SubSysId || requested.Revision != candidate.Revision
                || std::memcmp(&requested.DeviceIdentifier, &candidate.DeviceIdentifier, sizeof(GUID)) != 0) continue;
            LUID actual{};
            result = luidSource_->GetAdapterLUID(index, &actual);
            if (FAILED(result)) return result;
            if (found && !ResourceManagerGpuPolicy::SameLuid(selected, actual)) return D3DERR_NOTAVAILABLE;
            selected = actual;
            found = true;
        }
        if (!found) return D3DERR_NOTAVAILABLE;
        output = selected;
        return S_OK;
    }
};

inline HRESULT SelectAdapterOrdinal(IDirect3D9* factory, UINT original, D3DDEVTYPE type, DWORD flags,
    const ResourceManagerGpuPolicy::GpuShimPolicy& policy, Create9Ex createEx, UINT& selected)
{
    using namespace ResourceManagerGpuPolicy;
    selected = original;
    if (type != D3DDEVTYPE_HAL || policy.mode == GpuShimPolicyMode::Default) return S_OK;
    if (!factory) return E_POINTER;
    const UINT count = factory->GetAdapterCount();
    if (original >= count) return S_OK;
    if (count > MaximumAdapterCount) return D3DERR_NOTAVAILABLE;

    LUID target = policy.targetLuid;
    if (policy.mode != GpuShimPolicyMode::TargetLuid)
    {
        Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
        adapter.Attach(SelectAdapter(policy));
        if (!adapter) return S_OK;
        DXGI_ADAPTER_DESC1 description{};
        const HRESULT described = adapter->GetDesc1(&description);
        if (FAILED(described)) return described;
        target = description.AdapterLuid;
    }

    AdapterIdentities identities(factory);
    HRESULT result = identities.Open(createEx);
    if (FAILED(result)) return result;
    LUID actual{};
    result = identities.Luid(original, actual);
    if (FAILED(result)) return result;
    if (SameLuid(actual, target)) return S_OK;
    // The caller's parameter array belongs to its original multihead group.
    if (flags & D3DCREATE_ADAPTERGROUP_DEVICE) return D3DERR_NOTAVAILABLE;
    for (UINT index = 0; index < count; ++index)
    {
        if (index == original) continue;
        result = identities.Luid(index, actual);
        if (FAILED(result)) return result;
        if (SameLuid(actual, target)) { selected = index; return S_OK; }
    }
    return D3DERR_NOTAVAILABLE;
}
}
