#pragma once
#include "OpenGlCallbackSourceCodec.h"
#include <new>
#include <utility>

namespace ResourceManagerOpenGl
{
constexpr size_t ConfigurePolicyPathCapacity = 2048;
constexpr size_t ConfigureHeaderBytes = 8;
constexpr size_t MaximumConfigureArgumentBytes = ConfigureHeaderBytes +
    (ConfigurePolicyPathCapacity - 1) * sizeof(wchar_t) + MaximumCallbackSourceBytes;

struct ConfigureArguments
{
    std::wstring policyPath;
    IcdCallbackSource source;
};

// Pure boundary decoding. Resolution and installation are explicit subsequent operations.
inline BOOL DecodeConfigureArguments(const unsigned char* bytes, size_t length, ConfigureArguments& output)
{
    static_assert(sizeof(wchar_t) == 2);
    const auto invalid = [] { SetLastError(ERROR_INVALID_DATA); return FALSE; };
    if (!bytes || length < ConfigureHeaderBytes || length > MaximumConfigureArgumentBytes) return invalid();
    const auto number = [](const unsigned char* p) {
        return static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8) |
            (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24);
    };
    const uint32_t characters = number(bytes + 4);
    if (number(bytes) != length || !characters || characters >= ConfigurePolicyPathCapacity ||
        characters > (length - ConfigureHeaderBytes) / sizeof(wchar_t)) return invalid();
    const DWORD error = GetLastError();
    try {
        ConfigureArguments candidate;
        candidate.policyPath.resize(characters);
        for (size_t i = 0; i < characters; ++i) {
            const auto p = bytes + ConfigureHeaderBytes + i * sizeof(wchar_t);
            const auto value = static_cast<wchar_t>(static_cast<unsigned>(p[0]) | (static_cast<unsigned>(p[1]) << 8));
            if (!value) return invalid();
            candidate.policyPath[i] = value;
        }
        const size_t sourceOffset = ConfigureHeaderBytes + characters * sizeof(wchar_t);
        if (!DecodeIcdCallbackSource(bytes + sourceOffset, length - sourceOffset, candidate.source)) return FALSE;
        output = std::move(candidate);
        SetLastError(error);
        return TRUE;
    } catch (const std::bad_alloc&) {
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return FALSE;
    }
}
}
