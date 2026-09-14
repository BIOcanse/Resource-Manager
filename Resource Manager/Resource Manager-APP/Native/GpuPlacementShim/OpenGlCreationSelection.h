#pragma once
#include "OpenGlDeviceObservations.h"
#include "GpuPlacementPolicy.h"

namespace ResourceManagerOpenGl::Runtime
{
using CreationPolicyReader = ResourceManagerGpuPolicy::GpuShimPolicy(*)();
inline std::atomic<CreationPolicyReader> creationPolicyReader{};
inline bool UseNativeObservationRoute() noexcept
{
    return apiObservations.load() && !creationPolicyReader.load();
}
// Immutable adapter formats; an absent window record still means no format has been set.
inline std::shared_ptr<const DrawableSnapshot> unformattedWindowFormats;

inline DrawableSnapshot ReadWindowFormats(HDC dc)
{
    auto result = drawables.Read(dc);
    if (result) return result;
    DWORD process{};
    const HWND window = dc ? WindowFromDC(dc) : nullptr;
    if (!window || !GetWindowThreadProcessId(window, &process) || process != GetCurrentProcessId())
        return result;
    const auto formats = std::atomic_load(&unformattedWindowFormats);
    return formats ? *formats : result;
}

struct CreationSelection
{
    HDC dc{};
    DrawableSnapshot drawable;
    bool target{};
    DWORD error{};
};
// Owned only by the existing outer creation call, not a retained policy cache.
inline thread_local const CreationSelection* activeCreationSelection{};

inline CreationSelection SelectCreation(HDC dc)
{
    if (activeCreationSelection && activeCreationSelection->dc == dc)
        return *activeCreationSelection;
    CreationSelection result{dc, ReadWindowFormats(dc), false, 0};
    LUID requested{};
    if (activeCreationSelection) {
        result.target = activeCreationSelection->target;
        result.error = activeCreationSelection->error;
        if (!result.target || result.error) return result;
        requested = activeCreationSelection->drawable.target->adapterLuid;
    } else if (internalContextWork) {
        result.target = bool(result.drawable);
        return result;
    } else {
        const auto read = creationPolicyReader.load();
        if (!read) { result.error = ERROR_NOT_READY; return result; }
        const auto policy = read();
        if (policy.mode == ResourceManagerGpuPolicy::GpuShimPolicyMode::Default) return result;
        if (policy.mode == ResourceManagerGpuPolicy::GpuShimPolicyMode::TargetLuid) requested = policy.targetLuid;
        else {
            auto* adapter = ResourceManagerGpuPolicy::SelectAdapter(policy);
            if (!adapter) { result.error = ERROR_NOT_FOUND; return result; }
            DXGI_ADAPTER_DESC1 description{};
            const HRESULT described = adapter->GetDesc1(&description);
            adapter->Release();
            if (FAILED(described)) { result.error = ERROR_GEN_FAILURE; return result; }
            requested = description.AdapterLuid;
        }
        result.target = true;
    }
    if (!result.drawable.target) result.error = ERROR_NOT_READY;
    else if (!ResourceManagerGpuPolicy::SameLuid(requested, result.drawable.target->adapterLuid))
        result.error = ERROR_NOT_SUPPORTED;
    return result;
}
}
