#pragma once
#include "OpenGlRuntimeState.h"
#include <stdexcept>

namespace ResourceManagerOpenGl::Runtime
{
// The installing Provider owns module references. These functions prepare records only.
std::shared_ptr<const DrawableTarget> PrepareTarget(HMODULE module, const IcdExports& entries,
    LUID adapter)
{
    const DWORD incoming = GetLastError();
    if (!module || !Callable(Callback(entries.createLayerContext)) ||
        !Callable(Callback(entries.getProcAddress))) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    const PROC declared = Callback(entries.createLayerContext);
    const PROC attributes = entries.getProcAddress("wglCreateContextAttribsARB");
    std::array<HMODULE, 2> implementations{};
    size_t count{};
    for (const PROC entry : {declared, attributes}) {
        if (!Callable(entry)) continue;
        MEMORY_BASIC_INFORMATION memory{};
        if (!VirtualQuery(reinterpret_cast<const void*>(entry), &memory, sizeof(memory)))
            return nullptr;
        const auto implementation = static_cast<HMODULE>(memory.AllocationBase);
        bool present = false;
        for (size_t i = 0; i < count; ++i) present = present || implementations[i] == implementation;
        if (!present) implementations[count++] = implementation;
    }
    auto result = DrawableTarget::Create(module, entries, adapter,
        implementations, count);
    if (result) SetLastError(incoming);
    return result;
}

std::shared_ptr<const PixelFormatDirectory> PrepareFormatDirectory(HDC dc,
    decltype(&DrvDescribePixelFormat) sourceDescribe,
    decltype(&DrvDescribePixelFormat) targetDescribe,
    decltype(&DescribePixelFormat) publicDescribe, int maximumFormats)
{
    const DWORD incoming = GetLastError();
    if (!dc || !sourceDescribe || !targetDescribe || !publicDescribe || maximumFormats <= 0) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    PIXELFORMATDESCRIPTOR descriptor{};
    const int sourceCount = sourceDescribe(dc, 1, sizeof(descriptor), &descriptor);
    if (sourceCount <= 0) return nullptr;
    const int publicCount = publicDescribe(dc, 1, 0, nullptr);
    if (publicCount <= 0) return nullptr;
    if (sourceCount > publicCount || publicCount > maximumFormats) {
        SetLastError(ERROR_INVALID_DATA);
        return nullptr;
    }
    const int targetCount = targetDescribe(dc, 1, sizeof(descriptor), &descriptor);
    if (targetCount <= 0) return nullptr;
    const int genericCount = publicCount - sourceCount;
    if (targetCount > maximumFormats - genericCount) {
        SetLastError(ERROR_INVALID_DATA);
        return nullptr;
    }
    try {
        std::vector<PIXELFORMATDESCRIPTOR> formats;
        formats.reserve(static_cast<size_t>(targetCount) + static_cast<size_t>(genericCount));
        // Keep zero-based loop counters: a legal INT_MAX count must not overflow at ++i.
        for (int i = 0; i < targetCount; ++i) {
            PIXELFORMATDESCRIPTOR value{};
            const int result = targetDescribe(dc, i + 1, sizeof(value), &value);
            if (result <= 0) return nullptr;
            if (result != targetCount) {
                SetLastError(ERROR_INVALID_DATA);
                return nullptr;
            }
            formats.push_back(value);
        }
        for (int i = sourceCount; i < publicCount; ++i) {
            PIXELFORMATDESCRIPTOR value{};
            const int result = publicDescribe(dc, i + 1, sizeof(value), &value);
            if (result <= 0) return nullptr;
            if (result != publicCount) {
                SetLastError(ERROR_INVALID_DATA);
                return nullptr;
            }
            formats.push_back(value);
        }
        auto result = PixelFormatDirectory::Create(std::move(formats), targetCount);
        if (result) SetLastError(incoming);
        return result;
    } catch (const std::bad_alloc&) {
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
    } catch (const std::length_error&) {
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
    }
}
}
