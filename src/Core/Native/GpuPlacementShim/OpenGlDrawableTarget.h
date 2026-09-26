#pragma once
#include "OpenGlIcd.h"
#include <array>
#include <memory>

namespace ResourceManagerOpenGl
{
class DrawableTarget final
{
    DrawableTarget(HMODULE value, const IcdExports& entries, LUID adapter,
        const std::array<HMODULE, 2>& implementations,
        size_t implementationCount)
        : module(value), driver(entries), adapterLuid(adapter),
          callerModules(implementations), callerModuleCount(implementationCount) {}
public:
    // The installing runtime retains the loaded modules for the lifetime of its hooks.
    const HMODULE module;
    const IcdExports driver;
    const LUID adapterLuid;
    const std::array<HMODULE, 2> callerModules;
    const size_t callerModuleCount;

    static std::shared_ptr<const DrawableTarget> Create(HMODULE module, const IcdExports& entries,
        LUID adapter,
        const std::array<HMODULE, 2>& implementations, size_t implementationCount)
    {
        const DWORD error = GetLastError();
        if (!module ||
            (!adapter.LowPart && !adapter.HighPart) || !implementationCount ||
            implementationCount > implementations.size() || !entries.validateVersion ||
            !entries.setCallbacks || !entries.describePixelFormat || !entries.setPixelFormat ||
            !entries.createLayerContext || !entries.setContext || !entries.releaseContext ||
            !entries.deleteContext || !entries.getProcAddress || !entries.shareLists ||
            !entries.copyContext || !entries.swapBuffers) {
            SetLastError(ERROR_INVALID_PARAMETER);
            return nullptr;
        }
        for (size_t i = 0; i < implementationCount; ++i) {
            if (!implementations[i] || (i && implementations[i] == implementations[0])) {
                SetLastError(ERROR_INVALID_PARAMETER);
                return nullptr;
            }
        }
        try {
            auto result = std::shared_ptr<const DrawableTarget>(new DrawableTarget(module, entries,
                adapter, implementations, implementationCount));
            SetLastError(error);
            return result;
        } catch (const std::bad_alloc&) {
            SetLastError(ERROR_NOT_ENOUGH_MEMORY);
            return nullptr;
        }
    }

    bool OwnsCaller(HMODULE value) const noexcept
    {
        for (size_t i = 0; i < callerModuleCount; ++i)
            if (callerModules[i] == value) return true;
        return false;
    }
};
}
