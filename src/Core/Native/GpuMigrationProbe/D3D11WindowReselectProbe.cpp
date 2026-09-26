#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dxgi.h>
#include <dxgi1_4.h>
#include <d3d11.h>

#include <algorithm>
#include <cstdlib>
#include <cstdint>
#include <cstdio>
#include <fstream>
#include <sstream>
#include <string>

struct AdapterSnapshot
{
    bool valid = false;
    std::string name;
    std::string luidToken;
    UINT vendorId = 0;
    UINT deviceId = 0;
    SIZE_T dedicatedVideoMemory = 0;
};

struct D3DState
{
    HWND hwnd = nullptr;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    IDXGISwapChain* swapChain = nullptr;
    ID3D11RenderTargetView* renderTarget = nullptr;
    ID3D11Texture2D* workloadTextureA = nullptr;
    ID3D11Texture2D* workloadTextureB = nullptr;
    D3D_FEATURE_LEVEL featureLevel{};
    AdapterSnapshot initialAdapter;
    AdapterSnapshot lastAdapter;
    int recreateCount = 0;
    int simulatedPresentDeviceRemovedCount = 0;
    int simulatedResizeBuffersDeviceRemovedCount = 0;
    int actualPresentDeviceRemovedCount = 0;
    int actualResizeBuffersDeviceRemovedCount = 0;
    uint64_t successfulPresents = 0;
    uint64_t presentsAfterRecovery = 0;
    bool recoveryFailed = false;
    uint64_t presentAttemptsAfterRecovery = 0;
    HRESULT lastPresentResult = S_OK;
    HRESULT lastResizeResult = S_OK;
    uint64_t resizeAttempts = 0;
    uint64_t skippedRenderCalls = 0;
    std::string lastRecreateReason = "initial-create";
    HWND foregroundBeforeAutoTrigger = nullptr;
    HWND foregroundAfterAutoTrigger = nullptr;
    bool recreateBeforeNextPaint = false;
    bool simulatePresentDeviceRemoved = false;
    bool simulateResizeBuffersDeviceRemoved = false;
    bool triggerUsedNoActivateRestore = false;
    bool restoredZOrderByPredecessor = false;
};

struct ProbeOptions
{
    bool recreateOnDisplayChange = false;
    bool recreateOnDeviceChange = false;
    bool recreateOnSettingChange = false;
    bool explicitLowPowerAdapterOnStart = false;
    bool explicitLowPowerAdapterOnRecreate = false;
    bool noActivate = false;
    bool extendedSwapChain = false;
    int autoTriggerMs = 0;
    int frameIntervalMs = 33;
    int presentSyncInterval = 1;
    int copyPasses = 0;
    int copyTextureSize = 1024;
    std::string autoTriggerMethod;
    std::string outputPath;
};

static D3DState g_state;
static ProbeOptions g_options;

static constexpr UINT RM_RECREATE_DEFAULT_DEVICE_MESSAGE = WM_APP + 0x51;
static constexpr UINT RM_RECREATE_ON_NEXT_PAINT_MESSAGE = WM_APP + 0x52;

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

static void SafeRelease(IUnknown*& value)
{
    if (value != nullptr)
    {
        value->Release();
        value = nullptr;
    }
}

static AdapterSnapshot ReadDeviceAdapter(ID3D11Device* device)
{
    AdapterSnapshot snapshot;
    if (device == nullptr)
    {
        return snapshot;
    }

    IDXGIDevice* dxgiDevice = nullptr;
    HRESULT hr = device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDevice));
    if (FAILED(hr) || dxgiDevice == nullptr)
    {
        return snapshot;
    }

    IDXGIAdapter* adapter = nullptr;
    hr = dxgiDevice->GetAdapter(&adapter);
    dxgiDevice->Release();
    if (FAILED(hr) || adapter == nullptr)
    {
        return snapshot;
    }

    IDXGIAdapter1* adapter1 = nullptr;
    hr = adapter->QueryInterface(__uuidof(IDXGIAdapter1), reinterpret_cast<void**>(&adapter1));
    adapter->Release();
    if (FAILED(hr) || adapter1 == nullptr)
    {
        return snapshot;
    }

    DXGI_ADAPTER_DESC1 desc{};
    hr = adapter1->GetDesc1(&desc);
    adapter1->Release();
    if (FAILED(hr))
    {
        return snapshot;
    }

    snapshot.valid = true;
    snapshot.name = WideToUtf8(desc.Description);
    snapshot.luidToken = GpuEngineLuidToken(desc.AdapterLuid);
    snapshot.vendorId = desc.VendorId;
    snapshot.deviceId = desc.DeviceId;
    snapshot.dedicatedVideoMemory = desc.DedicatedVideoMemory;
    return snapshot;
}

static bool CreateRenderTarget()
{
    ID3D11Texture2D* backBuffer = nullptr;
    HRESULT hr = g_state.swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer));
    if (FAILED(hr) || backBuffer == nullptr)
    {
        return false;
    }

    hr = g_state.device->CreateRenderTargetView(backBuffer, nullptr, &g_state.renderTarget);
    backBuffer->Release();
    return SUCCEEDED(hr);
}

static bool DeviceAndSwapChainFactoryMatch()
{
    if (!g_state.device || !g_state.swapChain) return false;
    IDXGIDevice* device = nullptr;
    IDXGIAdapter* adapter = nullptr;
    IUnknown* deviceFactory = nullptr;
    IUnknown* chainFactory = nullptr;
    if (SUCCEEDED(g_state.device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&device))))
    {
        if (SUCCEEDED(device->GetAdapter(&adapter)))
            adapter->GetParent(__uuidof(IUnknown), reinterpret_cast<void**>(&deviceFactory));
    }
    g_state.swapChain->GetParent(__uuidof(IUnknown), reinterpret_cast<void**>(&chainFactory));
    const bool match = deviceFactory && chainFactory && deviceFactory == chainFactory;
    if (chainFactory) chainFactory->Release();
    if (deviceFactory) deviceFactory->Release();
    if (adapter) adapter->Release();
    if (device) device->Release();
    return match;
}

static bool CreateWorkloadTextures()
{
    if (g_options.copyPasses <= 0)
    {
        return true;
    }

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = static_cast<UINT>(g_options.copyTextureSize);
    desc.Height = static_cast<UINT>(g_options.copyTextureSize);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;

    HRESULT hr = g_state.device->CreateTexture2D(&desc, nullptr, &g_state.workloadTextureA);
    if (FAILED(hr) || g_state.workloadTextureA == nullptr)
    {
        return false;
    }

    hr = g_state.device->CreateTexture2D(&desc, nullptr, &g_state.workloadTextureB);
    return SUCCEEDED(hr) && g_state.workloadTextureB != nullptr;
}

static IDXGIAdapter1* SelectLowPowerAdapter()
{
    IDXGIFactory1* factory = nullptr;
    HRESULT hr = CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory));
    if (FAILED(hr) || factory == nullptr)
    {
        return nullptr;
    }

    IDXGIAdapter1* selected = nullptr;
    SIZE_T selectedDedicatedVideoMemory = static_cast<SIZE_T>(-1);
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
            if (selected == nullptr || desc.DedicatedVideoMemory < selectedDedicatedVideoMemory)
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

static void ReleaseD3D()
{
    if (g_state.context) g_state.context->ClearState();
    IUnknown* workloadTextureB = reinterpret_cast<IUnknown*>(g_state.workloadTextureB);
    SafeRelease(workloadTextureB);
    g_state.workloadTextureB = nullptr;

    IUnknown* workloadTextureA = reinterpret_cast<IUnknown*>(g_state.workloadTextureA);
    SafeRelease(workloadTextureA);
    g_state.workloadTextureA = nullptr;

    IUnknown* renderTarget = reinterpret_cast<IUnknown*>(g_state.renderTarget);
    SafeRelease(renderTarget);
    g_state.renderTarget = nullptr;

    IUnknown* swapChain = reinterpret_cast<IUnknown*>(g_state.swapChain);
    SafeRelease(swapChain);
    g_state.swapChain = nullptr;

    // Finish deferred destruction before creating a swap chain for the same HWND.
    if (g_state.context) g_state.context->Flush();

    IUnknown* context = reinterpret_cast<IUnknown*>(g_state.context);
    SafeRelease(context);
    g_state.context = nullptr;

    IUnknown* device = reinterpret_cast<IUnknown*>(g_state.device);
    SafeRelease(device);
    g_state.device = nullptr;
}

static bool RecreateD3D(HWND hwnd, const char* reason);

static void ResizeSwapChain(UINT width, UINT height)
{
    if (g_state.swapChain == nullptr || width == 0 || height == 0)
    {
        return;
    }

    if (g_state.simulateResizeBuffersDeviceRemoved)
    {
        g_state.simulateResizeBuffersDeviceRemoved = false;
        ++g_state.simulatedResizeBuffersDeviceRemovedCount;
        RecreateD3D(g_state.hwnd, "resizebuffers-device-removed-recreate");
        return;
    }

    IUnknown* target = reinterpret_cast<IUnknown*>(g_state.renderTarget);
    SafeRelease(target);
    g_state.renderTarget = nullptr;

    // ResizeBuffers1 requires a D3D12 command queue, including for flip-model chains.
    const HRESULT hr = g_state.swapChain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
    g_state.lastResizeResult = hr;
    ++g_state.resizeAttempts;
    if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET)
    {
        ++g_state.actualResizeBuffersDeviceRemovedCount;
        g_state.recoveryFailed |= !RecreateD3D(g_state.hwnd, "actual-resizebuffers-device-removed");
        return;
    }
    if (SUCCEEDED(hr))
    {
        CreateRenderTarget();
    }
}

static bool CreateD3D(HWND hwnd)
{
    RECT client{};
    GetClientRect(hwnd, &client);
    UINT width = static_cast<UINT>(client.right - client.left);
    UINT height = static_cast<UINT>(client.bottom - client.top);

    DXGI_SWAP_CHAIN_DESC swapDesc{};
    swapDesc.BufferCount = 2;
    swapDesc.BufferDesc.Width = width > 0 ? width : 800;
    swapDesc.BufferDesc.Height = height > 0 ? height : 480;
    swapDesc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    swapDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapDesc.OutputWindow = hwnd;
    swapDesc.SampleDesc.Count = 1;
    swapDesc.Windowed = TRUE;
    swapDesc.SwapEffect = g_options.extendedSwapChain ? DXGI_SWAP_EFFECT_FLIP_DISCARD : DXGI_SWAP_EFFECT_DISCARD;

    static const D3D_FEATURE_LEVEL levels[] = {
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0
    };

    IDXGIAdapter1* explicitAdapter = nullptr;
    if (g_options.explicitLowPowerAdapterOnStart
        || (g_state.initialAdapter.valid && g_options.explicitLowPowerAdapterOnRecreate))
    {
        explicitAdapter = SelectLowPowerAdapter();
    }

    HRESULT hr = D3D11CreateDeviceAndSwapChain(
        explicitAdapter,
        explicitAdapter != nullptr ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        levels,
        static_cast<UINT>(sizeof(levels) / sizeof(levels[0])),
        D3D11_SDK_VERSION,
        &swapDesc,
        &g_state.swapChain,
        &g_state.device,
        &g_state.featureLevel,
        &g_state.context);

    if (explicitAdapter != nullptr)
    {
        explicitAdapter->Release();
    }

    if (FAILED(hr) || g_state.device == nullptr || g_state.swapChain == nullptr)
    {
        return false;
    }

    if (!CreateRenderTarget())
    {
        return false;
    }

    if (!CreateWorkloadTextures())
    {
        return false;
    }

    AdapterSnapshot adapter = ReadDeviceAdapter(g_state.device);
    if (!g_state.initialAdapter.valid)
    {
        g_state.initialAdapter = adapter;
    }

    g_state.lastAdapter = adapter;
    return true;
}

static bool RecreateD3D(HWND hwnd, const char* reason)
{
    ReleaseD3D();
    bool created = CreateD3D(hwnd);
    if (created)
    {
        ++g_state.recreateCount;
        g_state.lastRecreateReason = reason != nullptr ? reason : "unknown";
        InvalidateRect(hwnd, nullptr, TRUE);
    }

    return created;
}

static void Render()
{
    if (g_state.context == nullptr || g_state.renderTarget == nullptr || g_state.swapChain == nullptr)
    {
        ++g_state.skippedRenderCalls;
        return;
    }

    if (g_state.simulatePresentDeviceRemoved)
    {
        g_state.simulatePresentDeviceRemoved = false;
        ++g_state.simulatedPresentDeviceRemovedCount;
        RecreateD3D(g_state.hwnd, "present-device-removed-recreate");
        if (g_state.context == nullptr || g_state.renderTarget == nullptr || g_state.swapChain == nullptr)
        {
            return;
        }
    }

    const float color[] = { 0.035f, 0.075f, 0.110f, 1.0f };
    g_state.context->ClearRenderTargetView(g_state.renderTarget, color);
    for (int pass = 0; pass < g_options.copyPasses; ++pass)
    {
        ID3D11Texture2D* destination = (pass & 1) == 0 ? g_state.workloadTextureB : g_state.workloadTextureA;
        ID3D11Texture2D* source = (pass & 1) == 0 ? g_state.workloadTextureA : g_state.workloadTextureB;
        if (destination != nullptr && source != nullptr)
        {
            g_state.context->CopyResource(destination, source);
        }
    }
    HRESULT result;
    if (g_options.extendedSwapChain)
    {
        IDXGISwapChain1* chain = nullptr;
        result = g_state.swapChain->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&chain));
        if (SUCCEEDED(result))
        {
            const DXGI_PRESENT_PARAMETERS parameters{};
            result = chain->Present1(static_cast<UINT>(g_options.presentSyncInterval), 0, &parameters);
            chain->Release();
        }
    }
    else result = g_state.swapChain->Present(static_cast<UINT>(g_options.presentSyncInterval), 0);
    g_state.lastPresentResult = result;
    if (g_state.recreateCount > 0)
        ++g_state.presentAttemptsAfterRecovery;
    if (result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET)
    {
        ++g_state.actualPresentDeviceRemovedCount;
        g_state.recoveryFailed |= !RecreateD3D(g_state.hwnd, "actual-present-device-removed");
    }
    else if (result == S_OK)
    {
        ++g_state.successfulPresents;
        if (g_state.recreateCount > 0)
            ++g_state.presentsAfterRecovery;
    }
}

static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    switch (message)
    {
    case RM_RECREATE_DEFAULT_DEVICE_MESSAGE:
        RecreateD3D(hwnd, "custom-device-recreate-message");
        return 0;
    case RM_RECREATE_ON_NEXT_PAINT_MESSAGE:
        g_state.recreateBeforeNextPaint = true;
        InvalidateRect(hwnd, nullptr, TRUE);
        return 0;
    case WM_SIZE:
        ResizeSwapChain(static_cast<UINT>(LOWORD(lParam)), static_cast<UINT>(HIWORD(lParam)));
        return 0;
    case WM_PAINT:
    {
        PAINTSTRUCT ps{};
        BeginPaint(hwnd, &ps);
        EndPaint(hwnd, &ps);
        if (g_state.recreateBeforeNextPaint)
        {
            g_state.recreateBeforeNextPaint = false;
            RecreateD3D(hwnd, "next-paint-recreate");
        }
        Render();
        return 0;
    }
    case WM_DISPLAYCHANGE:
        if (g_options.recreateOnDisplayChange)
        {
            RecreateD3D(hwnd, "display-change-recreate");
            return 0;
        }
        InvalidateRect(hwnd, nullptr, TRUE);
        return 0;
    case WM_DEVICECHANGE:
        if (g_options.recreateOnDeviceChange)
        {
            RecreateD3D(hwnd, "device-change-recreate");
            return 0;
        }
        InvalidateRect(hwnd, nullptr, TRUE);
        return 0;
    case WM_SETTINGCHANGE:
        if (g_options.recreateOnSettingChange)
        {
            RecreateD3D(hwnd, "setting-change-recreate");
            return 0;
        }
        InvalidateRect(hwnd, nullptr, TRUE);
        return 0;
    case WM_DWMCOMPOSITIONCHANGED:
        InvalidateRect(hwnd, nullptr, TRUE);
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    default:
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}

static void RunRedrawOnlySequence(HWND hwnd)
{
    InvalidateRect(hwnd, nullptr, TRUE);
    UpdateWindow(hwnd);
    RedrawWindow(hwnd, nullptr, nullptr, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
    SendMessageW(hwnd, WM_SETTINGCHANGE, 0, 0);
    SendMessageW(hwnd, WM_DISPLAYCHANGE, 0, 0);
    SendMessageW(hwnd, WM_DEVICECHANGE, 0, 0);
    SendMessageW(hwnd, WM_DWMCOMPOSITIONCHANGED, 0, 0);
}

static void RunMinimizeRestoreNoActivateSequence(HWND hwnd)
{
    HWND predecessor = GetWindow(hwnd, GW_HWNDPREV);
    g_state.triggerUsedNoActivateRestore = true;

    ShowWindow(hwnd, SW_SHOWMINNOACTIVE);
    ShowWindow(hwnd, SW_SHOWNOACTIVATE);

    if (predecessor != nullptr && IsWindow(predecessor))
    {
        SetWindowPos(
            hwnd,
            predecessor,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        g_state.restoredZOrderByPredecessor = true;
    }

    InvalidateRect(hwnd, nullptr, TRUE);
}

static void RunAutoTrigger(HWND hwnd)
{
    g_state.foregroundBeforeAutoTrigger = GetForegroundWindow();

    if (g_options.autoTriggerMethod == "redrawOnly")
    {
        RunRedrawOnlySequence(hwnd);
    }
    else if (g_options.autoTriggerMethod == "displayChange")
    {
        SendMessageW(hwnd, WM_DISPLAYCHANGE, 0, 0);
    }
    else if (g_options.autoTriggerMethod == "settingChange")
    {
        SendMessageW(hwnd, WM_SETTINGCHANGE, 0, 0);
    }
    else if (g_options.autoTriggerMethod == "customDeviceRecreate")
    {
        RecreateD3D(hwnd, "custom-device-recreate-message");
    }
    else if (g_options.autoTriggerMethod == "nextPaintRecreate")
    {
        g_state.recreateBeforeNextPaint = true;
        RedrawWindow(hwnd, nullptr, nullptr, RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
    }
    else if (g_options.autoTriggerMethod == "presentDeviceRemoved")
    {
        g_state.simulatePresentDeviceRemoved = true;
        InvalidateRect(hwnd, nullptr, TRUE);
    }
    else if (g_options.autoTriggerMethod == "resizeBuffersDeviceRemoved")
    {
        g_state.simulateResizeBuffersDeviceRemoved = true;
        SetWindowPos(hwnd, nullptr, 0, 0, 760, 480, SWP_NOMOVE | SWP_NOZORDER);
    }
    else if (g_options.autoTriggerMethod == "minimizeRestoreNoActivate")
    {
        RunMinimizeRestoreNoActivateSequence(hwnd);
    }
    else if (g_options.autoTriggerMethod == "minimizeRestoreNoActivateResizeBuffersDeviceRemoved")
    {
        g_state.simulateResizeBuffersDeviceRemoved = true;
        RunMinimizeRestoreNoActivateSequence(hwnd);
    }

    g_state.foregroundAfterAutoTrigger = GetForegroundWindow();
}

static bool HasArg(int argc, char** argv, const char* name)
{
    for (int i = 1; i < argc; ++i)
    {
        if (std::string(argv[i]) == name)
        {
            return true;
        }
    }

    return false;
}

static std::string StringArg(int argc, char** argv, const char* name, const std::string& fallback)
{
    for (int i = 1; i + 1 < argc; ++i)
    {
        if (std::string(argv[i]) == name)
        {
            return argv[i + 1];
        }
    }

    return fallback;
}

static int DurationFromArgs(int argc, char** argv)
{
    for (int i = 1; i + 1 < argc; ++i)
    {
        if (std::string(argv[i]) == "--duration-ms")
        {
            return std::clamp(std::atoi(argv[i + 1]), 1000, 120000);
        }
    }

    return 18000;
}

static int IntArg(int argc, char** argv, const char* name, int fallback)
{
    for (int i = 1; i + 1 < argc; ++i)
    {
        if (std::string(argv[i]) == name)
        {
            return std::atoi(argv[i + 1]);
        }
    }

    return fallback;
}

static void WriteOutput(const std::string& text)
{
    if (!g_options.outputPath.empty())
    {
        std::ofstream file(g_options.outputPath, std::ios::binary | std::ios::trunc);
        file << text;
        return;
    }

    std::printf("%s", text.c_str());
}

int main(int argc, char** argv)
{
    SetConsoleOutputCP(CP_UTF8);

    int durationMs = DurationFromArgs(argc, argv);
    g_options.recreateOnDisplayChange = HasArg(argc, argv, "--recreate-on-display-change");
    g_options.recreateOnDeviceChange = HasArg(argc, argv, "--recreate-on-device-change");
    g_options.recreateOnSettingChange = HasArg(argc, argv, "--recreate-on-setting-change");
    g_options.explicitLowPowerAdapterOnStart = HasArg(argc, argv, "--explicit-low-power-adapter-on-start");
    g_options.explicitLowPowerAdapterOnRecreate = HasArg(argc, argv, "--explicit-low-power-adapter-on-recreate");
    g_options.noActivate = HasArg(argc, argv, "--no-activate");
    g_options.extendedSwapChain = HasArg(argc, argv, "--extended-swap-chain");
    g_options.autoTriggerMs = IntArg(argc, argv, "--auto-trigger-ms", 0);
    g_options.frameIntervalMs = std::clamp(IntArg(argc, argv, "--frame-interval-ms", 33), 8, 1000);
    g_options.presentSyncInterval = std::clamp(IntArg(argc, argv, "--present-sync-interval", 1), 0, 1);
    g_options.copyPasses = std::clamp(IntArg(argc, argv, "--copy-passes", 0), 0, 32);
    g_options.copyTextureSize = std::clamp(IntArg(argc, argv, "--copy-texture-size", 1024), 256, 1536);
    g_options.autoTriggerMethod = StringArg(argc, argv, "--auto-trigger-method", "");
    g_options.outputPath = StringArg(argc, argv, "--output", "");

    const bool unsafeMigrationRequested =
        g_options.recreateOnDisplayChange ||
        g_options.recreateOnDeviceChange ||
        g_options.recreateOnSettingChange ||
        g_options.explicitLowPowerAdapterOnRecreate ||
        g_options.autoTriggerMs > 0 ||
        !g_options.autoTriggerMethod.empty();
    if (unsafeMigrationRequested && !HasArg(argc, argv, "--allow-unsafe-migration-probe"))
    {
        std::fprintf(
            stderr,
            "Migration triggers are disabled by default. Pass --allow-unsafe-migration-probe only for the isolated manual probe.\n");
        return 2;
    }

    DWORD started = GetTickCount();
    DWORD pid = GetCurrentProcessId();

    HINSTANCE instance = GetModuleHandleW(nullptr);
    const wchar_t* className = L"ResourceManagerD3D11WindowReselectProbe";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = instance;
    windowClass.lpszClassName = className;
    windowClass.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    RegisterClassW(&windowClass);

    std::wstring title = L"Resource Manager D3D11 Reselect Probe PID " + std::to_wstring(pid);
    HWND hwnd = CreateWindowExW(
        g_options.noActivate ? WS_EX_NOACTIVATE : 0,
        className,
        title.c_str(),
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        880,
        520,
        nullptr,
        nullptr,
        instance,
        nullptr);

    if (hwnd == nullptr)
    {
        std::printf("{\"success\":false,\"error\":\"CreateWindowExW failed\"}\n");
        return 2;
    }

    g_state.hwnd = hwnd;
    ShowWindow(hwnd, g_options.noActivate ? SW_SHOWNOACTIVATE : SW_SHOWNORMAL);
    UpdateWindow(hwnd);

    if (!CreateD3D(hwnd))
    {
        std::printf("{\"success\":false,\"error\":\"D3D11CreateDeviceAndSwapChain failed\"}\n");
        return 3;
    }

    if (HasArg(argc, argv, "--ready-stdout"))
    {
        std::printf("{\"ready\":true,\"pid\":%lu,\"hwnd\":%llu,\"initialLuid\":\"%s\"}\n",
            static_cast<unsigned long>(pid), static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(hwnd)),
            g_state.initialAdapter.luidToken.c_str());
        std::fflush(stdout);
    }

    MSG message{};
    bool autoTriggered = false;
    while ((GetTickCount() - started) < static_cast<DWORD>(durationMs))
    {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            if (message.message == WM_QUIT)
            {
                goto done;
            }

            TranslateMessage(&message);
            DispatchMessageW(&message);
        }

        if (!autoTriggered && g_options.autoTriggerMs > 0 && (GetTickCount() - started) >= static_cast<DWORD>(g_options.autoTriggerMs))
        {
            RunAutoTrigger(hwnd);
            autoTriggered = true;
        }

        Render();
        Sleep(static_cast<DWORD>(g_options.frameIntervalMs));
    }

done:
    AdapterSnapshot finalAdapter = ReadDeviceAdapter(g_state.device);

    std::ostringstream output;
    output
        << "{\"success\":" << ((finalAdapter.valid && g_state.renderTarget && !g_state.recoveryFailed && g_state.successfulPresents > 0
            && (g_state.recreateCount == 0 || g_state.presentsAfterRecovery > 0) && g_state.lastPresentResult == S_OK) ? "true" : "false")
        << ",\"pid\":" << static_cast<unsigned long>(pid)
        << ",\"initialAdapter\":\"" << JsonEscape(g_state.initialAdapter.name) << "\""
        << ",\"initialLuid\":\"" << JsonEscape(g_state.initialAdapter.luidToken) << "\""
        << ",\"finalAdapter\":\"" << JsonEscape(finalAdapter.name) << "\""
        << ",\"finalLuid\":\"" << JsonEscape(finalAdapter.luidToken) << "\""
        << ",\"adapterChanged\":" << ((g_state.initialAdapter.luidToken != finalAdapter.luidToken) ? "true" : "false")
        << ",\"recreateCount\":" << g_state.recreateCount
        << ",\"simulatedPresentDeviceRemovedCount\":" << g_state.simulatedPresentDeviceRemovedCount
        << ",\"simulatedResizeBuffersDeviceRemovedCount\":" << g_state.simulatedResizeBuffersDeviceRemovedCount
        << ",\"actualPresentDeviceRemovedCount\":" << g_state.actualPresentDeviceRemovedCount
        << ",\"actualResizeBuffersDeviceRemovedCount\":" << g_state.actualResizeBuffersDeviceRemovedCount
        << ",\"successfulPresents\":" << g_state.successfulPresents
        << ",\"presentsAfterRecovery\":" << g_state.presentsAfterRecovery
        << ",\"recoveryFailed\":" << (g_state.recoveryFailed ? "true" : "false")
        << ",\"presentAttemptsAfterRecovery\":" << g_state.presentAttemptsAfterRecovery
        << ",\"lastPresentResult\":" << static_cast<uint32_t>(g_state.lastPresentResult)
        << ",\"lastResizeResult\":" << static_cast<uint32_t>(g_state.lastResizeResult)
        << ",\"resizeAttempts\":" << g_state.resizeAttempts
        << ",\"renderTargetReady\":" << (g_state.renderTarget ? "true" : "false")
        << ",\"skippedRenderCalls\":" << g_state.skippedRenderCalls
        << ",\"deviceAndSwapChainFactoryMatch\":" << (DeviceAndSwapChainFactoryMatch() ? "true" : "false")
        << ",\"lastRecreateReason\":\"" << JsonEscape(g_state.lastRecreateReason) << "\""
        << ",\"autoTriggerMethod\":\"" << JsonEscape(g_options.autoTriggerMethod) << "\""
        << ",\"explicitLowPowerAdapterOnRecreate\":" << (g_options.explicitLowPowerAdapterOnRecreate ? "true" : "false")
        << ",\"presentSyncInterval\":" << g_options.presentSyncInterval
        << ",\"extendedSwapChain\":" << (g_options.extendedSwapChain ? "true" : "false")
        << ",\"copyPasses\":" << g_options.copyPasses
        << ",\"copyTextureSize\":" << g_options.copyTextureSize
        << ",\"triggerUsedNoActivateRestore\":" << (g_state.triggerUsedNoActivateRestore ? "true" : "false")
        << ",\"restoredZOrderByPredecessor\":" << (g_state.restoredZOrderByPredecessor ? "true" : "false")
        << ",\"foregroundChangedDuringTrigger\":" << ((g_state.foregroundBeforeAutoTrigger != g_state.foregroundAfterAutoTrigger) ? "true" : "false")
        << "}\n";
    WriteOutput(output.str());

    ReleaseD3D();

    DestroyWindow(hwnd);
    return 0;
}
