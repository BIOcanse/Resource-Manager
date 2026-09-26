#pragma once
#include "../GpuPlacementShim/GpuPlacementDeviceObservation.h"
#include <stdexcept>
#include <cstring>

namespace GpuObservationProbe
{
inline ResourceManagerGpuObservation::Snapshot Read(HMODULE provider)
{
    using ReadFunction = DWORD(WINAPI*)(LPVOID);
    const auto address = GetProcAddress(provider, "ResourceManagerGpuPlacementReadDeviceObservations");
    ReadFunction read = nullptr;
    static_assert(sizeof(read) == sizeof(address));
    std::memcpy(&read, &address, sizeof(read));
    ResourceManagerGpuObservation::Snapshot snapshot;
    if (!read || read(&snapshot) != 1 || snapshot.byteSize != sizeof(snapshot) || snapshot.version != 3)
        throw std::runtime_error("read-device-observations");
    return snapshot;
}

inline ResourceManagerGpuObservation::DeviceObservation Read(HMODULE provider, ResourceManagerGpuObservation::Api api)
{
    return Read(provider).api[static_cast<uint32_t>(api)];
}
}
