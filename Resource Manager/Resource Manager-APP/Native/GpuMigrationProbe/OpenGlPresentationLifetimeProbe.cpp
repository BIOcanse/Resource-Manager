#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlCoreRoutes.h"
#include "../GpuPlacementShim/OpenGlPixelFormatRoutes.h"
#include "../GpuPlacementShim/OpenGlWindowRoutes.h"
#include <cstdio>
#include <stdexcept>
#include <thread>

using namespace ResourceManagerOpenGl;
using namespace ResourceManagerOpenGl::Runtime;
unsigned checks{};
std::atomic<unsigned> targetCalls{}, sourceCalls{};
HANDLE entered{}, released{};
HDC expectedDc{};
LPPRESENTBUFFERS expectedPayload{};
BOOL nativeResult{}, argumentsMatched{};
DWORD nativeWait{};
void Check(bool value, const char* name) { if (!value) throw std::runtime_error(name); ++checks; }
template<typename T> struct Unused;
template<typename R, typename... A> struct Unused<R(WINAPI*)(A...)> {
    static R WINAPI Call(A...) { if constexpr(!std::is_void_v<R>) return R{}; }
};
BOOL WINAPI Install(HDC, LONG format) { return format == 1; }
BOOL WaitNative(HDC dc, LPPRESENTBUFFERS payload) {
    ++targetCalls; argumentsMatched = dc == expectedDc && payload == expectedPayload;
    SetEvent(entered); nativeWait = WaitForSingleObject(released, 5000);
    SetLastError(4321); return nativeResult;
}
BOOL WINAPI TargetSwap(HDC dc) { return WaitNative(dc, nullptr); }
BOOL WINAPI TargetPresent(HDC dc, LPPRESENTBUFFERS payload) { return WaitNative(dc, payload); }
BOOL WINAPI SourceSwap(HDC) { ++sourceCalls; SetLastError(1234); return TRUE; }

struct Window {
    HWND hwnd{}; HDC dc{}; DrawableReference record;
    bool Destroy() {
        if (!hwnd) return true;
        const BOOL dcResult = ReleaseDC(hwnd, dc);
        const BOOL windowResult = DestroyWindow(hwnd);
        if (windowResult) { hwnd = nullptr; dc = nullptr; }
        return dcResult && windowResult;
    }
    ~Window() { Destroy(); }
};
void Prepare(Window& window, const WNDCLASSW& wc, const std::shared_ptr<const DrawableTarget>& target,
    const std::shared_ptr<const PixelFormatDirectory>& directory) {
    window.hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, L"", WS_POPUP,
        0, 0, 16, 16, nullptr, nullptr, wc.hInstance, nullptr);
    window.dc = GetDC(window.hwnd); window.record = PrepareObservedDrawable(window.hwnd);
    Check(window.hwnd && window.dc && window.record && !IsWindowVisible(window.hwnd), "own hidden observed window");
    Check(drawables.PublishTarget(window.record, target, 0) && drawables.PublishDirectory(window.record, directory)
        && drawables.InstallPixelFormat(window.dc, 1), "original format and target publication");
}

int main() {
    SetErrorMode(32771);
    try {
        const auto foreground = GetForegroundWindow();
        WNDCLASSW wc{}; wc.style = CS_OWNDC; wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"RmPresentationLifetimeProbe";
        Check(RegisterClassW(&wc), "own class");
        IcdExports driver;
#define FIELD(name) driver.name = &Unused<decltype(driver.name)>::Call
        FIELD(validateVersion); FIELD(setCallbacks); FIELD(describePixelFormat); FIELD(createLayerContext);
        FIELD(setContext); FIELD(releaseContext); FIELD(deleteContext); FIELD(shareLists); FIELD(copyContext); FIELD(getProcAddress);
#undef FIELD
        driver.setPixelFormat = &Install; driver.swapBuffers = &TargetSwap; driver.presentBuffers = &TargetPresent;
        const auto target = DrawableTarget::Create(wc.hInstance, driver, {1,0}, {wc.hInstance,nullptr}, 1);
        PIXELFORMATDESCRIPTOR format{}; format.nSize = sizeof(format); format.nVersion = 1;
        format.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
        const auto directory = PixelFormatDirectory::Create({format}, 1);
        Check(target && directory, "explicit driver fixture"); systemSwap = &SourceSwap;
        for (unsigned index = 0; index < 4; ++index) {
            const bool present = index >= 2, destroy = (index % 2) != 0;
            Window first, second, replacement;
            Prepare(first, wc, target, directory); Prepare(second, wc, target, directory);
            drawables.CommitTargetPresentation(first.record, wc.hInstance, 1);
            drawables.CommitTargetPresentation(second.record, wc.hInstance, 1);
            Check(drawables.Read(first.dc).targetPresentation && drawables.Read(second.dc).targetPresentation, "two independent target windows");
            entered = CreateEventW(nullptr, TRUE, FALSE, nullptr); released = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            Check(entered && released, "bounded overlap events");
            PRESENTBUFFERS payload{}; payload.luidAdapter = {1,0};
            expectedDc = first.dc; expectedPayload = present ? &payload : nullptr; nativeResult = destroy ? FALSE : TRUE;
            const auto callDc = first.dc; const auto oldHwnd = first.hwnd;
            std::weak_ptr<DrawableRecord> borrowed = first.record;
            BOOL returned{}, threadIdentity{}; DWORD error{}, threadId{};
            FILETIME birth{}, exit{}, kernel{}, user{};
            const unsigned sourcesBefore = sourceCalls.load(), targetsBefore = targetCalls.load();
            std::thread inFlight([&] {
                threadId = GetCurrentThreadId();
                threadIdentity = GetThreadTimes(GetCurrentThread(), &birth, &exit, &kernel, &user);
                SetLastError(6789);
                returned = present ? PresentBuffers(callDc, &payload) : Swap(callDc);
                error = GetLastError();
            });
            try {
                Check(WaitForSingleObject(entered, 5000) == WAIT_OBJECT_0, "original driver entered");
                drawables.CommitSystemContextCreation(second.record, 2);
                Check(!drawables.Read(second.dc).targetPresentation && drawables.Read(first.dc).targetPresentation,
                    "other window restoration independent while first is in native call");
                Check(Swap(second.dc) && GetLastError() == 1234, "other window can present while native call is paused");
                drawables.CommitSystemContextCreation(first.record, 2);
                Check(!drawables.Read(first.dc).targetPresentation && Swap(first.dc) && GetLastError() == 1234,
                    "later first-window call sees source restoration without blocking");
                if (destroy) {
                    Check(first.Destroy() && !IsWindow(oldHwnd) && !first.record->active.load() && !drawables.Read(callDc),
                        "real destruction retires original record during native call");
                    Prepare(replacement, wc, target, directory);
                    drawables.CommitTargetPresentation(first.record, wc.hInstance, 3);
                    drawables.CommitSystemContextCreation(first.record, 4);
                    Check(replacement.record != first.record && !drawables.Read(replacement.dc).targetPresentation,
                        "late commits cannot alter replacement window");
                    first.record.reset();
                    Check(!borrowed.expired(), "in-flight route retains its original record");
                }
            } catch (...) { SetEvent(released); inFlight.join(); CloseHandle(entered); CloseHandle(released); throw; }
            SetEvent(released); inFlight.join(); CloseHandle(entered); CloseHandle(released);
            Check(threadIdentity && nativeWait == WAIT_OBJECT_0 && argumentsMatched && returned == nativeResult
                && error == 4321 && targetCalls.load() == targetsBefore + 1 && sourceCalls.load() == sourcesBefore + 2,
                "exact native result error arguments and one invocation preserved");
            if (destroy) Check(borrowed.expired(), "retired record released when native route exits");
            else Check(!drawables.Read(first.dc).targetPresentation, "late native completion cannot undo source restoration");
            Check(first.Destroy() && second.Destroy() && replacement.Destroy() && drawables.Count() == 0,
                "all own windows and original store cleaned");
            std::printf("{\"stage\":\"case\",\"index\":%u,\"presentBuffers\":%s,\"destroyedInFlight\":%s,\"returned\":%d,\"nativeError\":%lu,\"threadId\":%lu,\"creationFileTime\":%llu,\"normalCleanup\":true}\n",
                index, present ? "true" : "false", destroy ? "true" : "false", returned, error, threadId,
                (static_cast<unsigned long long>(birth.dwHighDateTime)<<32)|birth.dwLowDateTime);
            std::fflush(stdout);
        }
        Check(RemoveWindowObserversForCurrentThread() && UnregisterClassW(wc.lpszClassName, wc.hInstance)
            && contexts.Count() == 0 && GetForegroundWindow() == foreground, "observer context and foreground cleanup");
        FILETIME birth{}, exit{}, kernel{}, user{}; BOOL job{};
        Check(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user)
            && IsProcessInJob(GetCurrentProcess(), nullptr, &job) && job, "exact process and owned Job");
        std::printf("{\"stage\":\"complete\",\"passed\":true,\"checks\":%u,\"cases\":4,\"targetCalls\":%u,\"sourceCalls\":%u,\"remainingRecords\":0,\"remainingContexts\":0,\"nativeDriverSubstituted\":true,\"pid\":%lu,\"creationFileTime\":%llu,\"errorMode\":%u,\"inJob\":true,\"foregroundUnchanged\":true}\n",
            checks, targetCalls.load(), sourceCalls.load(), GetCurrentProcessId(),
            (static_cast<unsigned long long>(birth.dwHighDateTime)<<32)|birth.dwLowDateTime, GetErrorMode());
        return 0;
    } catch (const std::exception& e) { std::fprintf(stderr, "%s\n", e.what()); return 1; }
}
