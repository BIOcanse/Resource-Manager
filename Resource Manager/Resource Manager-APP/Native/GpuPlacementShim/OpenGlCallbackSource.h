#pragma once
#include "OpenGlIcd.h"
#include <array>
#include <cstdint>
#include <string>
#include <vector>

namespace ResourceManagerOpenGl
{
struct IcdCallbackModule
{
    std::wstring path;
    DWORD imageBytes{}, timestamp{};
    WORD machine{};
    std::array<unsigned char, 32> sha256{};
};

struct IcdCallbackReference
{
    // Zero denotes an absent entry; other values are one-based module indices.
    uint32_t module{}, rva{};
};

struct IcdCallbackSource
{
    std::vector<IcdCallbackModule> modules;
    std::array<IcdCallbackReference, 9> entries{};
};

class ResolvedIcdCallbacks
{
public:
    ResolvedIcdCallbacks() = default;
    ~ResolvedIcdCallbacks();
    ResolvedIcdCallbacks(const ResolvedIcdCallbacks&) = delete;
    ResolvedIcdCallbacks& operator=(const ResolvedIcdCallbacks&) = delete;
    ResolvedIcdCallbacks(ResolvedIcdCallbacks&& other) noexcept;
    ResolvedIcdCallbacks& operator=(ResolvedIcdCallbacks&& other) noexcept;
    const WGLCALLBACKS& Callbacks() const noexcept { return callbacks; }

private:
    WGLCALLBACKS callbacks{};
    std::vector<HMODULE> modules;
    friend BOOL ResolveIcdCallbackSource(const IcdCallbackSource&, ResolvedIcdCallbacks&);
};

// Explicit preparation only. Both operations leave output unchanged on failure.
BOOL DescribeIcdCallbackSource(const WGLCALLBACKS& callbacks, IcdCallbackSource& output);
BOOL ResolveIcdCallbackSource(const IcdCallbackSource& source, ResolvedIcdCallbacks& output);
}
