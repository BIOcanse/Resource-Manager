// Test window that presents at a fixed rate, for the live frame-timing ETW test.
//   dxgi: D3D11 flip-model swap chain, Present(0, 0)  -> DXGI runtime Present events
//   dxgi1: D3D11 Present1, including a ResizeBuffers call
//   d3d12: D3D12 flip-model swap chain through the D3D11On12 compositor
//   gl:   OpenGL SwapBuffers                          -> graphics-kernel Present events only
//   glcore: OpenGL 3.3 core-profile SwapBuffers
// Build: g++ -std=c++17 -O2 -static FramePresentGenerator.cpp -o FramePresentGenerator.exe -ld3d11 -ld3d12 -ldxgi -lopengl32 -lgdi32 -luser32 -lwinmm -ladvapi32
// Usage: FramePresentGenerator.exe <dxgi|dxgi1|d3d12|gl|glcore> <fps> <seconds> [overlay-dll-path]
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi.h>
#include <dxgi1_2.h>
#include <dxgi1_4.h>
#include <GL/gl.h>
#include <GL/wglext.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include "OverlayTestProducer.h"

static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wParam, lParam);
}

static double Seconds(const LARGE_INTEGER& start, const LARGE_INTEGER& frequency)
{
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    return double(now.QuadPart - start.QuadPart) / double(frequency.QuadPart);
}

static int RunD3D12(HWND window, double fps, double seconds, OverlayTestProducer* producer)
{
    ID3D12Device* device = nullptr;
    ID3D12CommandQueue* queue = nullptr;
    IDXGIFactory4* factory = nullptr;
    IDXGISwapChain1* chain = nullptr;
    if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device),
            reinterpret_cast<void**>(&device)))) return 3;
    D3D12_COMMAND_QUEUE_DESC queueDesc{};
    if (FAILED(device->CreateCommandQueue(&queueDesc, __uuidof(ID3D12CommandQueue),
            reinterpret_cast<void**>(&queue)))
        || FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory4), reinterpret_cast<void**>(&factory)))) return 3;
    RECT bounds{};
    GetClientRect(window, &bounds);
    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = bounds.right;
    desc.Height = bounds.bottom;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    if (FAILED(factory->CreateSwapChainForHwnd(queue, window, &desc, nullptr, nullptr, &chain))) return 3;
    LARGE_INTEGER frequency{}, start{};
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&start);
    long long frame = 0;
    bool changed = false, disabled = false, resized = false;
    timeBeginPeriod(1);
    for (;;)
    {
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
        double elapsed = Seconds(start, frequency);
        if (elapsed >= seconds) break;
        const double due = frame / fps;
        while (elapsed < due)
        {
            if (due - elapsed > 0.002) Sleep(1);
            elapsed = Seconds(start, frequency);
        }
        if (producer && !changed && elapsed >= seconds / 2) { producer->ChangeBitmap(); changed = true; }
        if (!resized && elapsed >= seconds / 2)
        {
            if (FAILED(chain->ResizeBuffers(0, 0, 0, DXGI_FORMAT_UNKNOWN, 0))) return 6;
            resized = true;
        }
        if (producer && !disabled && elapsed >= seconds * 0.75) { producer->Disable(); disabled = true; }
        if (FAILED(chain->Present(0, 0))) return 6;
        ++frame;
    }
    timeEndPeriod(1);
    std::printf("%lld\n", frame);
    const bool passed = std::fabs(double(frame) - fps * seconds) <= 2.0
        && (!producer || producer->Check(frame));
    chain->Release(); factory->Release(); queue->Release(); device->Release();
    return passed ? 0 : 5;
}

int main(int argc, char** argv)
{
    if (argc < 4) return 2;
    const bool glCore = std::strcmp(argv[1], "glcore") == 0;
    const bool gl = glCore || std::strcmp(argv[1], "gl") == 0;
    const bool dxgi1 = std::strcmp(argv[1], "dxgi1") == 0;
    const bool d3d12 = std::strcmp(argv[1], "d3d12") == 0;
    if (!gl && !dxgi1 && !d3d12 && std::strcmp(argv[1], "dxgi") != 0) return 2;
    const double fps = std::atof(argv[2]);
    const double seconds = std::atof(argv[3]);
    if (fps <= 0 || fps > 1000 || seconds <= 0) return 2;

    WNDCLASSW windowClass{};
    windowClass.style = CS_OWNDC;
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"RmFramePresentGenerator";
    RegisterClassW(&windowClass);
    HWND window = CreateWindowExW(0, windowClass.lpszClassName, L"Frame present generator", WS_OVERLAPPEDWINDOW,
        100, 100, 640, 360, nullptr, nullptr, windowClass.hInstance, nullptr);
    ShowWindow(window, SW_SHOWNOACTIVATE);

    OverlayTestProducer producer;
    HMODULE overlay = nullptr;
    if (argc >= 5)
    {
        if (!producer.Start(window)) { std::fprintf(stderr, "overlay producer failed: %lu\n", GetLastError()); return 4; }
        overlay = LoadLibraryA(argv[4]);
        if (!overlay) { std::fprintf(stderr, "overlay LoadLibrary failed: %lu\n", GetLastError()); return 4; }
        using InitializeFn = BOOL(WINAPI*)();
        auto initialize = reinterpret_cast<InitializeFn>(GetProcAddress(overlay, "ResourceManagerPerformanceOverlayInitialize"));
        if (!initialize || !initialize()) { std::fprintf(stderr, "overlay hook initialization failed\n"); return 4; }
    }

    if (d3d12)
    {
        const int result = RunD3D12(window, fps, seconds, overlay ? &producer : nullptr);
        DestroyWindow(window);
        return result;
    }

    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    IDXGISwapChain* swapChain = nullptr;
    IDXGISwapChain1* swapChain1 = nullptr;
    ID3D11RenderTargetView* target = nullptr;
    HDC dc = nullptr;
    HGLRC glContext = nullptr;
    if (gl)
    {
        dc = GetDC(window);
        PIXELFORMATDESCRIPTOR format{ sizeof(format), 1, PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            PFD_TYPE_RGBA, 32, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 24, 8, 0, PFD_MAIN_PLANE, 0, 0, 0, 0 };
        SetPixelFormat(dc, ChoosePixelFormat(dc, &format), &format);
        glContext = wglCreateContext(dc);
        if (!glContext || !wglMakeCurrent(dc, glContext)) return 3;
        if (glCore)
        {
            auto createCore = reinterpret_cast<PFNWGLCREATECONTEXTATTRIBSARBPROC>(
                wglGetProcAddress("wglCreateContextAttribsARB"));
            if (!createCore) return 3;
            const int attributes[] = { WGL_CONTEXT_MAJOR_VERSION_ARB, 3,
                WGL_CONTEXT_MINOR_VERSION_ARB, 3,
                WGL_CONTEXT_PROFILE_MASK_ARB, WGL_CONTEXT_CORE_PROFILE_BIT_ARB, 0 };
            HGLRC core = createCore(dc, nullptr, attributes);
            if (!core) return 3;
            wglMakeCurrent(nullptr, nullptr);
            wglDeleteContext(glContext);
            glContext = core;
            if (!wglMakeCurrent(dc, glContext)) return 3;
        }
    }
    else
    {
        DXGI_SWAP_CHAIN_DESC description{};
        description.BufferCount = 2;
        description.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        description.OutputWindow = window;
        description.SampleDesc.Count = 1;
        description.Windowed = TRUE;
        description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        if (FAILED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
                D3D11_SDK_VERSION, &description, &swapChain, &device, nullptr, &context))) return 3;
        if (dxgi1 && FAILED(swapChain->QueryInterface(__uuidof(IDXGISwapChain1),
                reinterpret_cast<void**>(&swapChain1)))) return 3;
        ID3D11Texture2D* backBuffer = nullptr;
        swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer));
        device->CreateRenderTargetView(backBuffer, nullptr, &target);
        backBuffer->Release();
    }

    LARGE_INTEGER frequency, start;
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&start);
    const double period = 1.0 / fps;
    long long frame = 0;
    bool resized = false;
    bool bitmapChanged = false;
    bool overlayDisabled = false;
    timeBeginPeriod(1);
    for (;;)
    {
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
        double elapsed = Seconds(start, frequency);
        if (elapsed >= seconds) break;
        const double due = frame * period;
        while (elapsed < due)
        {
            if (due - elapsed > 0.002) Sleep(1);
            elapsed = Seconds(start, frequency);
        }
        const float shade = float(frame % 120) / 120.0f;
        if (overlay && !bitmapChanged && elapsed >= seconds / 2)
        {
            producer.ChangeBitmap();
            bitmapChanged = true;
        }
        if (overlay && !overlayDisabled && elapsed >= seconds * 0.75)
        {
            producer.Disable();
            overlayDisabled = true;
        }
        if (dxgi1 && !resized && elapsed >= seconds / 2)
        {
            context->ClearState();
            target->Release();
            if (FAILED(swapChain->ResizeBuffers(0, 0, 0, DXGI_FORMAT_UNKNOWN, 0))) return 6;
            ID3D11Texture2D* backBuffer = nullptr;
            swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer));
            device->CreateRenderTargetView(backBuffer, nullptr, &target);
            backBuffer->Release();
            resized = true;
        }
        if (gl)
        {
            glClearColor(shade, 0.3f, 0.2f, 1.0f);
            glClear(GL_COLOR_BUFFER_BIT);
            if (!SwapBuffers(dc)) return 6;
        }
        else
        {
            const float color[4] = { shade, 0.2f, 0.4f, 1.0f };
            context->OMSetRenderTargets(1, &target, nullptr);
            context->ClearRenderTargetView(target, color);
            HRESULT present = S_OK;
            if (dxgi1) { DXGI_PRESENT_PARAMETERS parameters{}; present = swapChain1->Present1(0, 0, &parameters); }
            else present = swapChain->Present(0, 0);
            if (FAILED(present)) return 6;
        }
        ++frame;
    }
    timeEndPeriod(1);
    std::printf("%lld\n", frame);
    const bool ratePassed = std::fabs(double(frame) - fps * seconds) <= 2.0;
    const bool overlayPassed = !overlay || producer.Check(frame);
    if (gl) { wglMakeCurrent(nullptr, nullptr); wglDeleteContext(glContext); ReleaseDC(window, dc); }
    else { target->Release(); if (swapChain1) swapChain1->Release(); swapChain->Release(); context->Release(); device->Release(); }
    DestroyWindow(window);
    return ratePassed && overlayPassed ? 0 : 5;
}
