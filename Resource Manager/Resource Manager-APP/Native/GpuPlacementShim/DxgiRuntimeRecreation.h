#pragma once

// Included inside the original provider namespace; no independent hook owner.
namespace DxgiRuntime
{
enum class Method : uint32_t { Present = 1, ResizeBuffers = 2 };
enum class State : uint32_t { Empty, Armed, Signalled, Expired, Cancelled };
struct Request
{
    uint32_t byteSize = sizeof(Request);
    uint32_t version = 1;
    uint32_t api = 0;
    Method method = Method::Present;
    uint32_t reserved = 0;
    State state = State::Empty;
    uint64_t requestId = 0;
    uint64_t targetLuid = 0;
    uint64_t sourceLuid = 0;
    uint64_t deadlineMilliseconds = 0;
};
static_assert(sizeof(Request) == 56);

SRWLOCK requestLock = SRWLOCK_INIT;
CONDITION_VARIABLE requestChanged = CONDITION_VARIABLE_INIT;
Request current;
std::mutex installationLock;
bool installed = false;
using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using Present1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using ResizeFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using Resize1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain3*, UINT, UINT, UINT, DXGI_FORMAT, UINT, const UINT*, IUnknown* const*);
PresentFn realPresent = nullptr;
Present1Fn realPresent1 = nullptr;
ResizeFn realResize = nullptr;
Resize1Fn realResize1 = nullptr;
using CreateChainFn = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
using CreateHwndFn = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);
using CreateCoreFn = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, IUnknown*, const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**);
using CreateCompositionFn = HRESULT(STDMETHODCALLTYPE*)(IDXGIFactory2*, IUnknown*, const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**);
CreateChainFn realCreateChain = nullptr;
CreateHwndFn realCreateHwnd = nullptr;
CreateCoreFn realCreateCore = nullptr;
CreateCompositionFn realCreateComposition = nullptr;

HRESULT STDMETHODCALLTYPE CreateChain(IDXGIFactory* factory, IUnknown* device, DXGI_SWAP_CHAIN_DESC* desc, IDXGISwapChain** output)
{
    const GraphicsRuntimeCall runtimeCall;
    return realCreateChain(factory, device, desc, output);
}
HRESULT STDMETHODCALLTYPE CreateHwnd(IDXGIFactory2* factory, IUnknown* device, HWND window,
    const DXGI_SWAP_CHAIN_DESC1* desc, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreen, IDXGIOutput* restrictTo, IDXGISwapChain1** output)
{
    const GraphicsRuntimeCall runtimeCall;
    return realCreateHwnd(factory, device, window, desc, fullscreen, restrictTo, output);
}
HRESULT STDMETHODCALLTYPE CreateCore(IDXGIFactory2* factory, IUnknown* device, IUnknown* window,
    const DXGI_SWAP_CHAIN_DESC1* desc, IDXGIOutput* restrictTo, IDXGISwapChain1** output)
{
    const GraphicsRuntimeCall runtimeCall;
    return realCreateCore(factory, device, window, desc, restrictTo, output);
}
HRESULT STDMETHODCALLTYPE CreateComposition(IDXGIFactory2* factory, IUnknown* device,
    const DXGI_SWAP_CHAIN_DESC1* desc, IDXGIOutput* restrictTo, IDXGISwapChain1** output)
{
    const GraphicsRuntimeCall runtimeCall;
    return realCreateComposition(factory, device, desc, restrictTo, output);
}

bool Armed()
{
    AcquireSRWLockShared(&requestLock);
    const bool result = current.state == State::Armed
        && GetTickCount64() < current.deadlineMilliseconds;
    ReleaseSRWLockShared(&requestLock);
    return result;
}

bool InspectCall(IDXGISwapChain* chain, Method method, uint32_t supportedApis = 3u)
{
    if (g_dxgiBootstrap || g_insideGraphicsRuntime) return false;
    if (!g_apiObservations.Recording() && !Armed()) return false;
    uint32_t api = 0;
    LUID luid{};
    ID3D12Device* device12 = nullptr;
    if (SUCCEEDED(chain->GetDevice(__uuidof(ID3D12Device), reinterpret_cast<void**>(&device12))))
    {
        api = static_cast<uint32_t>(ResourceManagerGpuObservation::CallApi::D3D12);
        luid = device12->GetAdapterLuid();
        device12->Release();
    }
    else
    {
        ID3D11Device* device11 = nullptr;
        if (FAILED(chain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&device11)))) return false;
        api = static_cast<uint32_t>(ResourceManagerGpuObservation::CallApi::D3D11);
        IDXGIDevice* dxgi = nullptr;
        if (SUCCEEDED(device11->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgi))))
        {
            IDXGIAdapter* adapter = nullptr;
            if (SUCCEEDED(dxgi->GetAdapter(&adapter)))
            {
                DXGI_ADAPTER_DESC description{};
                if (SUCCEEDED(adapter->GetDesc(&description))) luid = description.AdapterLuid;
                adapter->Release();
            }
            dxgi->Release();
        }
        device11->Release();
    }
    if (!(api & supportedApis)) return false;
    g_apiObservations.Record(static_cast<ResourceManagerGpuObservation::CallApi>(api));
    const uint64_t source = (uint64_t{static_cast<uint32_t>(luid.HighPart)} << 32) | luid.LowPart;
    DWORD foregroundPid = 0;
    GetWindowThreadProcessId(GetForegroundWindow(), &foregroundPid);
    AcquireSRWLockExclusive(&requestLock);
    const bool signal = current.state == State::Armed && current.method == method && (current.api & api) != 0
        && GetTickCount64() < current.deadlineMilliseconds
        && source != 0 && source != current.targetLuid && foregroundPid != GetCurrentProcessId();
    if (signal)
    {
        current.sourceLuid = source;
        current.state = State::Signalled;
        WakeAllConditionVariable(&requestChanged);
    }
    ReleaseSRWLockExclusive(&requestLock);
    return signal;
}

HRESULT STDMETHODCALLTYPE Present(IDXGISwapChain* chain, UINT interval, UINT flags)
{
    if (!(flags & DXGI_PRESENT_TEST) && InspectCall(chain, Method::Present)) return DXGI_ERROR_DEVICE_REMOVED;
    const GraphicsRuntimeCall runtimeCall;
    return realPresent(chain, interval, flags);
}
HRESULT STDMETHODCALLTYPE Present1(IDXGISwapChain1* chain, UINT interval, UINT flags, const DXGI_PRESENT_PARAMETERS* parameters)
{
    if (!(flags & DXGI_PRESENT_TEST) && InspectCall(chain, Method::Present)) return DXGI_ERROR_DEVICE_REMOVED;
    const GraphicsRuntimeCall runtimeCall;
    return realPresent1(chain, interval, flags, parameters);
}
HRESULT STDMETHODCALLTYPE Resize(IDXGISwapChain* chain, UINT count, UINT width, UINT height, DXGI_FORMAT format, UINT flags)
{
    if (InspectCall(chain, Method::ResizeBuffers)) return DXGI_ERROR_DEVICE_REMOVED;
    const GraphicsRuntimeCall runtimeCall;
    return realResize(chain, count, width, height, format, flags);
}
HRESULT STDMETHODCALLTYPE Resize1(IDXGISwapChain3* chain, UINT count, UINT width, UINT height, DXGI_FORMAT format,
    UINT flags, const UINT* masks, IUnknown* const* queues)
{
    if (InspectCall(chain, Method::ResizeBuffers, 2u)) return DXGI_ERROR_DEVICE_REMOVED;
    const GraphicsRuntimeCall runtimeCall;
    return realResize1(chain, count, width, height, format, flags, masks, queues);
}

bool EnsureHooks()
{
    std::lock_guard<std::mutex> guard(installationLock);
    if (InterlockedCompareExchange(&g_hooksEnabled, 0, 0) != 1 || !g_realCreateDeviceAndSwapChain) return false;
    if (installed) return true;
    // Discover public DXGI implementation addresses, including pre-existing swap chains.
    // This is explicitly invoked outside DllMain, never from a render callback.
    const HWND window = CreateWindowExW(WS_EX_NOACTIVATE, L"STATIC", L"", WS_POPUP,
        0, 0, 1, 1, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!window) return false;
    DXGI_SWAP_CHAIN_DESC description{};
    description.BufferDesc.Width = description.BufferDesc.Height = 1;
    description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.SampleDesc.Count = 1;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 2;
    description.OutputWindow = window;
    description.Windowed = TRUE;
    description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    IDXGISwapChain* chain = nullptr;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    g_dxgiBootstrap = true;
    const HRESULT result = g_realCreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        nullptr, 0, D3D11_SDK_VERSION, &description, &chain, &device, nullptr, &context);
    g_dxgiBootstrap = false;
    bool ready = false;
    IDXGISwapChain3* chain3 = nullptr;
    if (SUCCEEDED(result) && chain && SUCCEEDED(chain->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&chain3))))
    {
        IDXGIFactory2* factory = nullptr;
        if (SUCCEEDED(chain->GetParent(__uuidof(IDXGIFactory2), reinterpret_cast<void**>(&factory))))
        {
            void** methods = *reinterpret_cast<void***>(factory);
            ready = CreateAndEnableHook(reinterpret_cast<FARPROC>(methods[10]), reinterpret_cast<void*>(&CreateChain), reinterpret_cast<void**>(&realCreateChain))
                && CreateAndEnableHook(reinterpret_cast<FARPROC>(methods[15]), reinterpret_cast<void*>(&CreateHwnd), reinterpret_cast<void**>(&realCreateHwnd))
                && CreateAndEnableHook(reinterpret_cast<FARPROC>(methods[16]), reinterpret_cast<void*>(&CreateCore), reinterpret_cast<void**>(&realCreateCore))
                && CreateAndEnableHook(reinterpret_cast<FARPROC>(methods[24]), reinterpret_cast<void*>(&CreateComposition), reinterpret_cast<void**>(&realCreateComposition));
            factory->Release();
        }
        void** table = *reinterpret_cast<void***>(chain3);
        // Public COM ABI slots: IDXGISwapChain(8/13), IDXGISwapChain1(22), IDXGISwapChain3(39).
        ready = ready && CreateAndEnableHook(reinterpret_cast<FARPROC>(table[8]), reinterpret_cast<void*>(&Present), reinterpret_cast<void**>(&realPresent))
            && CreateAndEnableHook(reinterpret_cast<FARPROC>(table[13]), reinterpret_cast<void*>(&Resize), reinterpret_cast<void**>(&realResize))
            && CreateAndEnableHook(reinterpret_cast<FARPROC>(table[22]), reinterpret_cast<void*>(&Present1), reinterpret_cast<void**>(&realPresent1))
            && CreateAndEnableHook(reinterpret_cast<FARPROC>(table[39]), reinterpret_cast<void*>(&Resize1), reinterpret_cast<void**>(&realResize1));
    }
    if (chain3) chain3->Release();
    if (chain) chain->Release();
    if (context) context->Release();
    if (device) device->Release();
    DestroyWindow(window);
    installed = ready;
    return ready;
}

DWORD Arm(void* parameter)
{
    if (!parameter) return 0;
    auto& input = *static_cast<Request*>(parameter);
    if (input.byteSize != sizeof(Request) || input.version != 1 || !input.requestId || !input.targetLuid
        || input.reserved != 0 || GetTickCount64() >= input.deadlineMilliseconds
        || !input.api || (input.api & ~3u) || (input.method != Method::Present && input.method != Method::ResizeBuffers)) return 0;
    const auto policy = ReadPolicyFile();
    if (policy.mode != GpuShimPolicyMode::TargetLuid || !EnsureHooks()) return 0;
    IDXGIAdapter1* target = SelectAdapter(policy);
    DXGI_ADAPTER_DESC1 description{};
    const bool found = target && SUCCEEDED(target->GetDesc1(&description));
    if (target) target->Release();
    const auto targetKey = (uint64_t{static_cast<uint32_t>(description.AdapterLuid.HighPart)} << 32) | description.AdapterLuid.LowPart;
    if (!found || targetKey != input.targetLuid) return 0;
    AcquireSRWLockExclusive(&requestLock);
    if (GetTickCount64() >= input.deadlineMilliseconds
        || (current.state != State::Empty && GetTickCount64() < current.deadlineMilliseconds))
    {
        ReleaseSRWLockExclusive(&requestLock);
        return 0;
    }
    current = input;
    current.state = State::Armed;
    current.sourceLuid = 0;
    input = current;
    WakeAllConditionVariable(&requestChanged);
    ReleaseSRWLockExclusive(&requestLock);
    return 1;
}

DWORD Finish(void* parameter, bool cancel)
{
    if (!parameter) return 0;
    auto& output = *static_cast<Request*>(parameter);
    if (output.byteSize != sizeof(Request) || output.version != 1 || !output.requestId) return 0;
    AcquireSRWLockExclusive(&requestLock);
    if (current.requestId != output.requestId || current.state == State::Empty)
    {
        ReleaseSRWLockExclusive(&requestLock);
        return 0;
    }
    while (!cancel && current.state == State::Armed)
    {
        if (current.requestId != output.requestId) break;
        const auto now = GetTickCount64();
        if (now >= current.deadlineMilliseconds) break;
        if (!SleepConditionVariableSRW(&requestChanged, &requestLock,
            static_cast<DWORD>((std::min)(current.deadlineMilliseconds - now, uint64_t{INFINITE - 1})), 0)
            && GetLastError() != ERROR_TIMEOUT)
        {
            cancel = true;
        }
    }
    if (current.requestId != output.requestId)
    {
        ReleaseSRWLockExclusive(&requestLock);
        return 0;
    }
    if (current.state == State::Armed) current.state = cancel ? State::Cancelled : State::Expired;
    output = current;
    current = {};
    ReleaseSRWLockExclusive(&requestLock);
    return 1;
}
}
