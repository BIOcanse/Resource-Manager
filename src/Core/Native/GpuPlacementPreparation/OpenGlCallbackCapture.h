#pragma once
#include "../GpuPlacementShim/OpenGlCallbackSource.h"

namespace ResourceManagerOpenGl
{
struct IcdCallbackAcquisition
{
    DWORD error{}, cleanupError{};
    bool Succeeded() const noexcept { return !error && !cleanupError; }
};

// One explicit invocation in a fresh helper process. Never used inside the injected provider.
IcdCallbackAcquisition AcquireIcdCallbackSource(LUID sourceAdapter, IcdCallbackSource& output);
}
