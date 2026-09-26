#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dxgi.h>
#include <d3d11.h>

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <string>

using D3D11CreateDeviceFn = HRESULT(WINAPI*)(
    IDXGIAdapter*,
    D3D_DRIVER_TYPE,
    HMODULE,
    UINT,
    const D3D_FEATURE_LEVEL*,
    UINT,
    UINT,
    ID3D11Device**,
    D3D_FEATURE_LEVEL*,
    ID3D11DeviceContext**);

using D3D11CreateDeviceAndSwapChainFn = HRESULT(WINAPI*)(
    IDXGIAdapter*,
    D3D_DRIVER_TYPE,
    HMODULE,
    UINT,
    const D3D_FEATURE_LEVEL*,
    UINT,
    UINT,
    const DXGI_SWAP_CHAIN_DESC*,
    IDXGISwapChain**,
    ID3D11Device**,
    D3D_FEATURE_LEVEL*,
    ID3D11DeviceContext**);

static HMODULE g_realD3D11 = nullptr;
static D3D11CreateDeviceFn g_realCreateDevice = nullptr;
static D3D11CreateDeviceAndSwapChainFn g_realCreateDeviceAndSwapChain = nullptr;

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

template <typename T>
static T ResolveProcAddress(HMODULE module, const char* name)
{
    FARPROC proc = GetProcAddress(module, name);
    T typed{};
    static_assert(sizeof(typed) == sizeof(proc), "Function pointer size mismatch.");
    std::memcpy(&typed, &proc, sizeof(typed));
    return typed;
}

static std::wstring Utf8ToWide(const char* value)
{
    if (value == nullptr || value[0] == '\0')
    {
        return {};
    }

    int required = MultiByteToWideChar(CP_UTF8, 0, value, -1, nullptr, 0);
    if (required <= 1)
    {
        return {};
    }

    std::wstring result(static_cast<size_t>(required - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value, -1, result.data(), required);
    return result;
}

static std::string ToLowerAscii(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

static bool IsHexDigit(char value)
{
    return (value >= '0' && value <= '9')
        || (value >= 'a' && value <= 'f')
        || (value >= 'A' && value <= 'F');
}

static bool TryReadHex32(const std::string& text, size_t offset, uint32_t& value)
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
    unsigned long parsed = std::strtoul(buffer, &end, 16);
    if (end == nullptr || *end != '\0')
    {
        return false;
    }

    value = static_cast<uint32_t>(parsed);
    return true;
}

static bool TryParseLuidToken(const std::string& text, LUID& luid)
{
    for (size_t offset = 0; offset + 21 <= text.size(); ++offset)
    {
        uint32_t high = 0;
        uint32_t low = 0;
        if (!TryReadHex32(text, offset, high))
        {
            continue;
        }

        size_t separator = offset + 10;
        if (separator >= text.size() || (text[separator] != '_' && text[separator] != ':'))
        {
            continue;
        }

        size_t lowOffset = separator + 1;
        if (!TryReadHex32(text, lowOffset, low))
        {
            continue;
        }

        luid.HighPart = static_cast<LONG>(high);
        luid.LowPart = static_cast<DWORD>(low);
        return true;
    }

    return false;
}

static HMODULE LoadRealD3D11()
{
    if (g_realD3D11 != nullptr)
    {
        return g_realD3D11;
    }

    wchar_t systemDirectory[MAX_PATH]{};
    UINT length = GetSystemDirectoryW(systemDirectory, MAX_PATH);
    if (length == 0 || length >= MAX_PATH)
    {
        return nullptr;
    }

    std::wstring path(systemDirectory);
    path += L"\\d3d11.dll";
    g_realD3D11 = LoadLibraryW(path.c_str());
    if (g_realD3D11 == nullptr)
    {
        return nullptr;
    }

    g_realCreateDevice = ResolveProcAddress<D3D11CreateDeviceFn>(g_realD3D11, "D3D11CreateDevice");
    g_realCreateDeviceAndSwapChain = ResolveProcAddress<D3D11CreateDeviceAndSwapChainFn>(g_realD3D11, "D3D11CreateDeviceAndSwapChain");

    return g_realD3D11;
}

static GpuShimPolicy ReadPolicyFile()
{
    char policyPathUtf8[2048]{};
    DWORD length = GetEnvironmentVariableA("RM_GPU_SHIM_POLICY_FILE", policyPathUtf8, sizeof(policyPathUtf8));
    if (length == 0 || length >= sizeof(policyPathUtf8))
    {
        return {};
    }

    std::wstring path = Utf8ToWide(policyPathUtf8);
    if (path.empty())
    {
        return {};
    }

    HANDLE file = CreateFileW(
        path.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return {};
    }

    char buffer[1024]{};
    DWORD read = 0;
    BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
    CloseHandle(file);
    if (!ok)
    {
        return {};
    }

    buffer[read] = '\0';
    std::string policy = ToLowerAscii(buffer);

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

static bool SameLuid(const LUID& left, const LUID& right)
{
    return left.LowPart == right.LowPart && left.HighPart == right.HighPart;
}

static bool IsBetterAdapter(
    const DXGI_ADAPTER_DESC1& candidate,
    SIZE_T selectedDedicatedVideoMemory,
    GpuShimPolicyMode mode)
{
    if (mode == GpuShimPolicyMode::LowPower)
    {
        return candidate.DedicatedVideoMemory < selectedDedicatedVideoMemory;
    }

    return candidate.DedicatedVideoMemory > selectedDedicatedVideoMemory;
}

static IDXGIAdapter1* SelectAdapter(const GpuShimPolicy& policy)
{
    if (policy.mode == GpuShimPolicyMode::Default)
    {
        return nullptr;
    }

    IDXGIFactory1* factory = nullptr;
    HRESULT hr = CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory));
    if (FAILED(hr) || factory == nullptr)
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
        hr = factory->EnumAdapters1(index, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND)
        {
            break;
        }

        if (FAILED(hr) || adapter == nullptr)
        {
            continue;
        }

        DXGI_ADAPTER_DESC1 desc{};
        hr = adapter->GetDesc1(&desc);
        if (SUCCEEDED(hr) && (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0)
        {
            if (policy.mode == GpuShimPolicyMode::TargetLuid && SameLuid(desc.AdapterLuid, policy.targetLuid))
            {
                if (selected != nullptr)
                {
                    selected->Release();
                }

                selected = adapter;
                break;
            }

            if (policy.mode != GpuShimPolicyMode::TargetLuid
                && (selected == nullptr || IsBetterAdapter(desc, selectedDedicatedVideoMemory, policy.mode)))
            {
                if (selected != nullptr)
                {
                    selected->Release();
                }

                selected = adapter;
                selectedDedicatedVideoMemory = desc.DedicatedVideoMemory;
                continue;
            }
        }

        adapter->Release();
    }

    factory->Release();
    return selected;
}

static bool CanReplaceAdapter(D3D_DRIVER_TYPE driverType)
{
    return driverType == D3D_DRIVER_TYPE_HARDWARE || driverType == D3D_DRIVER_TYPE_UNKNOWN;
}

static IDXGIAdapter1* SelectReplacementAdapter(D3D_DRIVER_TYPE driverType)
{
    if (!CanReplaceAdapter(driverType))
    {
        return nullptr;
    }

    return SelectAdapter(ReadPolicyFile());
}

extern "C" __declspec(dllexport) HRESULT WINAPI D3D11CreateDevice(
    IDXGIAdapter* pAdapter,
    D3D_DRIVER_TYPE DriverType,
    HMODULE Software,
    UINT Flags,
    const D3D_FEATURE_LEVEL* pFeatureLevels,
    UINT FeatureLevels,
    UINT SDKVersion,
    ID3D11Device** ppDevice,
    D3D_FEATURE_LEVEL* pFeatureLevel,
    ID3D11DeviceContext** ppImmediateContext)
{
    LoadRealD3D11();
    if (g_realCreateDevice == nullptr)
    {
        return E_FAIL;
    }

    IDXGIAdapter1* replacement = nullptr;
    if ((replacement = SelectReplacementAdapter(DriverType)) != nullptr)
    {
        pAdapter = replacement;
        DriverType = D3D_DRIVER_TYPE_UNKNOWN;
    }

    HRESULT hr = g_realCreateDevice(
        pAdapter,
        DriverType,
        Software,
        Flags,
        pFeatureLevels,
        FeatureLevels,
        SDKVersion,
        ppDevice,
        pFeatureLevel,
        ppImmediateContext);

    if (replacement != nullptr)
    {
        replacement->Release();
    }

    return hr;
}

extern "C" __declspec(dllexport) HRESULT WINAPI D3D11CreateDeviceAndSwapChain(
    IDXGIAdapter* pAdapter,
    D3D_DRIVER_TYPE DriverType,
    HMODULE Software,
    UINT Flags,
    const D3D_FEATURE_LEVEL* pFeatureLevels,
    UINT FeatureLevels,
    UINT SDKVersion,
    const DXGI_SWAP_CHAIN_DESC* pSwapChainDesc,
    IDXGISwapChain** ppSwapChain,
    ID3D11Device** ppDevice,
    D3D_FEATURE_LEVEL* pFeatureLevel,
    ID3D11DeviceContext** ppImmediateContext)
{
    LoadRealD3D11();
    if (g_realCreateDeviceAndSwapChain == nullptr)
    {
        return E_FAIL;
    }

    IDXGIAdapter1* replacement = nullptr;
    if ((replacement = SelectReplacementAdapter(DriverType)) != nullptr)
    {
        pAdapter = replacement;
        DriverType = D3D_DRIVER_TYPE_UNKNOWN;
    }

    HRESULT hr = g_realCreateDeviceAndSwapChain(
        pAdapter,
        DriverType,
        Software,
        Flags,
        pFeatureLevels,
        FeatureLevels,
        SDKVersion,
        pSwapChainDesc,
        ppSwapChain,
        ppDevice,
        pFeatureLevel,
        ppImmediateContext);

    if (replacement != nullptr)
    {
        replacement->Release();
    }

    return hr;
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        LoadRealD3D11();
    }

    return TRUE;
}
