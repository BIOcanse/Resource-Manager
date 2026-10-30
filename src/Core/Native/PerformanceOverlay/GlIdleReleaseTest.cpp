#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <GL/gl.h>
#include "IdleReleaseTest.h"

using namespace IdleReleaseTest;

struct Renderer
{
    Window window;
    HDC dc = nullptr;
    HGLRC context = nullptr;

    explicit Renderer(bool core)
    {
        LoadOverlay();
        dc = GetDC(window.handle);
        Check(dc != nullptr, "GetDC failed");
        PIXELFORMATDESCRIPTOR format{};
        format.nSize = sizeof(format);
        format.nVersion = 1;
        format.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
        format.iPixelType = PFD_TYPE_RGBA;
        format.cColorBits = 32;
        format.cAlphaBits = 8;
        const int selected = ChoosePixelFormat(dc, &format);
        Check(selected != 0 && SetPixelFormat(dc, selected, &format), "SetPixelFormat failed");
        context = wglCreateContext(dc);
        Check(context && wglMakeCurrent(dc, context), "Create/make current GL context failed");
        if (core)
        {
            using CreateContext = HGLRC(WINAPI*)(HDC, HGLRC, const int*);
            const auto address = wglGetProcAddress("wglCreateContextAttribsARB");
            CreateContext create = nullptr;
            static_assert(sizeof(create) == sizeof(address));
            std::memcpy(&create, &address, sizeof(create));
            Check(create != nullptr, "GL core unavailable: wglCreateContextAttribsARB is missing");
            const int attributes[] = {0x2091, 3, 0x2092, 3, 0x9126, 1, 0};
            HGLRC replacement = create(dc, nullptr, attributes);
            Check(replacement != nullptr, "GL core unavailable: OpenGL 3.3 core context creation failed");
            Check(wglMakeCurrent(nullptr, nullptr), "Release compatibility context failed");
            Check(wglDeleteContext(context), "Delete compatibility context failed");
            context = replacement;
            Check(wglMakeCurrent(dc, context), "Make core context current failed");
        }
        const char* version = reinterpret_cast<const char*>(glGetString(GL_VERSION));
        std::printf("GL version=%s context=%s\n", version ? version : "unknown", core ? "core" : "compatibility");
    }

    ~Renderer()
    {
        if (context)
        {
            wglMakeCurrent(dc, context);
            wglDeleteContext(context);
            wglMakeCurrent(nullptr, nullptr);
        }
        if (dc) ReleaseDC(window.handle, dc);
    }

    void Frame()
    {
        glViewport(0, 0, Width, Height);
        glDrawBuffer(GL_BACK);
        glClearColor(0, 0, 0, 1);
        glClear(GL_COLOR_BUFFER_BIT);
        Check(SwapBuffers(dc) != FALSE, "GL SwapBuffers failed");
        Check(glGetError() == GL_NO_ERROR, "GL frame reported an error");
    }

    void CheckPixel(uint32_t)
    {
        // A hidden window's front buffer is not a portable readback source. Run checks the
        // layer's draw counter, which advances only after a successful textured GL draw.
    }
};

int main(int argc, char** argv)
{
    const bool core = argc > 1 && std::strcmp(argv[1], "--core") == 0;
    try
    {
        Renderer renderer(core);
        Run(renderer, core ? "GL-core" : "GL-compatibility");
        return 0;
    }
    catch (const std::exception& error)
    {
        std::fprintf(stderr, "GL %s FAIL: %s (Win32 error %lu)\n",
            core ? "core" : "compatibility", error.what(), GetLastError());
        return 1;
    }
}
