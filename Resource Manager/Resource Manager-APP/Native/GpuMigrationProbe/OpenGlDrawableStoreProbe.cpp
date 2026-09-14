#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlDrawableStore.h"
#include <cstdio>
#include <stdexcept>

namespace {
using namespace ResourceManagerOpenGl;
DrawableStore store;
constexpr wchar_t Property[]=L"ResourceManager.OpenGl.Drawable.37A00D99-80B4-494A-B218-CE82330590CA";
constexpr wchar_t Names[][32]={L"RmDrawablePrivate20260909",L"RmDrawableCommon20260909",L"RmDrawableClass20260909"};
HWND windows[7]{};
unsigned stages[7]{};
unsigned checks{},errors{},beforeCalls{},procedureCalls{},afterCalls{};
DWORD windowThread{};
DrawableReference retained;
void Check(bool value,const char* name) {
    ++checks;std::printf("{\"check\":\"%s\",\"passed\":%s}\n",name,value?"true":"false");std::fflush(stdout);
    if(!value)throw std::runtime_error(name);
}
int Index(HWND window) {for(int i=0;i<7;++i)if(windows[i]==window)return i;return -1;}
LRESULT CALLBACK Before(int code,WPARAM w,LPARAM l) {
    if(code>=0){
        const auto& message=*reinterpret_cast<const CWPSTRUCT*>(l);
        const int index=Index(message.hwnd);
        if(message.message==WM_NCDESTROY&&index>=0){
            SetLastError(1234);
            const bool passed=stages[index]==0&&GetCurrentThreadId()==windowThread&&store.BeforeNcDestroy(message.hwnd)&&GetLastError()==1234&&!GetPropW(message.hwnd,Property);
            if(!passed)++errors;
            stages[index]=1;++beforeCalls;
            std::printf("{\"terminal\":%d,\"stage\":\"before\",\"passed\":%s}\n",index,passed?"true":"false");
        }
    }
    return CallNextHookEx(nullptr,code,w,l);
}
LRESULT CALLBACK After(int code,WPARAM w,LPARAM l) {
    if(code>=0){
        const auto& message=*reinterpret_cast<const CWPRETSTRUCT*>(l);
        const int index=Index(message.hwnd);
        if(message.message==WM_NCDESTROY&&index>=0){
            SetLastError(1234);
            const bool passed=stages[index]==2&&GetCurrentThreadId()==windowThread&&store.AfterNcDestroy(message.hwnd)&&GetLastError()==1234;
            if(!passed)++errors;
            stages[index]=3;++afterCalls;
            std::printf("{\"terminal\":%d,\"stage\":\"after\",\"passed\":%s}\n",index,passed?"true":"false");
        }
    }
    return CallNextHookEx(nullptr,code,w,l);
}
LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM w,LPARAM l) {
    if(message==WM_APP+37)return static_cast<LRESULT>(w+l);
    const int index=Index(window);
    if(message==WM_NCDESTROY&&index>=0){
        const auto count=store.Count();
        SetLastError(0);
        const auto again=store.PrepareOnWindowThread(window);
        const bool passed=stages[index]==1&&!again&&GetLastError()==ERROR_INVALID_WINDOW_HANDLE&&store.Count()==count&&!GetPropW(window,Property)&&(index!=0||!retained->active.load());
        if(!passed)++errors;
        stages[index]=2;++procedureCalls;
        std::printf("{\"terminal\":%d,\"stage\":\"procedure\",\"reentryRejected\":%s}\n",index,passed?"true":"false");
    }
    return DefWindowProcW(window,message,w,l);
}
DWORD WINAPI WrongThread(void*) {
    SetLastError(0);
    const auto value=store.PrepareOnWindowThread(windows[0]);
    const DWORD error=GetLastError();
    std::printf("{\"wrongThreadPrepare\":true,\"threadId\":%lu,\"error\":%lu,\"returnedNull\":%s}\n",GetCurrentThreadId(),error,value?"false":"true");
    return !value&&error==ERROR_INVALID_THREAD_ID?0:1;
}
}
int main() {
    SetErrorMode(32771);
    try {
        FILETIME b{},e{},k{},u{};BOOL job{};
        Check(GetProcessTimes(GetCurrentProcess(),&b,&e,&k,&u)&&IsProcessInJob(GetCurrentProcess(),nullptr,&job)&&job,"bounded-native-process");
        const auto birth=(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime;
        windowThread=GetCurrentThreadId();
        std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"threadId\":%lu,\"errorMode\":32771,\"inJob\":true}\n",GetCurrentProcessId(),birth,windowThread);
        const HWND foreground=GetForegroundWindow();
        const HINSTANCE instance=GetModuleHandleW(nullptr);
        for(int i=0;i<3;++i){
            WNDCLASSW cls{};cls.lpfnWndProc=Procedure;cls.hInstance=instance;cls.lpszClassName=Names[i];cls.style=i==0?CS_OWNDC:(i==2?CS_CLASSDC:0);
            Check(RegisterClassW(&cls)!=0,"register-owned-class");
        }
        HHOOK before=SetWindowsHookExW(WH_CALLWNDPROC,Before,nullptr,windowThread);
        HHOOK after=SetWindowsHookExW(WH_CALLWNDPROCRET,After,nullptr,windowThread);
        Check(before&&after,"install-both-terminal-observers");
        for(int i=0;i<7;++i){
            const int cls=i==0?0:(i==2||i==3?2:1);
            windows[i]=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,Names[cls],L"Owned drawable fixture",WS_POPUP,0,0,16,16,nullptr,nullptr,instance,nullptr);
            Check(windows[i]&&!IsWindowVisible(windows[i]),"create-hidden-window");
        }
        HDC privateDc=GetDC(windows[0]),commonDc=GetDC(windows[1]);
        Check(privateDc&&commonDc,"real-private-common-dcs");
        SetLastError(1234);
        Check(!store.Find(privateDc)&&GetLastError()==1234&&store.Count()==0,"lookup-does-not-enroll");
        retained=store.PrepareOnWindowThread(windows[0]);
        Check(retained&&retained->active.load()&&retained->window==windows[0]&&GetLastError()==1234,"explicit-owner-preparation");
        Check(store.PrepareOnWindowThread(windows[0])==retained&&store.Count()==1,"idempotent-window-record");
        std::weak_ptr<DrawableRecord> weak=retained;
        auto* raw=retained.get();retained.reset();
        Check(!weak.expired()&&store.Count()==1,"window-retains-with-zero-contexts");
        retained=store.Find(privateDc);
        Check(retained&&retained.get()==raw,"same-record-after-zero-contexts");
        DWORD workerId{};HANDLE worker=CreateThread(nullptr,0,WrongThread,nullptr,0,&workerId);
        Check(worker&&GetThreadTimes(worker,&b,&e,&k,&u),"real-other-thread-identity");
        const auto workerBirth=(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime;
        Check(WaitForSingleObject(worker,5000)==WAIT_OBJECT_0,"wrong-thread-returned");
        DWORD exit=259;Check(GetExitCodeThread(worker,&exit)&&exit==0&&store.Count()==1,"wrong-thread-no-mutation");
        std::printf("{\"workerId\":%lu,\"creationFileTime\":%llu,\"exitCode\":%lu}\n",workerId,workerBirth,exit);CloseHandle(worker);
        Check(ReleaseDC(windows[0],privateDc)==1&&store.Find(privateDc)==retained,"private-release-retains-window-association");
        auto common=store.PrepareOnWindowThread(windows[1]);
        Check(common&&store.Find(commonDc)==common,"common-dc-record");
        Check(ReleaseDC(windows[1],commonDc)==1&&!store.Find(commonDc),"common-release-clears-association");
        HDC reacquired=GetDC(windows[1]);
        Check(reacquired&&store.Find(reacquired)==common,"reacquired-dc-resolves-current-window");
        auto classA=store.PrepareOnWindowThread(windows[2]),classB=store.PrepareOnWindowThread(windows[3]);
        HDC dcA=GetDC(windows[2]);
        Check(classA&&classB&&dcA&&store.Find(dcA)==classA,"class-dc-first-window");
        HDC dcB=GetDC(windows[3]);
        Check(dcB&&dcA==dcB&&store.Find(dcA)==classB&&store.Find(dcB)==classB,"same-class-dc-now-second-window");
        auto draw=store.Find(privateDc),read=store.Find(reacquired);
        Check(draw==retained&&read==common&&draw!=read,"distinct-draw-read-references");
        HDC memory=CreateCompatibleDC(nullptr);
        const auto count=store.Count();
        Check(memory&&!store.Find(memory)&&!store.Find(nullptr)&&store.Count()==count,"memory-null-lookup-no-enrollment");
        Check(DeleteDC(memory)!=0,"delete-owned-memory-dc");
        int sentinel{};
        Check(SetPropW(windows[4],Property,&sentinel)!=0,"install-fixture-conflict");
        SetLastError(0);
        Check(!store.PrepareOnWindowThread(windows[4])&&GetLastError()==ERROR_ALREADY_EXISTS&&store.Count()==count&&GetPropW(windows[4],Property)==&sentinel,"conflict-preserved-not-overwritten");
        Check(RemovePropW(windows[4],Property)==&sentinel,"remove-only-fixture-property");
        HDC unknown=GetDC(windows[5]);
        Check(unknown&&!store.Find(unknown)&&store.Count()==count,"unknown-window-lookup-no-enrollment");
        Check(ReleaseDC(windows[5],unknown)==1&&ReleaseDC(windows[1],reacquired)==1&&ReleaseDC(windows[2],dcA)==1&&ReleaseDC(windows[3],dcB)==1,"release-owned-common-class-dcs");
        Check(SendMessageW(windows[0],WM_APP+37,17,23)==40&&store.Count()==count,"ordinary-message-unchanged");
        for(int i=0;i<7;++i){
            Check(DestroyWindow(windows[i])&&stages[i]==3,"native-terminal-before-procedure-after");
        }
        Check(!errors&&beforeCalls==7&&procedureCalls==7&&afterCalls==7&&store.Count()==0,"all-terminal-records-retired");
        Check(!retained->active.load()&&!common->active.load()&&!classA->active.load()&&!classB->active.load()&&!store.Find(privateDc),"borrowed-references-retired-not-dereferenced-as-windows");
        draw.reset();retained.reset();
        Check(weak.expired(),"record-freed-after-last-operation-reference");
        Check(UnhookWindowsHookEx(before)&&UnhookWindowsHookEx(after),"explicit-unhook-on-owner-thread-after-callbacks");
        for(int i=0;i<3;++i)Check(UnregisterClassW(Names[i],instance)!=0,"unregister-owned-class");
        Check(GetForegroundWindow()==foreground,"foreground-unchanged");
        std::printf("{\"passed\":true,\"checks\":%u,\"terminalBefore\":%u,\"terminalProcedure\":%u,\"terminalAfter\":%u,\"errors\":%u,\"remainingRecords\":%zu,\"gpuCreated\":false,\"threadMessageHooksInstalled\":true,\"detourHooksInstalled\":false,\"globalHook\":false,\"productDeployed\":false}\n",checks,beforeCalls,procedureCalls,afterCalls,errors,store.Count());
        return 0;
    }catch(const std::exception& error){std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n",checks,error.what());return 1;}
}
