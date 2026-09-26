#define main RetainedWindowCallCases
#include "OpenGlWindowCallProbe.cpp"
#undef main
static std::weak_ptr<ResourceManagerOpenGl::Runtime::WindowCallDetail::Request> exitedRequest;
static DWORD WINAPI ExitWindowThread(void* parameter) {
    auto* state=static_cast<State*>(parameter);
    state->owner=GetCurrentThreadId();
    state->window=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,ClassName,L"Exit fixture",WS_POPUP,0,0,16,16,nullptr,nullptr,GetModuleHandleW(nullptr),state);
    SetEvent(state->ready);
    if(!state->window)return 1;
    MSG message{};
    while(GetMessageW(&message,nullptr,0,0)>0){TranslateMessage(&message);DispatchMessageW(&message);}
    return 0;
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
        HANDLE owner=CreateThread(nullptr,0,ExitWindowThread,state.get(),0,nullptr);
        Check(owner&&WaitForSingleObject(state->ready,5000)==WAIT_OBJECT_0&&state->window,"owner-created");
        const HWND window=state->window;
        const auto result=ExecuteOnWindowThread(window,[](HWND)->BOOL{
            {std::lock_guard<std::mutex> lock(WindowCallDetail::slotLock);exitedRequest=WindowCallDetail::active;}
            ExitThread(0);
        },40);
        Result(result,"owner-exits-inside-operation");
        Join(owner,"window-owner");
        Check(result.state==WindowCallState::ThreadExited&&!result.returned&&!IsWindow(window)&&drawables.Count()==0,"thread-exit-is-not-operation-completion");
        const bool retained=!SlotEmpty();
        const auto owners=exitedRequest.use_count();
        std::printf("{\"requestOwnersAfterThreadExit\":%ld,\"expectedRetainedOwner\":%u}\n",owners,retained?1U:0U);
        Check(owners==(retained?1:0),"no-abandoned-callback-request-reference");
        Check(retained==(result.cleanupError!=0),"real-unhook-failure-retains-request");
        if(retained){
            std::lock_guard<std::mutex> lock(WindowCallDetail::slotLock);
            Check(WindowCallDetail::active->hook!=nullptr,"failed-unhook-handle-not-forgotten");
        }
        Check(UnregisterClassW(ClassName,GetModuleHandleW(nullptr))&&GetForegroundWindow()==foreground,"class-cleanup-foreground-unchanged");
        std::printf("{\"passed\":true,\"checks\":%u,\"ownerExited\":true,\"operationCompleted\":false,\"cleanupError\":%lu,\"requestRetained\":%s,\"remainingRecords\":%zu,\"gpuCreated\":false,\"thirdPartyInjection\":false}\n",checks,result.cleanupError,retained?"true":"false",drawables.Count());
        return 0;
    }catch(const std::exception& error){std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n",checks,error.what());return 1;}
}
