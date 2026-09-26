#define WIN32_LEAN_AND_MEAN
#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <algorithm>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <filesystem>
#include "../GpuPlacementShim/GpuPlacementDeviceObservation.h"

namespace
{
template<class T> void Release(T*& value) { if (value) value->Release(); value = nullptr; }
void Check(HRESULT hr) { if (FAILED(hr)) throw std::runtime_error("HRESULT " + std::to_string(static_cast<uint32_t>(hr))); }
std::string Token(LUID id)
{
    char value[64]{};
    std::snprintf(value, sizeof(value), "luid_0x%08x_0x%08x", static_cast<unsigned>(id.HighPart), static_cast<unsigned>(id.LowPart));
    return value;
}
struct Renderer
{
    HWND window = nullptr;
    ID3D12Device* device = nullptr;
    ID3D12CommandQueue* queue = nullptr;
    ID3D12CommandAllocator* allocator = nullptr;
    ID3D12GraphicsCommandList* list = nullptr;
    ID3D12DescriptorHeap* heap = nullptr;
    ID3D12Fence* fence = nullptr;
    IDXGISwapChain3* chain = nullptr;
    ID3D12Resource* buffers[2]{};
    HANDLE event = nullptr;
    UINT stride = 0;
    bool explicitEnumeration = false, animate = false;
    UINT64 fenceValue = 0, frames = 0, afterFrames = 0;
    unsigned recreations = 0, presentSignals = 0, resizeSignals = 0;
    HRESULT lastPresent = S_OK, lastResize = S_OK;
    bool extended = false, failed = false;
    LUID initial{};

    void Wait()
    {
        if (!queue || !fence) return;
        Check(queue->Signal(fence, ++fenceValue));
        if (fence->GetCompletedValue() < fenceValue)
        {
            Check(fence->SetEventOnCompletion(fenceValue, event));
            if (WaitForSingleObject(event, 3000) != WAIT_OBJECT_0) throw std::runtime_error("GPU fence did not complete");
        }
    }
    void ReleaseBuffers() { for (auto& buffer : buffers) Release(buffer); }
    void Clear()
    {
        ReleaseBuffers(); Release(list); Release(allocator); Release(heap);
        Release(chain); Release(fence); Release(queue); Release(device);
        if (event) CloseHandle(event);
        event = nullptr; fenceValue = 0;
    }
    ~Renderer() { Clear(); }
    void GetBuffers()
    {
        auto handle = heap->GetCPUDescriptorHandleForHeapStart();
        for (UINT i = 0; i < 2; ++i)
        {
            Check(chain->GetBuffer(i, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&buffers[i])));
            device->CreateRenderTargetView(buffers[i], nullptr, handle);
            handle.ptr += stride;
        }
    }
    void Create()
    {
        IDXGIAdapter1* selected = nullptr;
        if (explicitEnumeration)
        {
            IDXGIFactory1* enumeration = nullptr;
            Check(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&enumeration)));
            const HRESULT found = enumeration->EnumAdapters1(0, &selected);
            enumeration->Release(); Check(found);
        }
        const HRESULT creation = D3D12CreateDevice(selected, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), reinterpret_cast<void**>(&device));
        Release(selected); Check(creation);
        D3D12_COMMAND_QUEUE_DESC queueDescription{};
        queueDescription.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(device->CreateCommandQueue(&queueDescription, __uuidof(ID3D12CommandQueue), reinterpret_cast<void**>(&queue)));
        IDXGIFactory4* factory = nullptr;
        Check(CreateDXGIFactory1(__uuidof(IDXGIFactory4), reinterpret_cast<void**>(&factory)));
        RECT client{}; GetClientRect(window, &client);
        DXGI_SWAP_CHAIN_DESC1 description{};
        description.Width = (std::max)(1L, client.right);
        description.Height = (std::max)(1L, client.bottom);
        description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        description.BufferCount = 2;
        description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        IDXGISwapChain1* first = nullptr;
        const HRESULT created = factory->CreateSwapChainForHwnd(queue, window, &description, nullptr, nullptr, &first);
        factory->Release();
        Check(created);
        const HRESULT queried = first->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&chain));
        first->Release(); Check(queried);
        D3D12_DESCRIPTOR_HEAP_DESC heapDescription{};
        heapDescription.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        heapDescription.NumDescriptors = 2;
        Check(device->CreateDescriptorHeap(&heapDescription, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void**>(&heap)));
        stride = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        GetBuffers();
        Check(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof(ID3D12CommandAllocator), reinterpret_cast<void**>(&allocator)));
        Check(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, nullptr, __uuidof(ID3D12GraphicsCommandList), reinterpret_cast<void**>(&list)));
        Check(list->Close());
        Check(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence), reinterpret_cast<void**>(&fence)));
        event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!event) throw std::runtime_error("Fence event creation failed");
    }
    void Recreate() { Wait(); Clear(); Create(); ++recreations; }
    void Resize(UINT width, UINT height)
    {
        if (!chain || !width || !height || failed) return;
        Wait(); ReleaseBuffers();
        if (extended)
        {
            const UINT masks[2] = {1,1};
            IUnknown* queues[2] = {queue,queue};
            lastResize = chain->ResizeBuffers1(2, width, height, DXGI_FORMAT_UNKNOWN, 0, masks, queues);
        }
        else lastResize = chain->ResizeBuffers(2, width, height, DXGI_FORMAT_UNKNOWN, 0);
        if (lastResize == DXGI_ERROR_DEVICE_REMOVED || lastResize == DXGI_ERROR_DEVICE_RESET)
        { ++resizeSignals; Recreate(); }
        else { Check(lastResize); GetBuffers(); }
    }
    void Render()
    {
        if (failed || !chain) return;
        Check(allocator->Reset()); Check(list->Reset(allocator, nullptr));
        const UINT index = chain->GetCurrentBackBufferIndex();
        D3D12_RESOURCE_BARRIER barrier{};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = buffers[index];
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
        list->ResourceBarrier(1, &barrier);
        auto handle = heap->GetCPUDescriptorHandleForHeapStart(); handle.ptr += index * stride;
        const float color[4] = {animate ? (frames / 15 % 2 ? 0.75f : 0.15f) : 0.05f, 0.15f, 0.10f, 1};
        list->ClearRenderTargetView(handle, color, 0, nullptr);
        std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter);
        list->ResourceBarrier(1, &barrier); Check(list->Close());
        ID3D12CommandList* lists[] = {list}; queue->ExecuteCommandLists(1, lists); Wait();
        const DXGI_PRESENT_PARAMETERS parameters{};
        lastPresent = extended ? chain->Present1(1,0,&parameters) : chain->Present(1,0);
        if (lastPresent == DXGI_ERROR_DEVICE_REMOVED || lastPresent == DXGI_ERROR_DEVICE_RESET)
        { ++presentSignals; Recreate(); }
        else { Check(lastPresent); if (lastPresent == S_OK) { ++frames; if (recreations) ++afterFrames; } }
    }
} renderer;
LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM w, LPARAM l)
{
    if (message == WM_SIZE)
    {
        try { renderer.Resize(LOWORD(l), HIWORD(l)); }
        catch (const std::exception& error) { renderer.failed = true; std::fprintf(stderr, "%s\n", error.what()); }
        return 0;
    }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window,message,w,l);
}
BOOL CALLBACK SelectMonitor(HMONITOR monitor, HDC, LPRECT, LPARAM parameter)
{
    MONITORINFO info{};
    info.cbSize = sizeof(info);
    if (GetMonitorInfoW(monitor,&info) && !(info.dwFlags & MONITORINFOF_PRIMARY))
    { *reinterpret_cast<RECT*>(parameter) = info.rcWork; return FALSE; }
    return TRUE;
}
}
int main(int argc, char** argv)
{
    unsigned duration = 12000;
    std::string path;
    std::filesystem::path external;
    for (int i=1;i<argc;++i)
    {
        const std::string arg=argv[i];
        if (arg=="--extended-swap-chain") renderer.extended=true;
        if (arg=="--explicit-enumeration") renderer.explicitEnumeration=true;
        if (arg=="--external-control" && i+1<argc) external=argv[++i];
        if (arg=="--output" && i+1<argc) path=argv[++i];
        else if (arg=="--duration-ms" && i+1<argc) duration=std::clamp(std::stoul(argv[++i]),1000UL,20000UL);
    }
    int exitCode = 0;
    try
    {
        SetErrorMode(32771); setvbuf(stdout,nullptr,_IONBF,0);
        if (!external.empty())
        {
            if (!std::filesystem::is_directory(external) || std::filesystem::exists(external/"baseline.json"))
                throw std::runtime_error("External fixture needs a fresh prepared directory");
            if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                throw std::runtime_error("External fixture DPI context");
            renderer.animate=true; duration=60000;
        }
        WNDCLASSW definition{}; definition.lpfnWndProc=WindowProcedure; definition.hInstance=GetModuleHandleW(nullptr); definition.lpszClassName=L"RmD3D12RecoveryProbe";
        if (!RegisterClassW(&definition)) throw std::runtime_error("Window class registration failed");
        RECT display{40,40,0,0}; EnumDisplayMonitors(nullptr,nullptr,SelectMonitor,reinterpret_cast<LPARAM>(&display));
        const bool secondaryDisplay=display.right!=0;
        renderer.window=CreateWindowExW(WS_EX_NOACTIVATE,definition.lpszClassName,L"D3D12 recovery probe",WS_OVERLAPPEDWINDOW,
            display.left+40,display.top+40,640,400,nullptr,nullptr,definition.hInstance,nullptr);
        if (!renderer.window) throw std::runtime_error("Window creation failed");
        renderer.Create(); renderer.initial=renderer.device->GetAdapterLuid();
        ShowWindow(renderer.window,SW_SHOWNOACTIVATE);
        std::printf("{\"ready\":true,\"pid\":%lu,\"hwnd\":%llu,\"initialLuid\":\"%s\"}\n",GetCurrentProcessId(),
            static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(renderer.window)),Token(renderer.initial).c_str()); std::fflush(stdout);
        const auto start=GetTickCount64();
        UINT64 detachedAt=0;
        bool baseline=false, recreated=false, resized=false;
        auto publish = [&](const char* name) {
            FILETIME creation{},exit{},kernel{},user{};
            if (!GetProcessTimes(GetCurrentProcess(),&creation,&exit,&kernel,&user)) throw std::runtime_error("Process identity");
            BOOL debugger=TRUE;
            if (!CheckRemoteDebuggerPresent(GetCurrentProcess(),&debugger)) throw std::runtime_error("Debugger query");
            const auto id=renderer.device->GetAdapterLuid();
            std::ostringstream state;
            state << "{\"pid\":" << GetCurrentProcessId() << ",\"birth\":\"" << ((uint64_t(creation.dwHighDateTime)<<32)|creation.dwLowDateTime)
                << "\",\"luid\":\"" << ((uint64_t(uint32_t(id.HighPart))<<32)|id.LowPart) << "\",\"frames\":" << renderer.frames
                << ",\"recreations\":" << renderer.recreations << ",\"debugger\":" << (debugger?"true":"false")
                << ",\"observedPresentDeviceRemovedCount\":" << renderer.presentSignals << ",\"observedResizeDeviceRemovedCount\":" << renderer.resizeSignals
                << ",\"presentEntry\":\"" << reinterpret_cast<uintptr_t>((*(void***)renderer.chain)[renderer.extended?22:8]) << "\""
                << ",\"hwnd\":\"" << reinterpret_cast<uintptr_t>(renderer.window) << "\",\"secondaryDisplay\":" << (secondaryDisplay?"true":"false")
                << ",\"explicitEnumeration\":" << (renderer.explicitEnumeration?"true":"false") << ",\"adapters\":[";
            IDXGIFactory1* inventory=nullptr;
            Check(CreateDXGIFactory1(__uuidof(IDXGIFactory1),reinterpret_cast<void**>(&inventory)));
            for (UINT index=0;index<16;++index)
            {
                IDXGIAdapter1* adapter=nullptr;
                const auto hr=inventory->EnumAdapters1(index,&adapter);
                if (hr==DXGI_ERROR_NOT_FOUND) break;
                if (FAILED(hr)) { inventory->Release(); Check(hr); }
                DXGI_ADAPTER_DESC1 desc{}; const auto described=adapter->GetDesc1(&desc); adapter->Release();
                if (FAILED(described)) { inventory->Release(); Check(described); }
                if(index) state << ',';
                state << "{\"luid\":\"" << ((uint64_t(uint32_t(desc.AdapterLuid.HighPart))<<32)|desc.AdapterLuid.LowPart)
                    << "\",\"software\":" << ((desc.Flags&DXGI_ADAPTER_FLAG_SOFTWARE)?"true":"false") << '}';
            }
            inventory->Release(); state << "]}";
            const auto destination=external/name, pending=external/(std::string(name)+".pending");
            { std::ofstream file(pending); file << state.str(); file.flush(); if(!file) throw std::runtime_error("Fixture state write"); }
            std::filesystem::rename(pending,destination);
            std::puts(state.str().c_str());
        };
        while (GetTickCount64()-start<duration && !renderer.failed)
        {
            MSG message{};
            while (PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
            renderer.Render(); Sleep(16);
            if (!external.empty())
            {
                if (!baseline && renderer.frames>=20) { publish("baseline.json"); baseline=true; }
                if (!resized && std::filesystem::exists(external/"resize.request"))
                {
                    if (!SetWindowPos(renderer.window,nullptr,0,0,680,420,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE)) throw std::runtime_error("Fixture resize");
                    publish("resized.json"); resized=true;
                }
                if (!recreated && std::filesystem::exists(external/"recreate.request"))
                {
                    renderer.Recreate(); renderer.Render(); publish("recreated.json"); recreated=true;
                }
                if (!recreated && renderer.recreations && renderer.afterFrames) { publish("recreated.json"); recreated=true; }
                if (!detachedAt && std::filesystem::exists(external/"detach.complete"))
                {
                    BOOL debugger=TRUE;
                    if (!CheckRemoteDebuggerPresent(GetCurrentProcess(),&debugger) || debugger) throw std::runtime_error("Control not detached");
                    detachedAt=renderer.frames; publish("detached.json");
                }
                if (detachedAt && renderer.frames-detachedAt>=120) { publish("after-detach.json"); break; }
                if (std::filesystem::exists(external/"exit.request")) break;
            }
        }
        if (!external.empty() && !std::filesystem::exists(external/"after-detach.json")) throw std::runtime_error("External fixture did not complete detached observation");
        renderer.Wait();
    }
    catch (const std::exception& error) { renderer.failed=true; exitCode=1; std::fprintf(stderr,"%s\n",error.what()); }
    const LUID final=renderer.device ? renderer.device->GetAdapterLuid() : LUID{};
    ResourceManagerGpuObservation::Snapshot observed;
    const auto provider = GetModuleHandleW(L"ResourceManager.GpuPlacementShim.dll");
    const auto address = provider ? GetProcAddress(provider, "ResourceManagerGpuPlacementReadDeviceObservations") : nullptr;
    DWORD(WINAPI* read)(void*) = nullptr;
    static_assert(sizeof(read) == sizeof(address));
    std::memcpy(&read, &address, sizeof(read));
    const bool observationRead = read && read(&observed) == 1;
    std::ostringstream result;
    result << "{\"success\":" << (!renderer.failed && renderer.frames>0 && (!renderer.recreations || renderer.afterFrames>0) ? "true":"false")
        << ",\"pid\":" << GetCurrentProcessId() << ",\"initialLuid\":\"" << Token(renderer.initial) << "\",\"finalLuid\":\"" << Token(final) << "\""
        << ",\"recreateCount\":" << renderer.recreations << ",\"actualPresentDeviceRemovedCount\":" << renderer.presentSignals
        << ",\"actualResizeBuffersDeviceRemovedCount\":" << renderer.resizeSignals
        << ",\"simulatedPresentDeviceRemovedCount\":0,\"simulatedResizeBuffersDeviceRemovedCount\":0,\"explicitLowPowerAdapterOnRecreate\":false"
        << ",\"successfulPresents\":" << renderer.frames << ",\"presentsAfterRecovery\":" << renderer.afterFrames
        << ",\"lastPresentResult\":" << static_cast<uint32_t>(renderer.lastPresent) << ",\"lastResizeResult\":" << static_cast<uint32_t>(renderer.lastResize)
        << ",\"extendedSwapChain\":" << (renderer.extended?"true":"false") << ",\"recoveryFailed\":" << (renderer.failed?"true":"false")
        << ",\"observationRead\":" << (observationRead?"true":"false")
        << ",\"observedD3D11Devices\":" << observed.api[0].returnedDeviceCount
        << ",\"observedD3D12Devices\":" << observed.api[1].returnedDeviceCount << "}";
    if (!path.empty()) { std::ofstream file(path); file << result.str() << '\n'; }
    std::puts(result.str().c_str());
    renderer.Clear(); if(renderer.window) DestroyWindow(renderer.window);
    return renderer.failed ? 1 : exitCode;
}
