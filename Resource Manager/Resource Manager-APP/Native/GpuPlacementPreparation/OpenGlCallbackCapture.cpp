#include "OpenGlCallbackCapture.h"
#include "../GpuPlacementShim/OpenGlAdapterIcd.h"
#include "../GpuPlacementShim/third_party/minhook/include/MinHook.h"
#include <algorithm>
#include <atomic>
#include <mutex>
#include <stdexcept>
#ifdef _MSC_VER
#include <intrin.h>
#endif

namespace ResourceManagerOpenGl
{
namespace
{
std::atomic_flag attempted = ATOMIC_FLAG_INIT;
GlInfo selected{};
HMODULE openGl{};
QueryAdapter originalQuery{};
decltype(&DrvSetCallbackProcs) originalRegistration{};
std::mutex captureMutex;
WGLCALLBACKS captured{};
bool captureSeen{}, captureInvalid{};

template<typename T> T Entry(HMODULE module, const char* name)
{
    const auto address = module ? GetProcAddress(module, name) : nullptr;
    if (!address) throw static_cast<DWORD>(ERROR_PROC_NOT_FOUND);
    T output{}; static_assert(sizeof(output) == sizeof(address));
    std::memcpy(&output, &address, sizeof(output));
    return output;
}
void Require(BOOL okay)
{
    if (!okay) { const DWORD error = GetLastError(); throw error ? error : static_cast<DWORD>(ERROR_GEN_FAILURE); }
}
void RequireHook(MH_STATUS status) { if (status != MH_OK) throw static_cast<DWORD>(ERROR_FUNCTION_FAILED); }

LONG WINAPI SelectIcd(const QueryInfo* request)
{
#ifdef _MSC_VER
    const void* caller = _ReturnAddress();
#else
    const void* caller = __builtin_return_address(0);
#endif
    const LONG result = originalQuery(request);
    const DWORD error = GetLastError();
    MEMORY_BASIC_INFORMATION memory{};
    if (result >= 0 && request && request->type == 2 && request->data && request->size == sizeof(selected)
        && VirtualQuery(caller, &memory, sizeof(memory)) && memory.AllocationBase == openGl)
        std::memcpy(request->data, &selected, sizeof(selected));
    SetLastError(error);
    return result;
}

void WINAPI Capture(int count, PROC* values)
{
    const DWORD error = GetLastError();
    {
        std::lock_guard<std::mutex> guard(captureMutex);
        if (count != 9 || !values) captureInvalid = true;
        else {
            if (captureSeen && std::memcmp(&captured, values, sizeof(captured))) captureInvalid = true;
            std::memcpy(&captured, values, sizeof(captured));
            captureSeen = true;
        }
    }
    SetLastError(error);
    originalRegistration(count, values);
}

struct AcquisitionOwner
{
    HINSTANCE instance{GetModuleHandleW(nullptr)};
    HMODULE driver{};
    HWND window{};
    HDC dc{};
    HGLRC context{};
    void* queryHook{};
    void* captureHook{};
    bool hooksInitialized{}, queryCreated{}, captureCreated{}, queryEnabled{}, captureEnabled{}, classCreated{};
    static constexpr const wchar_t* windowClass = L"ResourceManager.OpenGlCallbackPreparation";

    DWORD Cleanup() noexcept
    {
        DWORD error{};
        const auto record = [&error](BOOL okay) {
            if (!okay && !error) { error = GetLastError(); if (!error) error = ERROR_GEN_FAILURE; }
        };
        if (context) {
            if (wglGetCurrentContext() == context) record(wglMakeCurrent(nullptr, nullptr));
            record(wglDeleteContext(context));
        }
        if (dc) record(ReleaseDC(window, dc) != 0);
        if (window) record(DestroyWindow(window));
        if (classCreated) record(UnregisterClassW(windowClass, instance));
        const auto unhook = [&error](void* target, bool enabled, bool created) {
            if (enabled && MH_DisableHook(target) != MH_OK && !error) error = ERROR_FUNCTION_FAILED;
            if (created && MH_RemoveHook(target) != MH_OK && !error) error = ERROR_FUNCTION_FAILED;
        };
        unhook(captureHook, captureEnabled, captureCreated);
        unhook(queryHook, queryEnabled, queryCreated);
        if (hooksInitialized && MH_Uninitialize() != MH_OK && !error) error = ERROR_FUNCTION_FAILED;
        if (driver) record(FreeLibrary(driver));
        return error;
    }
};
}

IcdCallbackAcquisition AcquireIcdCallbackSource(LUID sourceAdapter, IcdCallbackSource& output)
{
    const DWORD priorError = GetLastError();
    if (attempted.test_and_set()) return {ERROR_ALREADY_INITIALIZED, 0};
    AcquisitionOwner owner;
    IcdCallbackAcquisition result;
    IcdCallbackSource candidate;
    try {
        const auto gdi = GetModuleHandleW(L"gdi32.dll");
        openGl = GetModuleHandleW(L"opengl32.dll");
        if (!openGl) throw static_cast<DWORD>(ERROR_MOD_NOT_FOUND);
        const auto query = Entry<QueryAdapter>(gdi, "D3DKMTQueryAdapterInfo");
        const auto selectedIcd = QueryAdapterIcd(sourceAdapter, selected);
        result.cleanupError = selectedIcd.cleanupError;
        if (!selectedIcd.Succeeded()) throw selectedIcd.error ? selectedIcd.error : selectedIcd.cleanupError;
        owner.driver = LoadLibraryW(selected.filename);
        Require(owner.driver != nullptr);
        const auto registration = Entry<decltype(&DrvSetCallbackProcs)>(owner.driver, "DrvSetCallbackProcs");
        owner.queryHook = reinterpret_cast<void*>(query);
        owner.captureHook = reinterpret_cast<void*>(registration);
        RequireHook(MH_Initialize()); owner.hooksInitialized = true;
        RequireHook(MH_CreateHook(owner.queryHook, reinterpret_cast<void*>(&SelectIcd), reinterpret_cast<void**>(&originalQuery)));
        owner.queryCreated = true;
        RequireHook(MH_CreateHook(owner.captureHook, reinterpret_cast<void*>(&Capture), reinterpret_cast<void**>(&originalRegistration)));
        owner.captureCreated = true;
        RequireHook(MH_EnableHook(owner.captureHook)); owner.captureEnabled = true;
        RequireHook(MH_EnableHook(owner.queryHook)); owner.queryEnabled = true;
        WNDCLASSW wc{};
        wc.style = CS_OWNDC; wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = owner.instance; wc.lpszClassName = AcquisitionOwner::windowClass;
        Require(RegisterClassW(&wc) != 0); owner.classCreated = true;
        owner.window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, L"",
            WS_POPUP, 0, 0, 16, 16, nullptr, nullptr, owner.instance, nullptr);
        Require(owner.window != nullptr);
        owner.dc = GetDC(owner.window); Require(owner.dc != nullptr);
        PIXELFORMATDESCRIPTOR format{};
        format.nSize = sizeof(format); format.nVersion = 1;
        format.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
        format.iPixelType = PFD_TYPE_RGBA; format.cColorBits = 24; format.cAlphaBits = 8;
        format.cDepthBits = 24; format.cStencilBits = 8;
        const int index = ChoosePixelFormat(owner.dc, &format); Require(index != 0);
        Require(SetPixelFormat(owner.dc, index, &format));
        owner.context = wglCreateContext(owner.dc); Require(owner.context != nullptr);
        Require(wglMakeCurrent(owner.dc, owner.context));
        {
            std::lock_guard<std::mutex> guard(captureMutex);
            if (!captureSeen || captureInvalid) throw static_cast<DWORD>(ERROR_INVALID_DATA);
            Require(DescribeIcdCallbackSource(captured, candidate));
        }
    } catch (DWORD error) { result.error = error; }
      catch (const std::bad_alloc&) { result.error = ERROR_NOT_ENOUGH_MEMORY; }
      catch (...) { result.error = ERROR_UNHANDLED_EXCEPTION; }
    const DWORD cleanupError = owner.Cleanup();
    if (!result.cleanupError) result.cleanupError = cleanupError;
    if (result.Succeeded()) output = std::move(candidate);
    SetLastError(priorError);
    return result;
}
}
