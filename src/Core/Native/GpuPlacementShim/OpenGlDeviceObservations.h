#pragma once
#include "OpenGlDeviceIdentity.h"
#include "OpenGlCoreRoutes.h"

namespace ResourceManagerOpenGl::Runtime
{
// Only the latest returned context can complete the original latest observation.
// These fields are protected by bindingLock and do not own context lifetime.
inline ResourceManagerGpuObservation::Store* deviceObservations{};
inline DeviceIdentityQueryLimits deviceQueryLimits{};
inline HGLRC pendingObservationContext{};
inline uint64_t pendingObservationCount{};

inline void ConfigureDeviceObservations(ResourceManagerGpuObservation::Store& store, DeviceIdentityQueryLimits limits)
{
    std::lock_guard<std::mutex> lock(bindingLock);
    deviceObservations = &store;
    deviceQueryLimits = limits;
}

class InternalContextWork
{
    const bool previous_ = internalContextWork;
public:
    InternalContextWork() { internalContextWork = true; }
    ~InternalContextWork() { internalContextWork = previous_; }
    InternalContextWork(const InternalContextWork&) = delete;
    InternalContextWork& operator=(const InternalContextWork&) = delete;
};

// Called by successful bind/delete while the original binding lock is held.
inline void ObserveCurrentContext(HGLRC handle)
{
    if (internalContextWork || !deviceObservations || !handle || handle != pendingObservationContext) return;
    const DWORD error = GetLastError();
    DeviceIdentityQueries queries;
    queries.text = current ? currentTable->glDispatchTable.glGetString : Core_glGetString::original;
    queries.integer = current ? currentTable->glDispatchTable.glGetIntegerv : Core_glGetIntegerv::original;
    const auto resolve = current ? current->driver.getProcAddress : systemLookup;
    const auto indexed = resolve("glGetStringi");
    const auto bytes = resolve("glGetUnsignedBytevEXT");
    if (Callable(indexed)) std::memcpy(&queries.indexedText, &indexed, sizeof(indexed));
    if (Callable(bytes)) std::memcpy(&queries.bytes, &bytes, sizeof(bytes));
    const auto identity = QueryCurrentDeviceIdentity(queries, deviceQueryLimits);
    deviceObservations->CompleteIdentity(ResourceManagerGpuObservation::Api::OpenGL,
        pendingObservationCount, identity.identity, identity.adapter);
    pendingObservationContext = nullptr;
    pendingObservationCount = 0;
    SetLastError(error);
}

inline void ObserveDeletedContext(HGLRC handle)
{
    if (handle && handle == pendingObservationContext && !internalContextWork) {
        pendingObservationContext = nullptr;
        pendingObservationCount = 0;
    }
}
}
