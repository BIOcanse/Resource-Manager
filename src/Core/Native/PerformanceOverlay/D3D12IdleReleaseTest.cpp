#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include "IdleReleaseTest.h"

using namespace IdleReleaseTest;

struct Renderer
{
    Window window;
    IDXGIFactory4* factory = nullptr;
    ID3D12Device* device = nullptr;
    ID3D12CommandQueue* queue = nullptr;
    ID3D12CommandAllocator* allocator = nullptr;
    ID3D12GraphicsCommandList* commands = nullptr;
    ID3D12Fence* fence = nullptr;
    HANDLE completed = nullptr;
    UINT64 nextFence = 1;
    IDXGISwapChain3* chain = nullptr;
    ID3D12DescriptorHeap* targets = nullptr;
    std::vector<ID3D12Resource*> buffers;
    UINT targetStride = 0;

    // RuntimeRead: Fixed
    explicit Renderer(bool overlay = true)
    {
        if (overlay) LoadOverlay();
        Check(SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory4), reinterpret_cast<void**>(&factory))),
            "CreateDXGIFactory1 failed");
        IDXGIAdapter* warp = nullptr;
        Check(SUCCEEDED(factory->EnumWarpAdapter(__uuidof(IDXGIAdapter), reinterpret_cast<void**>(&warp))),
            "EnumWarpAdapter failed");
        const HRESULT created = D3D12CreateDevice(warp, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device),
            reinterpret_cast<void**>(&device));
        Drop(warp);
        Check(SUCCEEDED(created), "D3D12 WARP device creation failed");
        D3D12_COMMAND_QUEUE_DESC queueDesc{};
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(SUCCEEDED(device->CreateCommandQueue(&queueDesc, __uuidof(ID3D12CommandQueue),
            reinterpret_cast<void**>(&queue))), "CreateCommandQueue failed");
        Check(SUCCEEDED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
            __uuidof(ID3D12CommandAllocator), reinterpret_cast<void**>(&allocator))), "CreateCommandAllocator failed");
        Check(SUCCEEDED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, nullptr,
            __uuidof(ID3D12GraphicsCommandList), reinterpret_cast<void**>(&commands))), "CreateCommandList failed");
        Check(SUCCEEDED(commands->Close()), "Initial command list close failed");
        Check(SUCCEEDED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence),
            reinterpret_cast<void**>(&fence))), "CreateFence failed");
        completed = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        Check(completed != nullptr, "CreateEventW failed");
        CreateChain();
    }

    void Wait()
    {
        const UINT64 value = nextFence++;
        Check(SUCCEEDED(queue->Signal(fence, value)), "D3D12 queue Signal failed");
        Check(SUCCEEDED(fence->SetEventOnCompletion(value, completed)), "SetEventOnCompletion failed");
        Check(WaitForSingleObject(completed, 10000) == WAIT_OBJECT_0, "D3D12 queue drain timed out");
    }

    void CreateTargets()
    {
        D3D12_DESCRIPTOR_HEAP_DESC heap{};
        heap.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        heap.NumDescriptors = 2;
        Check(SUCCEEDED(device->CreateDescriptorHeap(&heap, __uuidof(ID3D12DescriptorHeap),
            reinterpret_cast<void**>(&targets))), "CreateDescriptorHeap failed");
        targetStride = device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        auto target = targets->GetCPUDescriptorHandleForHeapStart();
        buffers.resize(2, nullptr);
        for (UINT i = 0; i < 2; ++i)
        {
            Check(SUCCEEDED(chain->GetBuffer(i, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&buffers[i]))),
                "D3D12 GetBuffer failed");
            device->CreateRenderTargetView(buffers[i], nullptr, target);
            target.ptr += targetStride;
        }
    }

    void CreateChain()
    {
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = Width;
        desc.Height = Height;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        IDXGISwapChain1* created = nullptr;
        const HRESULT result = factory->CreateSwapChainForHwnd(queue, window.handle, &desc, nullptr, nullptr, &created);
        if (FAILED(result)) std::printf("D3D12 CreateSwapChainForHwnd HRESULT=%08lx\n", static_cast<unsigned long>(result));
        Check(SUCCEEDED(result), "D3D12 CreateSwapChainForHwnd failed");
        const HRESULT queried = created->QueryInterface(__uuidof(IDXGISwapChain3), reinterpret_cast<void**>(&chain));
        if (SUCCEEDED(queried))
        {
            HMODULE releaseOwner = nullptr;
            void** methods = *reinterpret_cast<void***>(chain);
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(methods[2]), &releaseOwner);
            const HMODULE overlay = GetModuleHandleW(L"ResourceManager.PerformanceOverlay.dll");
            std::printf("D3D12 swapchain interfaces_same=%s release_hooked=%s\n",
                static_cast<void*>(created) == static_cast<void*>(chain) ? "yes" : "no",
                overlay && releaseOwner == overlay ? "yes" : "no");
            std::fflush(stdout);
        }
        Drop(created);
        Check(SUCCEEDED(queried), "D3D12 swapchain3 QueryInterface failed");
        CreateTargets();
    }

    void ClearTargets()
    {
        for (auto*& buffer : buffers) Drop(buffer);
        buffers.clear();
        Drop(targets);
    }

    void Resize()
    {
        Wait();
        ClearTargets();
        Check(SUCCEEDED(chain->ResizeBuffers(2, Width, Height, DXGI_FORMAT_B8G8R8A8_UNORM, 0)),
            "D3D12 ResizeBuffers failed (overlay retained wrapped buffers)");
        CreateTargets();
    }

    void Retire()
    {
        Wait();
        ClearTargets();
        if (chain)
        {
            HMODULE owner = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>((*reinterpret_cast<void***>(chain))[2]), &owner);
            std::printf("D3D12 retiring release_hooked=%s\n",
                owner && owner == GetModuleHandleW(L"ResourceManager.PerformanceOverlay.dll") ? "yes" : "no");
            const ULONG remaining = chain->Release();
            chain = nullptr;
            std::printf("D3D12 retired swapchain remaining_refs=%lu\n", remaining);
            std::fflush(stdout);
        }
    }

    ~Renderer()
    {
        if (chain) Retire();
        Drop(commands); Drop(allocator); Drop(fence); Drop(queue); Drop(device); Drop(factory);
        if (completed) CloseHandle(completed);
    }

    void Frame()
    {
        Check(SUCCEEDED(allocator->Reset()), "Command allocator Reset failed");
        Check(SUCCEEDED(commands->Reset(allocator, nullptr)), "Command list Reset failed");
        const UINT index = chain->GetCurrentBackBufferIndex();
        auto target = targets->GetCPUDescriptorHandleForHeapStart();
        target.ptr += size_t(index) * targetStride;
        D3D12_RESOURCE_BARRIER barrier{};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = buffers[index];
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
        commands->ResourceBarrier(1, &barrier);
        const FLOAT black[4] = {0, 0, 0, 1};
        commands->ClearRenderTargetView(target, black, 0, nullptr);
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
        commands->ResourceBarrier(1, &barrier);
        Check(SUCCEEDED(commands->Close()), "Command list Close failed");
        ID3D12CommandList* lists[] = {commands};
        queue->ExecuteCommandLists(1, lists);
        Check(SUCCEEDED(chain->Present(0, 0)), "D3D12 Present failed");
        Wait();
    }

    void CheckPixel(uint32_t) {}
};

int main(int argc, char** argv)
{
    const bool baseline = argc > 1 && std::strcmp(argv[1], "--baseline") == 0;
    const bool hooksOnly = argc > 1 && std::strcmp(argv[1], "--hooks-only") == 0;
    try
    {
        Renderer renderer(!baseline);
        if (baseline || hooksOnly)
        {
            for (int i = 0; i < 6; ++i)
            {
                for (int frame = 0; frame < 4; ++frame) renderer.Frame();
                renderer.Retire();
                renderer.CreateChain();
            }
            std::printf("D3D12 %s swapchain retirement/recreate x6 PASS\n",
                baseline ? "baseline-without-overlay" : "hooks-without-bitmap");
            return 0;
        }
        Run(renderer, "D3D12-WARP");
        Publisher publisher;
        publisher.Open(renderer.window.handle, 0xffff0000u);
        Sleep(300); // The reader bounds absent-section probes to 250 ms.
        for (int i = 0; i < 8; ++i) renderer.Frame();
        const auto beforeResize = OverlayProtocol::Read64(&publisher.header->drawSuccessCounter);
        Check(beforeResize > 0, "D3D12 did not draw before ResizeBuffers");
        renderer.Resize();
        for (int i = 0; i < 8; ++i) renderer.Frame();
        Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) > beforeResize,
            "D3D12 did not recover after ResizeBuffers");
        std::puts("D3D12 enabled ResizeBuffers PASS");
        for (int i = 0; i < 6; ++i)
        {
            renderer.Retire();
            renderer.CreateChain();
            const auto beforeDraw = OverlayProtocol::Read64(&publisher.header->drawSuccessCounter);
            for (int frame = 0; frame < 4; ++frame) renderer.Frame();
            Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) > beforeDraw,
                "D3D12 did not draw on replacement swapchain");
        }
        publisher.Disable();
        for (int i = 0; i < 8; ++i) renderer.Frame();
        publisher.Close();
        publisher.CheckGone();
        std::puts("D3D12 enabled swapchain retirement/recreate x6 PASS");
        return 0;
    }
    catch (const std::exception& error)
    {
        std::fprintf(stderr, "D3D12 FAIL: %s (Win32 error %lu)\n", error.what(), GetLastError());
        return 1;
    }
}
