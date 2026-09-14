#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlDrawableStore.h"
#include <cstdio>
#include <stdexcept>
#include <type_traits>

namespace {
using namespace ResourceManagerOpenGl;
DrawableStore store;
unsigned checks{},nativeCalls{},unexpectedCalls{};
HDC expectedDc{};
BOOL nativeResult{};
DWORD nativeError=1357;
LONG expectedFormat=1;
bool exactFormat{},quietNative{},leaveError{};
DWORD observedIncoming{};
bool callbackRead{},callbackReentry{},blockNative{};
HANDLE entered{},releaseNative{};
BOOL workerResult{};
DWORD workerError{};
void Check(bool value,const char* label) {
    ++checks;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n",label,value?"true":"false");
    std::fflush(stdout);
    if(!value)throw std::runtime_error(label);
}
template<typename T> struct Unused;
template<typename R,typename... Args> struct Unused<R(WINAPI*)(Args...)> {
    static R WINAPI Invoke(Args...) {++unexpectedCalls;if constexpr(!std::is_void_v<R>)return R{};}
};
BOOL WINAPI SetFormat(HDC dc,LONG format) {
    observedIncoming=GetLastError();exactFormat=format==expectedFormat;
    ++nativeCalls;
    if(quietNative){if(!leaveError)SetLastError(nativeError);return nativeResult;}
    callbackRead=dc==expectedDc&&store.Read(dc).installedPixelFormat==0;
    callbackReentry=!store.InstallPixelFormat(dc,format)&&GetLastError()==ERROR_BUSY;
    if(blockNative) {
        if(!SetEvent(entered)||WaitForSingleObject(releaseNative,10000)!=WAIT_OBJECT_0) {
            SetLastError(ERROR_TIMEOUT);return FALSE;
        }
    }
    SetLastError(nativeError);return nativeResult;
}
IcdExports Driver() {
    IcdExports d{};
#define FIELD(name) d.name=&Unused<decltype(d.name)>::Invoke
    FIELD(validateVersion);FIELD(setCallbacks);FIELD(describePixelFormat);FIELD(getProcAddress);
    FIELD(createLayerContext);FIELD(setContext);FIELD(releaseContext);FIELD(deleteContext);
    FIELD(shareLists);FIELD(copyContext);FIELD(swapBuffers);
#undef FIELD
    d.setPixelFormat=&SetFormat;return d;
}
LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM w,LPARAM l) {
    if(message!=WM_NCDESTROY)return DefWindowProcW(window,message,w,l);
    if(!store.BeforeNcDestroy(window))++unexpectedCalls;
    const LRESULT result=DefWindowProcW(window,message,w,l);
    if(!store.AfterNcDestroy(window))++unexpectedCalls;
    return result;
}
DWORD WINAPI InstallWorker(void*) {
    workerResult=store.InstallPixelFormat(expectedDc,1);workerError=GetLastError();return 0;
}
}
int main() {
    SetErrorMode(32771);
    HWND windows[3]{};HDC dcs[3]{};HANDLE worker{};
    const HINSTANCE module=GetModuleHandleW(nullptr);
    const wchar_t* name=L"RmExplicitPixelFormatInstallProbe";
    try {
        FILETIME b{},e{},k{},u{};BOOL inJob{};
        Check(GetProcessTimes(GetCurrentProcess(),&b,&e,&k,&u)&&IsProcessInJob(GetCurrentProcess(),nullptr,&inJob)&&inJob,"bounded-native-process");
        std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"errorMode\":%u,\"inJob\":true}\n",GetCurrentProcessId(),(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime,GetErrorMode());
        WNDCLASSW wc{};wc.style=CS_OWNDC;wc.hInstance=module;wc.lpfnWndProc=Procedure;wc.lpszClassName=name;
        Check(RegisterClassW(&wc)!=0,"hidden-fixture-class");
        const auto driver=Driver();
        const auto target=DrawableTarget::Create(module,driver,LUID{1,0},{module,nullptr},1);
        std::vector<PIXELFORMATDESCRIPTOR> formats(5);
        for(auto& f:formats){f.nSize=sizeof(f);f.nVersion=1;f.dwFlags=PFD_DRAW_TO_WINDOW|PFD_SUPPORT_OPENGL;}
        formats[2].dwFlags=PFD_SUPPORT_OPENGL;
        formats[1].dwFlags=PFD_DRAW_TO_WINDOW;
        formats[3].dwFlags=PFD_DRAW_TO_WINDOW|PFD_SUPPORT_OPENGL|PFD_GENERIC_FORMAT;
        formats[4].dwFlags|=PFD_GENERIC_FORMAT;
        const auto directory=PixelFormatDirectory::Create(std::move(formats),4);
        Check(target&&directory,"explicit-native-and-generic-directory");
        for(unsigned i=0;i<3;++i) {
            windows[i]=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,name,L"",WS_POPUP,0,0,16,16,nullptr,nullptr,module,nullptr);
            dcs[i]=GetDC(windows[i]);
            Check(windows[i]&&dcs[i]&&!IsWindowVisible(windows[i]),"real-hidden-window");
            Check(!store.InstallPixelFormat(dcs[i],1)&&GetLastError()==ERROR_INVALID_WINDOW_HANDLE&&store.Count()==i,"install-does-not-enroll");
            auto record=store.PrepareOnWindowThread(windows[i]);
            Check(record&&store.Read(dcs[i]).installedPixelFormat==0,"initial-value-is-uninstalled");
            Check(!store.InstallPixelFormat(dcs[i],1)&&GetLastError()==ERROR_NOT_READY,"unprepared-target-rejected");
            Check(store.PublishTarget(record,target,1),"publish-target");
            Check(!store.InstallPixelFormat(dcs[i],1)&&GetLastError()==ERROR_NOT_READY,"unprepared-directory-rejected");
            Check(store.PublishDirectory(record,directory),"publish-directory");
        }
        Check(!nativeCalls&&!unexpectedCalls,"preparation-and-read-never-install");
        expectedDc=dcs[0];
        for(int format:{0,-1,3,4,5,6,INT_MAX})
            Check(!store.InstallPixelFormat(expectedDc,format)&&GetLastError()==ERROR_INVALID_PIXEL_FORMAT&&!nativeCalls,"invalid-or-non-native-window-format-no-driver-call");
        nativeResult=FALSE;SetLastError(8765);
        Check(!store.InstallPixelFormat(expectedDc,1)&&GetLastError()==1357&&nativeCalls==1&&exactFormat&&observedIncoming==8765,"native-failure-exact-request-and-error-preserved");
        Check(callbackRead&&callbackReentry&&store.Read(expectedDc).installedPixelFormat==0&&nativeCalls==1,"failed-install-read-reentry-no-retry");
        quietNative=true;leaveError=true;SetLastError(9876);
        Check(!store.InstallPixelFormat(expectedDc,1)&&GetLastError()==9876&&observedIncoming==9876&&nativeCalls==2&&exactFormat,"native-false-untouched-error-preserved");
        leaveError=false;nativeError=0;
        Check(!store.InstallPixelFormat(expectedDc,1)&&GetLastError()==0&&nativeCalls==3&&store.Read(expectedDc).installedPixelFormat==0,"native-false-zero-error-not-invented");
        quietNative=false;nativeResult=7;nativeError=2468;expectedFormat=2;
        Check(store.InstallPixelFormat(expectedDc,2)==7&&GetLastError()==2468&&nativeCalls==4&&exactFormat,"private-icd-format-without-public-opengl-bit-installed-exactly");
        Check(callbackRead&&callbackReentry&&store.Read(expectedDc).installedPixelFormat==2,"publish-only-after-native-success");
        SetLastError(4321);
        Check(store.InstallPixelFormat(expectedDc,2)==TRUE&&GetLastError()==4321&&nativeCalls==4,"same-format-idempotent-no-driver-call");
        Check(!store.InstallPixelFormat(expectedDc,1)&&GetLastError()==ERROR_INVALID_PIXEL_FORMAT&&nativeCalls==4&&store.Read(expectedDc).installedPixelFormat==2,"different-format-cannot-reinstall");
        expectedDc=dcs[1];expectedFormat=1;blockNative=true;nativeResult=TRUE;
        entered=CreateEventW(nullptr,TRUE,FALSE,nullptr);releaseNative=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        Check(entered&&releaseNative,"explicit-concurrency-events");
        worker=CreateThread(nullptr,0,InstallWorker,nullptr,0,nullptr);
        Check(worker&&WaitForSingleObject(entered,10000)==WAIT_OBJECT_0,"render-thread-enters-native-call");
        Check(store.Read(expectedDc).installedPixelFormat==0,"concurrent-read-returns-uninstalled-without-wait");
        Check(!store.InstallPixelFormat(expectedDc,1)&&GetLastError()==ERROR_BUSY,"concurrent-same-request-rejected-without-wait");
        Check(!store.InstallPixelFormat(expectedDc,2)&&GetLastError()==ERROR_BUSY,"concurrent-different-request-rejected-without-wait");
        Check(SetEvent(releaseNative)&&WaitForSingleObject(worker,10000)==WAIT_OBJECT_0,"render-thread-natural-exit");
        DWORD exit{};Check(GetExitCodeThread(worker,&exit)&&exit==0&&workerResult&&workerError==2468&&callbackRead&&callbackReentry&&nativeCalls==5&&exactFormat,"render-thread-result-and-reentry");
        Check(store.Read(expectedDc).installedPixelFormat==1,"render-thread-published-to-original-store");
        Check(CloseHandle(worker)&&CloseHandle(entered)&&CloseHandle(releaseNative),"thread-and-events-closed");worker=entered=releaseNative=nullptr;
        for(unsigned i=0;i<3;++i) {
            const auto record=store.Find(dcs[i]);
            Check(ReleaseDC(windows[i],dcs[i])&&DestroyWindow(windows[i]),"natural-window-destroy");windows[i]=nullptr;
            Check(record&&!record->active.load()&&!store.Read(dcs[i]),"original-record-retired");
            Check(!store.InstallPixelFormat(dcs[i],1)&&GetLastError()==ERROR_INVALID_WINDOW_HANDLE&&nativeCalls==5,"retired-window-no-driver-call");
        }
        Check(store.Count()==0&&!unexpectedCalls&&UnregisterClassW(name,module),"all-original-records-and-class-cleaned");
        std::printf("{\"passed\":true,\"checks\":%u,\"nativeSetCalls\":%u,\"remainingRecords\":0,\"gpuCreated\":false,\"driverSubstituted\":true,\"publicRoutesConnected\":false}\n",checks,nativeCalls);
        return 0;
    } catch(const std::exception& error) {
        if(releaseNative)SetEvent(releaseNative);
        if(worker){WaitForSingleObject(worker,10000);CloseHandle(worker);}
        if(entered)CloseHandle(entered);
        if(releaseNative)CloseHandle(releaseNative);
        for(unsigned i=0;i<3;++i)if(windows[i]){if(dcs[i])ReleaseDC(windows[i],dcs[i]);DestroyWindow(windows[i]);}
        UnregisterClassW(name,module);
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n",checks,error.what());return 1;
    }
}
