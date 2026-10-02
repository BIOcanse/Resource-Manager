// Test window that presents at a fixed rate, for the live frame-timing ETW test.
//   dxgi: D3D11 flip-model swap chain, Present(0, 0)  -> DXGI runtime Present events
//   gl:   OpenGL SwapBuffers                          -> graphics-kernel Present events only
// Build: g++ -std=c++17 -O2 -static FramePresentGenerator.cpp -o FramePresentGenerator.exe -ld3d11 -ldxgi -lopengl32 -lgdi32 -luser32 -lwinmm
// Usage: FramePresentGenerator.exe <dxgi|gl> <fps> <seconds>   (prints the number of presented frames)
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <GL/gl.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>

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

int main(int argc, char** argv)
{
    if (argc < 4) return 2;
    const bool gl = std::strcmp(argv[1], "gl") == 0;
    if (!gl && std::strcmp(argv[1], "dxgi") != 0) return 2;
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

    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    IDXGISwapChain* swapChain = nullptr;
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
        if (gl)
        {
            glClearColor(shade, 0.3f, 0.2f, 1.0f);
            glClear(GL_COLOR_BUFFER_BIT);
            SwapBuffers(dc);
        }
        else
        {
            const float color[4] = { shade, 0.2f, 0.4f, 1.0f };
            context->OMSetRenderTargets(1, &target, nullptr);
            context->ClearRenderTargetView(target, color);
            swapChain->Present(0, 0);
        }
        ++frame;
    }
    timeEndPeriod(1);
    std::printf("%lld\n", frame);
    if (gl) { wglMakeCurrent(nullptr, nullptr); wglDeleteContext(glContext); ReleaseDC(window, dc); }
    else { target->Release(); swapChain->Release(); context->Release(); device->Release(); }
    DestroyWindow(window);
    return 0;
}
