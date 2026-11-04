#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlCoreRoutes.h"
#include "../GpuPlacementShim/OpenGlPixelFormatRoutes.h"
#include <cstdio>
#include <stdexcept>
#include <thread>

using namespace ResourceManagerOpenGl;
using namespace ResourceManagerOpenGl::Runtime;
unsigned checks{}, sets{}, unexpected{};
unsigned systemWrites{}, systemReads{};
void Check(bool value, const char* name) { if (!value) throw std::runtime_error(name); ++checks; }
template<typename T> struct Unused;
template<typename R, typename... A> struct Unused<R(WINAPI*)(A...)> {
    static R WINAPI Call(A...) { ++unexpected; if constexpr(!std::is_void_v<R>) return R{}; }
};
int WINAPI NoSystemFormat(HDC) { return 0; }
BOOL WINAPI Install(HDC, LONG value) { ++sets; return value == 1; }
PROC WINAPI LookupNone(LPCSTR) { return nullptr; }
BOOL WINAPI SystemWrite(HDC dc, int format, const PIXELFORMATDESCRIPTOR*) {
    ++systemWrites;
    Check(!drawables.InstallPixelFormat(dc, 1) && GetLastError() == ERROR_BUSY, "source and target Set share original mutation guard");
    SetLastError(1234);
    return format == 3;
}
int WINAPI SystemRead(HDC) { ++systemReads; return 9; }
ResourceManagerGpuPolicy::GpuShimPolicy TargetPolicy() {
    ResourceManagerGpuPolicy::GpuShimPolicy value;
    value.mode = ResourceManagerGpuPolicy::GpuShimPolicyMode::TargetLuid;
    value.targetLuid = {1, 0}; return value;
}
int main() {
    SetErrorMode(32771);
    try {
        const auto foreground = GetForegroundWindow();
        WNDCLASSW wc{}; wc.style = CS_OWNDC; wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr); wc.lpszClassName = L"RmNewWindowPreparationProbe";
        Check(RegisterClassW(&wc), "own class");
        const HWND window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, wc.lpszClassName,
            L"", WS_POPUP, 0, 0, 16, 16, nullptr, nullptr, wc.hInstance, nullptr);
        const HDC dc = GetDC(window); Check(window && dc && !IsWindowVisible(window), "own hidden window");
        IcdExports driver;
#define FIELD(name) driver.name = &Unused<decltype(driver.name)>::Call
        FIELD(validateVersion); FIELD(setCallbacks); FIELD(describePixelFormat); FIELD(createLayerContext);
        FIELD(setContext); FIELD(releaseContext); FIELD(deleteContext); FIELD(shareLists); FIELD(copyContext); FIELD(swapBuffers);
#undef FIELD
        driver.getProcAddress = &LookupNone; driver.setPixelFormat = &Install;
        const auto target = DrawableTarget::Create(wc.hInstance, driver, {1,0}, {wc.hInstance,nullptr}, 1);
        Check(target != nullptr, "adapter description has no window format state");
        PIXELFORMATDESCRIPTOR p{}; p.nSize = sizeof(p); p.nVersion = 1;
        p.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
        const auto directory = PixelFormatDirectory::Create({p}, 1);
        const auto dispatch = WindowDispatch::Create(wc.hInstance, {});
        Check(directory && dispatch, "explicit immutable format fixture");
        std::shared_ptr<const DrawableSnapshot> view = std::make_shared<DrawableSnapshot>(
            DrawableSnapshot{nullptr, target, directory, dispatch, 0, false});
        std::atomic_store(&unformattedWindowFormats, view);
        creationPolicyReader.store(&TargetPolicy); systemPixelFormat = &NoSystemFormat;
        pixelFormatSourceCallers.modules[0] = GetModuleHandleW(L"kernel32.dll"); pixelFormatSourceCallers.count = 1;
        Check(drawables.Count() == 0 && contexts.Count() == 0, "no initial state");
        for (unsigned i=0; i<8; ++i) {
            const auto selection = SelectCreation(dc);
            Check(!selection.error && selection.target && !selection.drawable.record, "query borrows only adapter formats");
            Check(ReadPixelFormat(dc) == 0, "query returns no installed format");
            PIXELFORMATDESCRIPTOR output{};
            Check(DescribePixelFormats(dc, 1, sizeof(output), &output) == 1, "query describes prepared format");
        }
        Check(drawables.Count() == 0 && contexts.Count() == 0 && sets == 0 && unexpected == 0,
            "queries do not publish create or call driver");
        DWORD foreignError{}; BOOL foreignResult{};
        std::thread other([&] { foreignResult = WritePixelFormat(dc, 1, &p); foreignError = GetLastError(); });
        other.join();
        Check(!foreignResult && foreignError == ERROR_INVALID_THREAD_ID && drawables.Count() == 0 && sets == 0,
            "foreign window owner rejected without publication");
        Check(WritePixelFormat(dc, 1, &p) && sets == 1, "explicit Set publishes and installs once");
        const auto actual = drawables.Read(dc);
        Check(actual && actual.sourcePixelFormat == 0 && actual.installedPixelFormat == 1,
            "original store retains true source and installed target format");
        Check(WritePixelFormat(dc, 1, &p) && sets == 1, "same format repeats without native reset");
        Check(!WritePixelFormat(dc, 2, &p) && sets == 1, "different invalid format never reaches driver");
        Check(!drawables.PublishTarget(actual.record, target, -1), "negative source format publication rejected");
        Check(drawables.InstallSystemPixelFormat(dc, 3, &p, &SystemWrite, &SystemRead)
            && GetLastError() == 1234 && systemWrites == 1 && systemReads == 1, "successful system Set records actual native value");
        const auto changed = drawables.Read(dc);
        Check(changed.sourcePixelFormat == 9 && changed.target == actual.target
            && changed.installedPixelFormat == 1, "one original window fact changes without replacing adapter");
        Check(!drawables.InstallSystemPixelFormat(dc, 2, &p, &SystemWrite, &SystemRead)
            && GetLastError() == 1234 && systemWrites == 2 && systemReads == 1
            && drawables.Read(dc).sourcePixelFormat == 9, "failed system Set does not read or alter current value");
        Check(ReleaseDC(window, dc) && DestroyWindow(window), "normal own window destruction");
        Check(drawables.Count() == 0 && actual.record && !actual.record->active.load(), "terminal observers retire original record");
        Check(RemoveWindowObserversForCurrentThread() && UnregisterClassW(wc.lpszClassName, wc.hInstance), "observer and class cleanup");
        Check(contexts.Count() == 0 && unexpected == 0 && GetForegroundWindow() == foreground, "no context or unrelated native work");
        FILETIME b{},e{},k{},u{}; BOOL job{};
        Check(GetProcessTimes(GetCurrentProcess(), &b,&e,&k,&u) && IsProcessInJob(GetCurrentProcess(), nullptr, &job) && job, "native identity and Job");
        const auto birth=(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime;
        std::printf("{\"passed\":true,\"checks\":%u,\"sets\":%u,\"unexpected\":%u,\"remainingRecords\":0,\"remainingContexts\":0,\"pid\":%lu,\"creationFileTime\":%llu,\"errorMode\":%u,\"inJob\":true,\"foregroundUnchanged\":true}\n",
            checks,sets,unexpected,GetCurrentProcessId(),birth,GetErrorMode());
        return 0;
    } catch(const std::exception& e) { std::fprintf(stderr,"%s\n",e.what()); return 1; }
}
