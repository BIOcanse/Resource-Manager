#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlWindowCall.h"
#include "../GpuPlacementShim/OpenGlWindowThread.h"
#include <cstdio>
#include <stdexcept>

namespace {
using namespace ResourceManagerOpenGl::Runtime;
constexpr wchar_t ClassName[]=L"RmWindowCall20260911";
unsigned checks{},cases{};
void Check(bool yes,const char* name) {
    ++checks;std::printf("{\"check\":\"%s\",\"passed\":%s}\n",name,yes?"true":"false");std::fflush(stdout);
    if(!yes)throw std::runtime_error(name);
}
struct State {
    HANDLE ready{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    HANDLE entered{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    HANDLE release{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    std::atomic<HWND> window{};
    std::atomic<DWORD> owner{};
    std::atomic<unsigned> calls{};
    std::atomic<bool> blockProcedure{};
    ResourceManagerOpenGl::DrawableReference record;
    ~State(){CloseHandle(ready);CloseHandle(entered);CloseHandle(release);}
};
LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM w,LPARAM l) {
    if(message==WM_NCCREATE)SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams));
    auto* state=reinterpret_cast<State*>(GetWindowLongPtrW(window,GWLP_USERDATA));
    if(state&&(message==WM_APP+1||(message>=0xC000&&state->blockProcedure.load()))) {
        SetEvent(state->entered);
        WaitForSingleObject(state->release,5000);
    }
    if(message==WM_CLOSE){DestroyWindow(window);PostQuitMessage(0);return 0;}
    return DefWindowProcW(window,message,w,l);
}
DWORD WINAPI WindowThread(void* parameter) {
    const auto state=*static_cast<std::shared_ptr<State>*>(parameter);
    state->owner=GetCurrentThreadId();
    const HWND window=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,ClassName,L"Window call",WS_POPUP,0,0,16,16,nullptr,nullptr,GetModuleHandleW(nullptr),state.get());
    state->window=window;SetEvent(state->ready);
    if(!window)return 1;
    MSG message{};
    while(GetMessageW(&message,nullptr,0,0)>0){TranslateMessage(&message);DispatchMessageW(&message);}
    if(IsWindow(window))DestroyWindow(window);
    if(!RemoveWindowObserversForCurrentThread())return 2;
    return drawables.CountForThread(GetCurrentThreadId())==0?0:3;
}
struct Sender {
    HWND window{};
    WindowWork work;
    DWORD timeout{40};
    WindowCallResult result{};
};
DWORD WINAPI Send(void* parameter) {
    auto& sender=*static_cast<Sender*>(parameter);
    sender.result=ExecuteOnWindowThread(sender.window,sender.work,sender.timeout);
    return 0;
}
void Join(HANDLE thread,const char* role) {
    FILETIME b{},e{},k{},u{};DWORD exit=259;
    Check(thread&&GetThreadTimes(thread,&b,&e,&k,&u),"native-thread-identity");
    const auto birth=(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime;
    Check(WaitForSingleObject(thread,5000)==WAIT_OBJECT_0&&GetExitCodeThread(thread,&exit)&&exit==0,"native-thread-normal-exit");
    std::printf("{\"threadRole\":\"%s\",\"threadId\":%lu,\"creationFileTime\":%llu,\"exitCode\":%lu}\n",role,GetThreadId(thread),birth,exit);
    CloseHandle(thread);
}
void Result(const WindowCallResult& r,const char* name) {
    ++cases;
    std::printf("{\"case\":\"%s\",\"state\":%u,\"returned\":%s,\"operationError\":%lu,\"dispatchError\":%lu,\"cleanupError\":%lu}\n",name,static_cast<unsigned>(r.state),r.returned?"true":"false",r.operationError,r.dispatchError,r.cleanupError);
}
bool SlotEmpty(){std::lock_guard<std::mutex> lock(WindowCallDetail::slotLock);return !WindowCallDetail::active;}
}
int main() {
    SetErrorMode(32771);
    try {
        FILETIME b{},e{},k{},u{};BOOL job{};
        Check(GetProcessTimes(GetCurrentProcess(),&b,&e,&k,&u)&&IsProcessInJob(GetCurrentProcess(),nullptr,&job)&&job,"bounded-native-process");
        std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"errorMode\":32771,\"inJob\":true}\n",GetCurrentProcessId(),(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime);
        const HWND foreground=GetForegroundWindow();
        WNDCLASSW cls{};cls.lpfnWndProc=Procedure;cls.hInstance=GetModuleHandleW(nullptr);cls.lpszClassName=ClassName;cls.style=CS_OWNDC;
        Check(RegisterClassW(&cls)!=0,"register-hidden-fixture-class");
        auto state=std::make_shared<State>();
        Check(state->ready&&state->entered&&state->release,"owned-events");
        HANDLE windowThread=CreateThread(nullptr,0,WindowThread,&state,0,nullptr);
        Check(windowThread&&WaitForSingleObject(state->ready,5000)==WAIT_OBJECT_0&&state->window&&!IsWindowVisible(state->window),"real-other-window-thread");
        const HWND window=state->window;
        WindowWork prepare=[state](HWND target){
            if(GetCurrentThreadId()!=state->owner||target!=state->window){SetLastError(ERROR_INVALID_THREAD_ID);return FALSE;}
            state->calls++;
            state->record=PrepareObservedDrawable(target);
            const auto nested=ExecuteOnWindowThread(target,[](HWND){return TRUE;},40);
            if(!state->record||nested.state!=WindowCallState::NotStarted||nested.dispatchError!=ERROR_BUSY){SetLastError(ERROR_INVALID_DATA);return FALSE;}
            SetLastError(1234);return TRUE;
        };
        SetLastError(876);
        auto result=ExecuteOnWindowThread(window,prepare,1000);Result(result,"prepare");
        Check(result.state==WindowCallState::Completed&&result.returned&&result.operationError==1234&&!result.dispatchError&&!result.cleanupError&&GetLastError()==876,"actual-owner-preparation-and-reentrant-busy");
        Check(state->calls==1&&state->record&&state->record->active.load()&&SlotEmpty(),"record-only-in-original-store");
        const auto record=state->record;
        result=ExecuteOnWindowThread(window,prepare,1000);Result(result,"idempotent");
        Check(result.returned&&state->record==record&&state->calls==2&&drawables.Count()==1&&SlotEmpty(),"same-record-after-second-explicit-call");
        result=ExecuteOnWindowThread(window,[](HWND){SetLastError(55);return FALSE;},1000);Result(result,"operation-error");
        Check(result.state==WindowCallState::Completed&&!result.returned&&result.operationError==55&&!result.cleanupError&&SlotEmpty(),"operation-error-not-transport-error");
        result=ExecuteOnWindowThread(window,[](HWND)->BOOL{throw std::bad_alloc();},1000);Result(result,"exception");
        Check(result.state==WindowCallState::Completed&&!result.returned&&result.operationError==ERROR_NOT_ENOUGH_MEMORY&&SlotEmpty(),"callback-exception-completes-request");

        ResetEvent(state->entered);ResetEvent(state->release);
        Check(PostMessageW(window,WM_APP+1,0,0)&&WaitForSingleObject(state->entered,5000)==WAIT_OBJECT_0,"owner-blocked-before-dispatch");
        result=ExecuteOnWindowThread(window,[state](HWND){state->calls++;return TRUE;},40);Result(result,"not-started-timeout");
        Check(result.state==WindowCallState::NotStarted&&result.dispatchError&&!result.returned&&!result.cleanupError&&state->calls==2&&SlotEmpty(),"unclaimed-timeout-cancels-without-work");
        WPARAM retiredToken{};{std::lock_guard<std::mutex> lock(WindowCallDetail::slotLock);retiredToken=WindowCallDetail::nextToken-1;}
        SetEvent(state->release);
        DWORD_PTR ignored{};
        Check(SendMessageTimeoutW(window,RegisterWindowMessageW(L"ResourceManager.OpenGl.WindowCall.9AA339C3-DF60-4C19-8DC9-C85F2B826F20"),retiredToken,0,SMTO_BLOCK,1000,&ignored)!=0&&state->calls==2,"retired-message-does-not-run-work");

        ResetEvent(state->entered);ResetEvent(state->release);
        Sender slow{window,[state](HWND){
            state->calls++;SetEvent(state->entered);
            if(WaitForSingleObject(state->release,5000)!=WAIT_OBJECT_0){SetLastError(ERROR_TIMEOUT);return FALSE;}
            SetLastError(678);return TRUE;
        }};
        HANDLE sender=CreateThread(nullptr,0,Send,&slow,0,nullptr);
        Check(sender&&WaitForSingleObject(state->entered,5000)==WAIT_OBJECT_0,"work-entered-on-owner");
        Check(WaitForSingleObject(sender,160)==WAIT_TIMEOUT,"sender-retains-running-work-after-message-timeout");
        SetEvent(state->release);Join(sender,"slow-sender");Result(slow.result,"started-timeout");
        Check(slow.result.state==WindowCallState::Completed&&slow.result.returned&&slow.result.operationError==678&&slow.result.dispatchError&&!slow.result.cleanupError&&state->calls==3&&SlotEmpty(),"actual-completed-work-and-dispatch-timeout-both-retained");

        ResetEvent(state->entered);ResetEvent(state->release);state->blockProcedure=true;
        Sender after{window,[](HWND){SetLastError(987);return TRUE;}};
        sender=CreateThread(nullptr,0,Send,&after,0,nullptr);
        Check(sender&&WaitForSingleObject(state->entered,5000)==WAIT_OBJECT_0,"window-procedure-blocked-after-work");
        Join(sender,"after-sender");Result(after.result,"completed-before-procedure-timeout");
        Check(after.result.state==WindowCallState::Completed&&after.result.returned&&after.result.operationError==987&&after.result.dispatchError&&!after.result.cleanupError&&SlotEmpty(),"window-procedure-result-not-used-as-operation-result");
        state->blockProcedure=false;SetEvent(state->release);

        HWND own=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,ClassName,L"Direct owner",WS_POPUP,0,0,16,16,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        const DWORD ownThread=GetCurrentThreadId();
        result=ExecuteOnWindowThread(own,[ownThread](HWND target){return GetCurrentThreadId()==ownThread&&PrepareObservedDrawable(target)?TRUE:FALSE;},40);Result(result,"same-thread");
        Check(result.state==WindowCallState::Completed&&result.returned&&!result.dispatchError&&!result.cleanupError&&SlotEmpty(),"same-thread-direct-operation");
        Check(DestroyWindow(own)&&RemoveWindowObserversForCurrentThread(),"direct-owner-cleanup");
        result=ExecuteOnWindowThread(nullptr,prepare,40);Result(result,"invalid-window");
        Check(result.state==WindowCallState::NotStarted&&result.dispatchError==ERROR_INVALID_WINDOW_HANDLE&&SlotEmpty(),"invalid-window-no-dispatch");
        result=ExecuteOnWindowThread(window,prepare,0);Result(result,"invalid-timeout");
        Check(result.state==WindowCallState::NotStarted&&result.dispatchError==ERROR_INVALID_PARAMETER&&SlotEmpty(),"explicit-finite-timeout-required");
        Check(PostMessageW(window,WM_CLOSE,0,0)!=FALSE,"request-normal-window-close");
        Join(windowThread,"window-owner");
        Check(!record->active.load()&&drawables.Count()==0&&SlotEmpty(),"window-record-and-request-normally-retired");
        Check(UnregisterClassW(ClassName,GetModuleHandleW(nullptr))&&GetForegroundWindow()==foreground,"class-cleanup-foreground-unchanged");
        std::printf("{\"passed\":true,\"checks\":%u,\"cases\":%u,\"remainingRecords\":%zu,\"requestEmpty\":true,\"gpuCreated\":false,\"thirdPartyInjection\":false}\n",checks,cases,drawables.Count());
        return 0;
    }catch(const std::exception& error){std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n",checks,error.what());return 1;}
}
