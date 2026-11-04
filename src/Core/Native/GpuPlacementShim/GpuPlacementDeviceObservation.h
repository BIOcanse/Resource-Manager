#pragma once
#include <windows.h>
#include <cstdint>

namespace ResourceManagerGpuObservation
{
enum class Api : uint32_t { D3D11, D3D12, Vulkan, D3D9, OpenGL, Count };
enum class Identity : uint32_t { NoDevice, Adapter, Unavailable, MultipleAdapters };

struct DeviceObservation
{
    uint64_t returnedDeviceCount = 0;
    uint64_t adapterLuid = 0;
    Identity identity = Identity::NoDevice;
    uint32_t reserved = 0;
};

struct Snapshot
{
    uint32_t byteSize = sizeof(Snapshot);
    uint32_t version = 3;
    DeviceObservation api[static_cast<uint32_t>(Api::Count)]{};
};
static_assert(sizeof(DeviceObservation) == 24);
static_assert(sizeof(Snapshot) == 128);

inline uint64_t Pack(LUID luid)
{
    return (static_cast<uint64_t>(static_cast<uint32_t>(luid.HighPart)) << 32) | luid.LowPart;
}

class Store
{
    SRWLOCK lock_ = SRWLOCK_INIT;
    Snapshot current_{};
public:
    uint64_t Publish(Api api, Identity identity, LUID luid = {}) noexcept
    {
        AcquireSRWLockExclusive(&lock_);
        auto& value = current_.api[static_cast<uint32_t>(api)];
        ++value.returnedDeviceCount;
        value.adapterLuid = identity == Identity::Adapter ? Pack(luid) : 0;
        value.identity = identity;
        const auto count = value.returnedDeviceCount;
        ReleaseSRWLockExclusive(&lock_);
        return count;
    }

    bool CompleteIdentity(Api api, uint64_t returnedCount, Identity identity, LUID luid = {}) noexcept
    {
        AcquireSRWLockExclusive(&lock_);
        auto& value = current_.api[static_cast<uint32_t>(api)];
        const bool matches = returnedCount && value.returnedDeviceCount == returnedCount;
        if (matches) {
            value.adapterLuid = identity == Identity::Adapter ? Pack(luid) : 0;
            value.identity = identity;
        }
        ReleaseSRWLockExclusive(&lock_);
        return matches;
    }

    DWORD CopyTo(void* buffer) noexcept
    {
        if (!buffer) return 0;
        auto& output = *static_cast<Snapshot*>(buffer);
        if (output.byteSize != sizeof(Snapshot) || output.version != 3) return 0;
        AcquireSRWLockShared(&lock_);
        output = current_;
        ReleaseSRWLockShared(&lock_);
        return 1;
    }
};
}
