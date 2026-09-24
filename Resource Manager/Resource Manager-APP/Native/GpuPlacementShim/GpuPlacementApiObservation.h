#pragma once
#include <windows.h>
#include <cstdint>

namespace ResourceManagerGpuObservation
{
// Match Domain/GpuPlacement/GpuGraphicsApi, not the device Api ordinal.
enum class CallApi : uint32_t { D3D11 = 1, D3D12 = 2, Vulkan = 4, OpenGL = 8, D3D9 = 16 };

struct CallObservationRequest
{
    uint32_t byteSize = sizeof(CallObservationRequest);
    uint32_t version = 1;
    uint32_t apis = 0;
    uint32_t durationMilliseconds = 0;
};

struct CallObservationSnapshot
{
    uint32_t byteSize = sizeof(CallObservationSnapshot);
    uint32_t version = 1;
    uint32_t observedApis = 0;
    uint32_t recording = 0;
};
static_assert(sizeof(CallObservationRequest) == 16);
static_assert(sizeof(CallObservationSnapshot) == 16);

class CallStore
{
    SRWLOCK lock_ = SRWLOCK_INIT;
    uint32_t requestedApis_ = 0;
    uint32_t observedApis_ = 0;
    uint32_t durationMilliseconds_ = 0;
    ULONGLONG startedAt_ = 0;
    bool enabled_ = false;

    bool IsRecording() const noexcept
    {
        return enabled_ && GetTickCount64() - startedAt_ < durationMilliseconds_;
    }

public:
    bool Recording() noexcept
    {
        AcquireSRWLockShared(&lock_);
        const bool recording = IsRecording();
        ReleaseSRWLockShared(&lock_);
        return recording;
    }

    DWORD Start(uint32_t apis, uint32_t durationMilliseconds) noexcept
    {
        if (!apis || (apis & ~uint32_t{31}) || !durationMilliseconds) return ERROR_INVALID_PARAMETER;
        AcquireSRWLockExclusive(&lock_);
        if (IsRecording()) {
            ReleaseSRWLockExclusive(&lock_);
            return ERROR_BUSY;
        }
        requestedApis_ = apis;
        observedApis_ = 0;
        durationMilliseconds_ = durationMilliseconds;
        startedAt_ = GetTickCount64();
        enabled_ = true;
        ReleaseSRWLockExclusive(&lock_);
        return ERROR_SUCCESS;
    }

    void Record(CallApi api) noexcept
    {
        const DWORD error = GetLastError();
        AcquireSRWLockExclusive(&lock_);
        if (IsRecording()) observedApis_ |= static_cast<uint32_t>(api) & requestedApis_;
        ReleaseSRWLockExclusive(&lock_);
        SetLastError(error);
    }

    void Stop() noexcept
    {
        AcquireSRWLockExclusive(&lock_);
        enabled_ = false;
        ReleaseSRWLockExclusive(&lock_);
    }

    DWORD CopyTo(void* buffer, bool stop = false) noexcept
    {
        if (!buffer) return ERROR_INVALID_PARAMETER;
        auto& output = *static_cast<CallObservationSnapshot*>(buffer);
        if (output.byteSize != sizeof(output) || output.version != 1) return ERROR_INVALID_DATA;
        AcquireSRWLockExclusive(&lock_);
        if (stop) enabled_ = false;
        output = {sizeof(output), 1, observedApis_, IsRecording() ? 1u : 0u};
        ReleaseSRWLockExclusive(&lock_);
        return ERROR_SUCCESS;
    }
};
}
