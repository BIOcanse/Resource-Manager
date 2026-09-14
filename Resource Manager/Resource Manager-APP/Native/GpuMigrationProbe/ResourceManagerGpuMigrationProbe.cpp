#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dxgi.h>
#include <dxgi1_2.h>
#include <d3d11.h>
#include <d3d11_1.h>

#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <sstream>
#include <string>
#include <vector>

struct AdapterInfo
{
    UINT index;
    std::string name;
    LUID luid;
    UINT vendorId;
    UINT deviceId;
    SIZE_T dedicatedVideoMemory;
    bool isSoftware;
    bool integratedGuess;
    IDXGIAdapter1* adapter;
};

struct StepResult
{
    std::string name;
    bool success;
    HRESULT hr;
    std::string detail;
    std::string sample;
};

struct DeviceBundle
{
    ID3D11Device* device = nullptr;
    ID3D11Device1* device1 = nullptr;
    ID3D11DeviceContext* context = nullptr;
    D3D_FEATURE_LEVEL featureLevel{};
};

struct ChildProbeResult
{
    std::string tag;
    std::string preference;
    DWORD exitCode = 0;
    std::string output;
};

struct RegistryValueBackup
{
    bool existed = false;
    DWORD type = REG_NONE;
    std::vector<BYTE> data;
};

static std::string JsonEscape(const std::string& value)
{
    std::ostringstream stream;
    for (unsigned char ch : value)
    {
        switch (ch)
        {
        case '\\': stream << "\\\\"; break;
        case '"': stream << "\\\""; break;
        case '\n': stream << "\\n"; break;
        case '\r': stream << "\\r"; break;
        case '\t': stream << "\\t"; break;
        default:
            if (ch < 0x20)
            {
                char buffer[7];
                std::snprintf(buffer, sizeof(buffer), "\\u%04x", ch);
                stream << buffer;
            }
            else
            {
                stream << ch;
            }
            break;
        }
    }

    return stream.str();
}

static std::string HrString(HRESULT hr)
{
    char buffer[16];
    std::snprintf(buffer, sizeof(buffer), "0x%08lX", static_cast<unsigned long>(static_cast<uint32_t>(hr)));
    return buffer;
}

static std::string LuidString(const LUID& luid)
{
    char buffer[32];
    std::snprintf(
        buffer,
        sizeof(buffer),
        "0x%08lX:0x%08lX",
        static_cast<unsigned long>(static_cast<uint32_t>(luid.HighPart)),
        static_cast<unsigned long>(luid.LowPart));
    return buffer;
}

static std::string GpuEngineLuidToken(const LUID& luid)
{
    char buffer[40];
    std::snprintf(
        buffer,
        sizeof(buffer),
        "luid_0x%08lx_0x%08lx",
        static_cast<unsigned long>(static_cast<uint32_t>(luid.HighPart)),
        static_cast<unsigned long>(luid.LowPart));
    return buffer;
}

static std::string WideToUtf8(const wchar_t* value)
{
    if (value == nullptr || value[0] == L'\0')
    {
        return {};
    }

    int required = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (required <= 1)
    {
        return {};
    }

    std::string result(static_cast<size_t>(required - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), required, nullptr, nullptr);
    return result;
}

static std::wstring Utf8ToWide(const std::string& value)
{
    if (value.empty())
    {
        return {};
    }

    int required = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, nullptr, 0);
    if (required <= 1)
    {
        return {};
    }

    std::wstring result(static_cast<size_t>(required - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), -1, result.data(), required);
    return result;
}

static std::wstring GetExecutablePath()
{
    std::wstring buffer(MAX_PATH, L'\0');
    for (;;)
    {
        DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (length == 0)
        {
            return {};
        }

        if (length < buffer.size() - 1)
        {
            buffer.resize(length);
            return buffer;
        }

        buffer.resize(buffer.size() * 2);
    }
}

static void SafeRelease(IUnknown* value)
{
    if (value != nullptr)
    {
        value->Release();
    }
}

static std::vector<AdapterInfo> EnumerateAdapters()
{
    std::vector<AdapterInfo> adapters;
    IDXGIFactory1* factory = nullptr;
    HRESULT hr = CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory));
    if (FAILED(hr) || factory == nullptr)
    {
        return adapters;
    }

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
        if (FAILED(hr))
        {
            adapter->Release();
            continue;
        }

        bool isSoftware = (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
        bool integratedGuess = !isSoftware && desc.DedicatedVideoMemory < (1024ull * 1024ull * 1024ull);
        adapters.push_back(AdapterInfo{
            index,
            WideToUtf8(desc.Description),
            desc.AdapterLuid,
            desc.VendorId,
            desc.DeviceId,
            desc.DedicatedVideoMemory,
            isSoftware,
            integratedGuess,
            adapter
        });
    }

    factory->Release();
    return adapters;
}

static int SelectSourceAdapter(const std::vector<AdapterInfo>& adapters)
{
    int selected = -1;
    SIZE_T bestMemory = 0;
    for (size_t i = 0; i < adapters.size(); ++i)
    {
        if (adapters[i].isSoftware)
        {
            continue;
        }

        if (selected < 0 || adapters[i].dedicatedVideoMemory > bestMemory)
        {
            selected = static_cast<int>(i);
            bestMemory = adapters[i].dedicatedVideoMemory;
        }
    }

    return selected;
}

static int SelectTargetAdapter(const std::vector<AdapterInfo>& adapters, int source)
{
    int selected = -1;
    SIZE_T bestMemory = static_cast<SIZE_T>(-1);
    for (size_t i = 0; i < adapters.size(); ++i)
    {
        if (static_cast<int>(i) == source || adapters[i].isSoftware)
        {
            continue;
        }

        if (adapters[i].integratedGuess && adapters[i].dedicatedVideoMemory < bestMemory)
        {
            selected = static_cast<int>(i);
            bestMemory = adapters[i].dedicatedVideoMemory;
        }
    }

    if (selected >= 0)
    {
        return selected;
    }

    for (size_t i = 0; i < adapters.size(); ++i)
    {
        if (static_cast<int>(i) == source || adapters[i].isSoftware)
        {
            continue;
        }

        if (adapters[i].dedicatedVideoMemory < bestMemory)
        {
            selected = static_cast<int>(i);
            bestMemory = adapters[i].dedicatedVideoMemory;
        }
    }

    return selected >= 0 ? selected : source;
}

static StepResult CreateDevice(const AdapterInfo& adapter, DeviceBundle& bundle)
{
    static const D3D_FEATURE_LEVEL featureLevels[] = {
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0
    };

    HRESULT hr = D3D11CreateDevice(
        adapter.adapter,
        D3D_DRIVER_TYPE_UNKNOWN,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        featureLevels,
        static_cast<UINT>(sizeof(featureLevels) / sizeof(featureLevels[0])),
        D3D11_SDK_VERSION,
        &bundle.device,
        &bundle.featureLevel,
        &bundle.context);

    if (SUCCEEDED(hr) && bundle.device != nullptr)
    {
        bundle.device->QueryInterface(__uuidof(ID3D11Device1), reinterpret_cast<void**>(&bundle.device1));
    }

    return StepResult{
        std::string("createDevice:") + adapter.name,
        SUCCEEDED(hr),
        hr,
        SUCCEEDED(hr) ? "D3D11 device created" : "D3D11CreateDevice failed",
        {}
    };
}

static StepResult CreateDefaultDevice(DeviceBundle& bundle)
{
    static const D3D_FEATURE_LEVEL featureLevels[] = {
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0
    };

    HRESULT hr = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        featureLevels,
        static_cast<UINT>(sizeof(featureLevels) / sizeof(featureLevels[0])),
        D3D11_SDK_VERSION,
        &bundle.device,
        &bundle.featureLevel,
        &bundle.context);

    if (SUCCEEDED(hr) && bundle.device != nullptr)
    {
        bundle.device->QueryInterface(__uuidof(ID3D11Device1), reinterpret_cast<void**>(&bundle.device1));
    }

    return StepResult{
        "createDefaultDevice",
        SUCCEEDED(hr),
        hr,
        SUCCEEDED(hr) ? "D3D11 default hardware device created" : "D3D11CreateDevice default hardware failed",
        {}
    };
}

static bool GetDeviceAdapterDesc(ID3D11Device* device, DXGI_ADAPTER_DESC1& desc)
{
    IDXGIDevice* dxgiDevice = nullptr;
    HRESULT hr = device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDevice));
    if (FAILED(hr) || dxgiDevice == nullptr)
    {
        return false;
    }

    IDXGIAdapter* adapter = nullptr;
    hr = dxgiDevice->GetAdapter(&adapter);
    dxgiDevice->Release();
    if (FAILED(hr) || adapter == nullptr)
    {
        return false;
    }

    IDXGIAdapter1* adapter1 = nullptr;
    hr = adapter->QueryInterface(__uuidof(IDXGIAdapter1), reinterpret_cast<void**>(&adapter1));
    adapter->Release();
    if (FAILED(hr) || adapter1 == nullptr)
    {
        return false;
    }

    hr = adapter1->GetDesc1(&desc);
    adapter1->Release();
    return SUCCEEDED(hr);
}

static int RunDefaultDeviceChild(const std::string& tag)
{
    DeviceBundle bundle;
    StepResult step = CreateDefaultDevice(bundle);
    DXGI_ADAPTER_DESC1 desc{};
    bool hasAdapter = step.success && bundle.device != nullptr && GetDeviceAdapterDesc(bundle.device, desc);

    std::printf(
        "{\"tag\":\"%s\",\"success\":%s,\"hr\":\"%s\",\"adapterName\":\"%s\",\"adapterLuid\":\"%s\",\"gpuEngineLuidToken\":\"%s\",\"vendorId\":%u,\"deviceId\":%u,\"dedicatedVideoMemoryMb\":%.1f}\n",
        JsonEscape(tag).c_str(),
        step.success ? "true" : "false",
        HrString(step.hr).c_str(),
        hasAdapter ? JsonEscape(WideToUtf8(desc.Description)).c_str() : "",
        hasAdapter ? LuidString(desc.AdapterLuid).c_str() : "",
        hasAdapter ? GpuEngineLuidToken(desc.AdapterLuid).c_str() : "",
        hasAdapter ? desc.VendorId : 0,
        hasAdapter ? desc.DeviceId : 0,
        hasAdapter ? static_cast<double>(desc.DedicatedVideoMemory) / 1024.0 / 1024.0 : 0.0);

    SafeRelease(bundle.context);
    SafeRelease(bundle.device1);
    SafeRelease(bundle.device);
    return step.success && hasAdapter ? 0 : 3;
}

static std::string FormatPixel(const uint8_t* pixel)
{
    char buffer[32];
    std::snprintf(buffer, sizeof(buffer), "#%02X%02X%02X%02X", pixel[0], pixel[1], pixel[2], pixel[3]);
    return buffer;
}

static HRESULT ReadBackFirstPixel(DeviceBundle& bundle, ID3D11Texture2D* texture, std::string& sample)
{
    D3D11_TEXTURE2D_DESC desc{};
    texture->GetDesc(&desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;

    ID3D11Texture2D* staging = nullptr;
    HRESULT hr = bundle.device->CreateTexture2D(&desc, nullptr, &staging);
    if (FAILED(hr))
    {
        return hr;
    }

    bundle.context->CopyResource(staging, texture);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    hr = bundle.context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped);
    if (SUCCEEDED(hr))
    {
        sample = FormatPixel(reinterpret_cast<const uint8_t*>(mapped.pData));
        bundle.context->Unmap(staging, 0);
    }

    staging->Release();
    return hr;
}

static StepResult RenderFrame(DeviceBundle& bundle, const char* stepName, UINT miscFlags, ID3D11Texture2D** outTexture)
{
    if (outTexture != nullptr)
    {
        *outTexture = nullptr;
    }

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = 64;
    desc.Height = 64;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = miscFlags;

    ID3D11Texture2D* texture = nullptr;
    HRESULT hr = bundle.device->CreateTexture2D(&desc, nullptr, &texture);
    if (FAILED(hr))
    {
        return StepResult{ stepName, false, hr, "CreateTexture2D failed", {} };
    }

    ID3D11RenderTargetView* targetView = nullptr;
    hr = bundle.device->CreateRenderTargetView(texture, nullptr, &targetView);
    if (FAILED(hr))
    {
        texture->Release();
        return StepResult{ stepName, false, hr, "CreateRenderTargetView failed", {} };
    }

    const float color[] = { 0.10f, 0.45f, 0.80f, 1.00f };
    bundle.context->ClearRenderTargetView(targetView, color);
    bundle.context->Flush();

    std::string sample;
    hr = ReadBackFirstPixel(bundle, texture, sample);
    targetView->Release();
    if (FAILED(hr))
    {
        texture->Release();
        return StepResult{ stepName, false, hr, "Readback failed after render", {} };
    }

    if (outTexture != nullptr)
    {
        *outTexture = texture;
    }
    else
    {
        texture->Release();
    }

    return StepResult{ stepName, true, S_OK, "Rendered and read back one CPU-defined frame", sample };
}

static StepResult TryOpenSharedTextureOnTarget(ID3D11Texture2D* sourceTexture, DeviceBundle& targetBundle)
{
    IDXGIResource* dxgiResource = nullptr;
    HRESULT hr = sourceTexture->QueryInterface(__uuidof(IDXGIResource), reinterpret_cast<void**>(&dxgiResource));
    if (FAILED(hr) || dxgiResource == nullptr)
    {
        return StepResult{ "openSharedTextureOnTarget", false, hr, "Source texture does not expose IDXGIResource", {} };
    }

    HANDLE sharedHandle = nullptr;
    hr = dxgiResource->GetSharedHandle(&sharedHandle);
    dxgiResource->Release();
    if (FAILED(hr) || sharedHandle == nullptr)
    {
        return StepResult{ "openSharedTextureOnTarget", false, hr, "GetSharedHandle failed", {} };
    }

    ID3D11Texture2D* openedTexture = nullptr;
    hr = targetBundle.device->OpenSharedResource(sharedHandle, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&openedTexture));
    if (FAILED(hr) || openedTexture == nullptr)
    {
        return StepResult{ "openSharedTextureOnTarget", false, hr, "Target adapter could not open source shared texture", {} };
    }

    std::string sample;
    HRESULT readbackHr = ReadBackFirstPixel(targetBundle, openedTexture, sample);
    openedTexture->Release();
    if (FAILED(readbackHr))
    {
        return StepResult{ "openSharedTextureOnTarget", false, readbackHr, "Shared texture opened, but target readback failed", {} };
    }

    return StepResult{ "openSharedTextureOnTarget", true, S_OK, "Target adapter opened and read source shared texture", sample };
}

static StepResult TryOpenNtSharedTextureOnTarget(DeviceBundle& sourceBundle, DeviceBundle& targetBundle)
{
    if (sourceBundle.device1 == nullptr || targetBundle.device1 == nullptr)
    {
        return StepResult{ "openNtSharedTextureOnTarget", false, E_NOINTERFACE, "ID3D11Device1 is unavailable on source or target device", {} };
    }

    ID3D11Texture2D* sourceTexture = nullptr;
    StepResult render = RenderFrame(
        sourceBundle,
        "renderNtSharedTextureOnSourceAdapter",
        D3D11_RESOURCE_MISC_SHARED_NTHANDLE,
        &sourceTexture);
    if (!render.success || sourceTexture == nullptr)
    {
        return StepResult{ "openNtSharedTextureOnTarget", false, render.hr, "Could not create NT shared source texture", {} };
    }

    IDXGIResource1* dxgiResource = nullptr;
    HRESULT hr = sourceTexture->QueryInterface(__uuidof(IDXGIResource1), reinterpret_cast<void**>(&dxgiResource));
    if (FAILED(hr) || dxgiResource == nullptr)
    {
        sourceTexture->Release();
        return StepResult{ "openNtSharedTextureOnTarget", false, hr, "Source texture does not expose IDXGIResource1", {} };
    }

    HANDLE sharedHandle = nullptr;
    hr = dxgiResource->CreateSharedHandle(
        nullptr,
        DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
        nullptr,
        &sharedHandle);
    dxgiResource->Release();
    if (FAILED(hr) || sharedHandle == nullptr)
    {
        sourceTexture->Release();
        return StepResult{ "openNtSharedTextureOnTarget", false, hr, "CreateSharedHandle failed for NT shared texture", {} };
    }

    ID3D11Texture2D* openedTexture = nullptr;
    hr = targetBundle.device1->OpenSharedResource1(
        sharedHandle,
        __uuidof(ID3D11Texture2D),
        reinterpret_cast<void**>(&openedTexture));
    CloseHandle(sharedHandle);
    sourceTexture->Release();
    if (FAILED(hr) || openedTexture == nullptr)
    {
        return StepResult{ "openNtSharedTextureOnTarget", false, hr, "Target adapter could not open source NT shared texture", {} };
    }

    std::string sample;
    HRESULT readbackHr = ReadBackFirstPixel(targetBundle, openedTexture, sample);
    openedTexture->Release();
    if (FAILED(readbackHr))
    {
        return StepResult{ "openNtSharedTextureOnTarget", false, readbackHr, "NT shared texture opened, but target readback failed", {} };
    }

    return StepResult{ "openNtSharedTextureOnTarget", true, S_OK, "Target adapter opened and read source NT shared texture", sample };
}

static const wchar_t* GpuPreferenceRegistrySubKey()
{
    return L"Software\\Microsoft\\DirectX\\UserGpuPreferences";
}

static RegistryValueBackup ReadGpuPreferenceBackup(const std::wstring& executablePath)
{
    RegistryValueBackup backup;
    HKEY key = nullptr;
    LONG status = RegOpenKeyExW(HKEY_CURRENT_USER, GpuPreferenceRegistrySubKey(), 0, KEY_READ, &key);
    if (status != ERROR_SUCCESS)
    {
        return backup;
    }

    DWORD type = REG_NONE;
    DWORD size = 0;
    status = RegQueryValueExW(key, executablePath.c_str(), nullptr, &type, nullptr, &size);
    if (status == ERROR_SUCCESS && size > 0)
    {
        backup.existed = true;
        backup.type = type;
        backup.data.resize(size);
        status = RegQueryValueExW(key, executablePath.c_str(), nullptr, &backup.type, backup.data.data(), &size);
        if (status != ERROR_SUCCESS)
        {
            backup = RegistryValueBackup{};
        }
    }

    RegCloseKey(key);
    return backup;
}

static bool WriteGpuPreference(const std::wstring& executablePath, int preference)
{
    HKEY key = nullptr;
    DWORD disposition = 0;
    LONG status = RegCreateKeyExW(
        HKEY_CURRENT_USER,
        GpuPreferenceRegistrySubKey(),
        0,
        nullptr,
        0,
        KEY_SET_VALUE,
        nullptr,
        &key,
        &disposition);
    if (status != ERROR_SUCCESS)
    {
        return false;
    }

    wchar_t value[64];
    std::swprintf(value, sizeof(value) / sizeof(value[0]), L"GpuPreference=%d;", preference);
    status = RegSetValueExW(
        key,
        executablePath.c_str(),
        0,
        REG_SZ,
        reinterpret_cast<const BYTE*>(value),
        static_cast<DWORD>((std::wcslen(value) + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return status == ERROR_SUCCESS;
}

static bool RestoreGpuPreference(const std::wstring& executablePath, const RegistryValueBackup& backup)
{
    HKEY key = nullptr;
    DWORD disposition = 0;
    LONG status = RegCreateKeyExW(
        HKEY_CURRENT_USER,
        GpuPreferenceRegistrySubKey(),
        0,
        nullptr,
        0,
        KEY_SET_VALUE,
        nullptr,
        &key,
        &disposition);
    if (status != ERROR_SUCCESS)
    {
        return false;
    }

    if (backup.existed)
    {
        status = RegSetValueExW(
            key,
            executablePath.c_str(),
            0,
            backup.type,
            backup.data.data(),
            static_cast<DWORD>(backup.data.size()));
    }
    else
    {
        status = RegDeleteValueW(key, executablePath.c_str());
        if (status == ERROR_FILE_NOT_FOUND)
        {
            status = ERROR_SUCCESS;
        }
    }

    RegCloseKey(key);
    return status == ERROR_SUCCESS;
}

static ChildProbeResult RunDefaultDeviceChildProcess(
    const std::wstring& executablePath,
    const std::string& tag,
    const std::string& preference)
{
    ChildProbeResult result;
    result.tag = tag;
    result.preference = preference;
    result.exitCode = static_cast<DWORD>(-1);

    SECURITY_ATTRIBUTES securityAttributes{};
    securityAttributes.nLength = sizeof(securityAttributes);
    securityAttributes.bInheritHandle = TRUE;

    HANDLE stdoutRead = nullptr;
    HANDLE stdoutWrite = nullptr;
    if (!CreatePipe(&stdoutRead, &stdoutWrite, &securityAttributes, 0))
    {
        result.output = "{\"error\":\"CreatePipe failed\"}";
        return result;
    }
    SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0);

    std::wstring commandLine = L"\"" + executablePath + L"\" --default-device --tag " + Utf8ToWide(tag);
    std::vector<wchar_t> mutableCommand(commandLine.begin(), commandLine.end());
    mutableCommand.push_back(L'\0');

    STARTUPINFOW startupInfo{};
    startupInfo.cb = sizeof(startupInfo);
    startupInfo.dwFlags = STARTF_USESTDHANDLES;
    startupInfo.hStdOutput = stdoutWrite;
    startupInfo.hStdError = stdoutWrite;

    PROCESS_INFORMATION processInfo{};
    BOOL created = CreateProcessW(
        nullptr,
        mutableCommand.data(),
        nullptr,
        nullptr,
        TRUE,
        CREATE_NO_WINDOW,
        nullptr,
        nullptr,
        &startupInfo,
        &processInfo);
    CloseHandle(stdoutWrite);

    if (!created)
    {
        CloseHandle(stdoutRead);
        result.output = "{\"error\":\"CreateProcess failed\"}";
        return result;
    }

    char buffer[1024];
    DWORD bytesRead = 0;
    while (ReadFile(stdoutRead, buffer, sizeof(buffer), &bytesRead, nullptr) && bytesRead > 0)
    {
        result.output.append(buffer, buffer + bytesRead);
    }

    WaitForSingleObject(processInfo.hProcess, 10000);
    DWORD exitCode = 0;
    if (GetExitCodeProcess(processInfo.hProcess, &exitCode))
    {
        result.exitCode = exitCode;
    }

    CloseHandle(processInfo.hThread);
    CloseHandle(processInfo.hProcess);
    CloseHandle(stdoutRead);
    return result;
}

static void PrintChildProbe(const ChildProbeResult& probe, bool first)
{
    if (!first)
    {
        std::printf(",");
    }

    std::printf(
        "{\"tag\":\"%s\",\"preference\":\"%s\",\"exitCode\":%lu,\"output\":\"%s\"}",
        JsonEscape(probe.tag).c_str(),
        JsonEscape(probe.preference).c_str(),
        static_cast<unsigned long>(probe.exitCode),
        JsonEscape(probe.output).c_str());
}

static bool TextContains(const std::string& text, const std::string& value)
{
    return !value.empty() && text.find(value) != std::string::npos;
}

static void PrintAdapters(const std::vector<AdapterInfo>& adapters)
{
    std::printf("\"adapters\":[");
    for (size_t i = 0; i < adapters.size(); ++i)
    {
        if (i > 0)
        {
            std::printf(",");
        }

        std::printf(
            "{\"dxgiIndex\":%u,\"name\":\"%s\",\"adapterLuid\":\"%s\",\"gpuEngineLuidToken\":\"%s\",\"vendorId\":%u,\"deviceId\":%u,\"dedicatedVideoMemoryMb\":%.1f,\"isSoftware\":%s,\"integratedGuess\":%s}",
            adapters[i].index,
            JsonEscape(adapters[i].name).c_str(),
            LuidString(adapters[i].luid).c_str(),
            GpuEngineLuidToken(adapters[i].luid).c_str(),
            adapters[i].vendorId,
            adapters[i].deviceId,
            static_cast<double>(adapters[i].dedicatedVideoMemory) / 1024.0 / 1024.0,
            adapters[i].isSoftware ? "true" : "false",
            adapters[i].integratedGuess ? "true" : "false");
    }
    std::printf("]");
}

static void PrintStep(const StepResult& step, bool first)
{
    if (!first)
    {
        std::printf(",");
    }

    std::printf(
        "{\"name\":\"%s\",\"success\":%s,\"hr\":\"%s\",\"detail\":\"%s\",\"sample\":\"%s\"}",
        JsonEscape(step.name).c_str(),
        step.success ? "true" : "false",
        HrString(step.hr).c_str(),
        JsonEscape(step.detail).c_str(),
        JsonEscape(step.sample).c_str());
}

int main(int argc, char** argv)
{
    SetConsoleOutputCP(CP_UTF8);

    if (argc >= 2 && std::string(argv[1]) == "--default-device")
    {
        std::string tag = "default";
        for (int i = 2; i + 1 < argc; ++i)
        {
            if (std::string(argv[i]) == "--tag")
            {
                tag = argv[i + 1];
                break;
            }
        }

        return RunDefaultDeviceChild(tag);
    }

    std::vector<AdapterInfo> adapters = EnumerateAdapters();
    std::vector<StepResult> steps;
    std::vector<ChildProbeResult> childProbes;
    bool crossAdapterAttempted = false;
    bool crossAdapterTextureSharePossible = false;
    bool ntCrossAdapterTextureSharePossible = false;
    bool cooperativeRerenderPossible = false;
    bool powerSavingPreferenceSelectedTarget = false;
    bool highPerformancePreferenceSelectedSource = false;
    bool graphicsPreferenceRestored = false;

    int sourceIndex = SelectSourceAdapter(adapters);
    int targetIndex = sourceIndex >= 0 ? SelectTargetAdapter(adapters, sourceIndex) : -1;
    std::wstring executablePath = GetExecutablePath();

    DeviceBundle sourceDevice;
    DeviceBundle targetDevice;
    ID3D11Texture2D* sourceTexture = nullptr;

    if (sourceIndex >= 0)
    {
        steps.push_back(CreateDevice(adapters[static_cast<size_t>(sourceIndex)], sourceDevice));
    }
    else
    {
        steps.push_back(StepResult{ "selectSourceAdapter", false, E_FAIL, "No non-software DXGI adapter found", {} });
    }

    if (sourceDevice.device != nullptr)
    {
        steps.push_back(RenderFrame(sourceDevice, "renderOnSourceAdapter", D3D11_RESOURCE_MISC_SHARED, &sourceTexture));
    }

    if (targetIndex >= 0 && targetIndex != sourceIndex)
    {
        crossAdapterAttempted = true;
        steps.push_back(CreateDevice(adapters[static_cast<size_t>(targetIndex)], targetDevice));
    }
    else if (targetIndex >= 0)
    {
        steps.push_back(StepResult{ "selectTargetAdapter", false, S_FALSE, "Only one usable adapter was found; cross-adapter move cannot be tested", {} });
    }

    if (sourceTexture != nullptr && targetDevice.device != nullptr)
    {
        StepResult shared = TryOpenSharedTextureOnTarget(sourceTexture, targetDevice);
        crossAdapterTextureSharePossible = shared.success;
        steps.push_back(shared);

        StepResult ntShared = TryOpenNtSharedTextureOnTarget(sourceDevice, targetDevice);
        ntCrossAdapterTextureSharePossible = ntShared.success;
        steps.push_back(ntShared);
    }

    if (targetDevice.device != nullptr)
    {
        StepResult rerender = RenderFrame(targetDevice, "rerenderSameFrameOnTargetAdapter", 0, nullptr);
        cooperativeRerenderPossible = rerender.success;
        steps.push_back(rerender);
    }

    if (!executablePath.empty())
    {
        RegistryValueBackup backup = ReadGpuPreferenceBackup(executablePath);
        childProbes.push_back(RunDefaultDeviceChildProcess(executablePath, "current", "current"));

        if (WriteGpuPreference(executablePath, 1))
        {
            ChildProbeResult probe = RunDefaultDeviceChildProcess(executablePath, "powerSaving", "GpuPreference=1");
            if (targetIndex >= 0)
            {
                powerSavingPreferenceSelectedTarget = TextContains(probe.output, adapters[static_cast<size_t>(targetIndex)].name);
            }
            childProbes.push_back(probe);
        }
        else
        {
            childProbes.push_back(ChildProbeResult{ "powerSaving", "GpuPreference=1", static_cast<DWORD>(-1), "{\"error\":\"Could not write graphics preference\"}" });
        }

        if (WriteGpuPreference(executablePath, 2))
        {
            ChildProbeResult probe = RunDefaultDeviceChildProcess(executablePath, "highPerformance", "GpuPreference=2");
            if (sourceIndex >= 0)
            {
                highPerformancePreferenceSelectedSource = TextContains(probe.output, adapters[static_cast<size_t>(sourceIndex)].name);
            }
            childProbes.push_back(probe);
        }
        else
        {
            childProbes.push_back(ChildProbeResult{ "highPerformance", "GpuPreference=2", static_cast<DWORD>(-1), "{\"error\":\"Could not write graphics preference\"}" });
        }

        graphicsPreferenceRestored = RestoreGpuPreference(executablePath, backup);
    }

    std::printf("{");
    PrintAdapters(adapters);
    std::printf(",\"sourceAdapterIndex\":%d,\"targetAdapterIndex\":%d,\"crossAdapterAttempted\":%s,",
        sourceIndex,
        targetIndex,
        crossAdapterAttempted ? "true" : "false");
    std::printf("\"steps\":[");
    for (size_t i = 0; i < steps.size(); ++i)
    {
        PrintStep(steps[i], i == 0);
    }
    std::printf("],");
    std::printf("\"defaultDeviceProbes\":[");
    for (size_t i = 0; i < childProbes.size(); ++i)
    {
        PrintChildProbe(childProbes[i], i == 0);
    }
    std::printf("],");
    std::printf(
        "\"conclusion\":{\"crossAdapterTextureSharePossible\":%s,\"ntCrossAdapterTextureSharePossible\":%s,\"cooperativeRerenderPossible\":%s,\"powerSavingPreferenceSelectedTarget\":%s,\"highPerformancePreferenceSelectedSource\":%s,\"graphicsPreferenceCanInduceDefaultAdapter\":%s,\"graphicsPreferenceRestored\":%s,\"arbitraryProcessLiveMigrationPossible\":false,\"interpretation\":\"%s\"}",
        (crossAdapterTextureSharePossible || ntCrossAdapterTextureSharePossible) ? "true" : "false",
        ntCrossAdapterTextureSharePossible ? "true" : "false",
        cooperativeRerenderPossible ? "true" : "false",
        powerSavingPreferenceSelectedTarget ? "true" : "false",
        highPerformancePreferenceSelectedSource ? "true" : "false",
        (powerSavingPreferenceSelectedTarget && highPerformancePreferenceSelectedSource) ? "true" : "false",
        graphicsPreferenceRestored ? "true" : "false",
        cooperativeRerenderPossible
            ? "Device recreation on the target adapter works for a cooperative or shim-controlled renderer; arbitrary running processes still need a safe recreate point."
            : "Target adapter re-render failed; keep GPU live scheduling at preview/startup-preference level on this machine.");
    std::printf("}\n");

    SafeRelease(sourceTexture);
    SafeRelease(sourceDevice.context);
    SafeRelease(sourceDevice.device1);
    SafeRelease(sourceDevice.device);
    SafeRelease(targetDevice.context);
    SafeRelease(targetDevice.device1);
    SafeRelease(targetDevice.device);
    for (auto& adapter : adapters)
    {
        SafeRelease(adapter.adapter);
        adapter.adapter = nullptr;
    }

    return cooperativeRerenderPossible ? 0 : 2;
}
