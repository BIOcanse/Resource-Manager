#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlCoreRoutes.h"
#include "../GpuPlacementShim/OpenGlPixelFormatRoutes.h"
#include <cstdio>
#include <stdexcept>
#include <thread>

using namespace ResourceManagerOpenGl;
using namespace ResourceManagerOpenGl::Runtime;
unsigned checks{}, creates{}, deletes{};
void Check(bool value, const char* text) { if (!value) throw std::runtime_error(text); ++checks; }
template<typename T> struct Unused;
template<typename R, typename... A> struct Unused<R(WINAPI*)(A...)> {
    static R WINAPI Call(A...) { if constexpr(!std::is_void_v<R>) return R{}; }
};
DHGLRC WINAPI CreateNative(HDC, int) { return ++creates; }
BOOL WINAPI DeleteNative(DHGLRC) { ++deletes; return TRUE; }

int main() {
    SetErrorMode(32771);
    try {
        const auto foreground = GetForegroundWindow();
        WNDCLASSW wc{}; wc.style = CS_OWNDC; wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"RmPresentationOrderProbe";
        Check(RegisterClassW(&wc), "own class");
        HWND windows[2]{}; HDC dcs[2]{}; DrawableReference records[2];
        IcdExports driver;
#define FIELD(name) driver.name = &Unused<decltype(driver.name)>::Call
        FIELD(validateVersion); FIELD(setCallbacks); FIELD(describePixelFormat); FIELD(setPixelFormat);
        FIELD(setContext); FIELD(releaseContext); FIELD(shareLists); FIELD(copyContext); FIELD(swapBuffers); FIELD(getProcAddress);
#undef FIELD
        driver.createLayerContext = &CreateNative; driver.deleteContext = &DeleteNative;
        const auto target = DrawableTarget::Create(wc.hInstance, driver, {1, 0}, {wc.hInstance, nullptr}, 1);
        Check(target != nullptr, "same original adapter description");
        for (unsigned i=0; i<2; ++i) {
            windows[i] = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, wc.lpszClassName, L"", WS_POPUP,
                0, 0, 16, 16, nullptr, nullptr, wc.hInstance, nullptr);
            dcs[i] = GetDC(windows[i]); records[i] = PrepareObservedDrawable(windows[i]);
            Check(windows[i] && dcs[i] && records[i] && !IsWindowVisible(windows[i]), "own observed window");
            Check(drawables.PublishTarget(records[i], target, 0) && !drawables.Read(dcs[i]).targetPresentation, "preparation does not hand off");
        }
        auto first = contexts.Create(wc.hInstance, driver, dcs[0], 0);
        Check(first && first->applicationCreationOrder == 0, "internal native creation has no application order");
        drawables.CommitTargetPresentation(records[1], wc.hInstance, first->applicationCreationOrder);
        Check(!drawables.Read(dcs[1]).targetPresentation, "unpublished internal object cannot hand off");
        const auto firstOrder = contexts.PublishApplicationCreation(first);
        Check(firstOrder == 1 && first->applicationCreationOrder == firstOrder, "first actual application publication");
        drawables.CommitTargetPresentation(records[0], wc.hInstance, firstOrder);
        Check(drawables.Read(dcs[0]).targetPresentation && !drawables.Read(dcs[1]).targetPresentation, "A creation affects only A");
        drawables.CommitTargetPresentation(records[1], wc.hInstance, firstOrder);
        Check(drawables.Read(dcs[1]).targetPresentation, "A object can hand off new B");
        const auto restoredOrder = contexts.PublishApplicationCreation(nullptr);
        Check(restoredOrder == 2, "source successful creation shares ordering");
        drawables.CommitSystemContextCreation(records[1], restoredOrder);
        Check(!drawables.Read(dcs[1]).targetPresentation, "new source creation restores B");
        drawables.CommitTargetPresentation(records[1], wc.hInstance, firstOrder);
        Check(!drawables.Read(dcs[1]).targetPresentation, "old A bind cannot undo B restore");
        Check(contexts.PublishApplicationCreation(first) == firstOrder, "same object does not become newly created again");
        auto next = contexts.Create(wc.hInstance, driver, dcs[0], 0);
        const auto nextOrder = contexts.PublishApplicationCreation(next);
        Check(next && nextOrder == 3, "new A receives next successful order");
        drawables.CommitTargetPresentation(records[1], nullptr, nextOrder);
        Check(!drawables.Read(dcs[1]).targetPresentation, "wrong module cannot consume order");
        drawables.CommitTargetPresentation(records[1], wc.hInstance, nextOrder);
        Check(drawables.Read(dcs[1]).targetPresentation, "new A can hand off restored B");
        drawables.CommitSystemContextCreation(records[1], restoredOrder);
        Check(drawables.Read(dcs[1]).targetPresentation, "older source completion cannot reverse newer target");
        const auto latest = contexts.PublishApplicationCreation(nullptr);
        std::thread source([&] { for(unsigned i=0;i<1000;++i) drawables.CommitSystemContextCreation(records[1], latest); });
        std::thread older([&] { for(unsigned i=0;i<1000;++i) drawables.CommitTargetPresentation(records[1], wc.hInstance, nextOrder); });
        source.join(); older.join();
        Check(!drawables.Read(dcs[1]).targetPresentation, "original window lock preserves newer source under concurrent commits");
        Check(contexts.DestroyUnbound(first) && contexts.DestroyUnbound(next) && contexts.Count() == 0
            && creates == 2 && deletes == 2, "original context owner cleanup");
        for(unsigned i=0;i<2;++i) {
            Check(ReleaseDC(windows[i], dcs[i]) && DestroyWindow(windows[i]), "normal window cleanup");
            drawables.CommitTargetPresentation(records[i], wc.hInstance, latest + 1);
            Check(!records[i]->active.load(), "late commit cannot resurrect retired record");
        }
        Check(drawables.Count() == 0 && RemoveWindowObserversForCurrentThread()
            && UnregisterClassW(wc.lpszClassName, wc.hInstance), "original store and observer cleanup");
        Check(GetForegroundWindow() == foreground, "foreground unchanged");
        FILETIME b{},e{},k{},u{}; BOOL job{};
        Check(GetProcessTimes(GetCurrentProcess(), &b,&e,&k,&u) && IsProcessInJob(GetCurrentProcess(), nullptr, &job) && job, "native process identity");
        std::printf("{\"passed\":true,\"checks\":%u,\"commits\":2000,\"remainingRecords\":0,\"remainingContexts\":0,\"nativeDriverSubstituted\":true,\"pid\":%lu,\"creationFileTime\":%llu,\"errorMode\":%u,\"inJob\":true,\"foregroundUnchanged\":true}\n",
            checks, GetCurrentProcessId(), (static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime, GetErrorMode());
        return 0;
    } catch(const std::exception& e) { std::fprintf(stderr,"%s\n",e.what()); return 1; }
}
