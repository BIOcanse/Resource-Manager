#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <cwchar>

namespace OverlayProtocol
{
constexpr uint32_t Magic = 0x564F4D52;
constexpr uint32_t Version = 1;
constexpr uint32_t HeaderBytes = 80;
constexpr uint32_t MaximumPixelBytes = 64u * 1024u * 1024u;

struct alignas(8) Header
{
    uint32_t magic;
    uint32_t version;
    uint32_t headerBytes;
    uint32_t pixelCapacity;
    volatile LONG64 generation;
    volatile LONG64 consumedGeneration;
    volatile LONG64 frameCounter;
    volatile LONG64 drawSuccessCounter;
    uint64_t targetWindow;
    uint32_t width;
    uint32_t height;
    uint32_t stride;
    volatile LONG enabled;
    volatile LONG swapChainWidth;
    volatile LONG swapChainHeight;
};
static_assert(sizeof(Header) == HeaderBytes);
static_assert(offsetof(Header, generation) == 16);
static_assert(offsetof(Header, swapChainWidth) == 72);

inline bool SectionName(wchar_t (&name)[96])
{
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) return false;
    const uint64_t start = (uint64_t(created.dwHighDateTime) << 32) | created.dwLowDateTime;
    return swprintf(name, 96, L"Local\\ResourceManager.PerformanceOverlay.%08X.%016llX",
        GetCurrentProcessId(), static_cast<unsigned long long>(start)) > 0;
}

inline LONG64 Read64(volatile LONG64* value) { return InterlockedCompareExchange64(value, 0, 0); }
inline LONG Read32(volatile LONG* value) { return InterlockedCompareExchange(value, 0, 0); }
}
