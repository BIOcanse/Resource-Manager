#pragma once
#include <windows.h>
#include <dxgi.h>
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>

namespace ResourceManagerGpuPolicy
{
enum class GpuShimPolicyMode
{
    Default,
    LowPower,
    HighPerformance,
    TargetLuid
};

struct GpuShimPolicy
{
    GpuShimPolicyMode mode = GpuShimPolicyMode::Default;
    LUID targetLuid{};
};

inline std::string ToLowerAscii(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

inline bool IsHexDigit(char value)
{
    return (value >= '0' && value <= '9')
        || (value >= 'a' && value <= 'f')
        || (value >= 'A' && value <= 'F');
}

inline bool TryReadHex32(const std::string& text, size_t offset, uint32_t& value)
{
    if (offset + 10 > text.size()
        || text[offset] != '0'
        || (text[offset + 1] != 'x' && text[offset + 1] != 'X'))
    {
        return false;
    }

    for (size_t index = offset + 2; index < offset + 10; ++index)
    {
        if (!IsHexDigit(text[index]))
        {
            return false;
        }
    }

    char buffer[9]{};
    std::memcpy(buffer, text.data() + offset + 2, 8);
    char* end = nullptr;
    const unsigned long parsed = std::strtoul(buffer, &end, 16);
    if (end == nullptr || *end != '\0')
    {
        return false;
    }

    value = static_cast<uint32_t>(parsed);
    return true;
}

inline bool TryParseLuidToken(const std::string& text, LUID& luid)
{
    for (size_t offset = 0; offset + 21 <= text.size(); ++offset)
    {
        uint32_t high = 0;
        uint32_t low = 0;
        if (!TryReadHex32(text, offset, high))
        {
            continue;
        }

        const size_t separator = offset + 10;
        if (separator >= text.size() || (text[separator] != '_' && text[separator] != ':'))
        {
            continue;
        }

        if (!TryReadHex32(text, separator + 1, low))
        {
            continue;
        }

        luid.HighPart = static_cast<LONG>(high);
        luid.LowPart = static_cast<DWORD>(low);
        return true;
    }

    return false;
}

inline GpuShimPolicy ReadPolicyFile(const std::wstring& path, bool* fileMissing = nullptr)
{
    if (fileMissing) *fileMissing = false;
    if (path.empty())
    {
        return {};
    }

    const HANDLE file = CreateFileW(
        path.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        const DWORD error = GetLastError();
        if (fileMissing) *fileMissing = error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
        return {};
    }

    char buffer[1024]{};
    DWORD bytesRead = 0;
    const BOOL readSucceeded = ReadFile(file, buffer, sizeof(buffer) - 1, &bytesRead, nullptr);
    CloseHandle(file);
    if (!readSucceeded)
    {
        return {};
    }

    buffer[bytesRead] = '\0';
    const std::string policy = ToLowerAscii(buffer);
    GpuShimPolicy result{};
    if (TryParseLuidToken(policy, result.targetLuid))
    {
        result.mode = GpuShimPolicyMode::TargetLuid;
        return result;
    }

    if (policy.find("lowpower") != std::string::npos
        || policy.find("low-power") != std::string::npos
        || policy.find("integratedgpu") != std::string::npos)
    {
        result.mode = GpuShimPolicyMode::LowPower;
    }
    else if (policy.find("highperformance") != std::string::npos
        || policy.find("high-performance") != std::string::npos
        || policy.find("dedicatedgpu") != std::string::npos)
    {
        result.mode = GpuShimPolicyMode::HighPerformance;
    }

    return result;
}

inline GpuShimPolicy ReadCurrentPolicy(const std::wstring& runtimePath)
{
    wchar_t startupPath[2048]{};
    const DWORD length = GetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", startupPath, 2048);
    if (length >= 2048) startupPath[0] = L'\0';
    if (!runtimePath.empty())
    {
        bool missing = false;
        const auto policy = ReadPolicyFile(runtimePath, &missing);
        if (!missing || _wcsicmp(runtimePath.c_str(), startupPath) == 0) return policy;
    }
    return ReadPolicyFile(startupPath);
}

inline bool SameLuid(const LUID& left, const LUID& right)
{
    return left.LowPart == right.LowPart && left.HighPart == right.HighPart;
}

inline bool IsBetterAdapter(
    const DXGI_ADAPTER_DESC1& candidate,
    SIZE_T selectedDedicatedVideoMemory,
    GpuShimPolicyMode mode)
{
    return mode == GpuShimPolicyMode::LowPower
        ? candidate.DedicatedVideoMemory < selectedDedicatedVideoMemory
        : candidate.DedicatedVideoMemory > selectedDedicatedVideoMemory;
}

inline IDXGIAdapter1* SelectAdapter(const GpuShimPolicy& policy)
{
    if (policy.mode == GpuShimPolicyMode::Default)
    {
        return nullptr;
    }

    IDXGIFactory1* factory = nullptr;
    const HRESULT factoryResult = CreateDXGIFactory1(
        __uuidof(IDXGIFactory1),
        reinterpret_cast<void**>(&factory));
    if (FAILED(factoryResult) || factory == nullptr)
    {
        return nullptr;
    }

    IDXGIAdapter1* selected = nullptr;
    SIZE_T selectedDedicatedVideoMemory = policy.mode == GpuShimPolicyMode::LowPower
        ? static_cast<SIZE_T>(-1)
        : 0;
    for (UINT index = 0;; ++index)
    {
        IDXGIAdapter1* adapter = nullptr;
        const HRESULT enumerationResult = factory->EnumAdapters1(index, &adapter);
        if (enumerationResult == DXGI_ERROR_NOT_FOUND)
        {
            break;
        }

        if (FAILED(enumerationResult) || adapter == nullptr)
        {
            if (adapter != nullptr) adapter->Release();
            if (selected != nullptr) selected->Release();
            selected = nullptr;
            break;
        }

        DXGI_ADAPTER_DESC1 description{};
        if (SUCCEEDED(adapter->GetDesc1(&description))
            && (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0)
        {
            if (policy.mode == GpuShimPolicyMode::TargetLuid
                && SameLuid(description.AdapterLuid, policy.targetLuid))
            {
                if (selected != nullptr)
                {
                    selected->Release();
                }

                selected = adapter;
                break;
            }

            if (policy.mode != GpuShimPolicyMode::TargetLuid
                && (selected == nullptr
                    || IsBetterAdapter(description, selectedDedicatedVideoMemory, policy.mode)))
            {
                if (selected != nullptr)
                {
                    selected->Release();
                }

                selected = adapter;
                selectedDedicatedVideoMemory = description.DedicatedVideoMemory;
                continue;
            }
        }

        adapter->Release();
    }

    factory->Release();
    return selected;
}

}
