#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <atomic>
#include <cstdio>
#include <stdexcept>

namespace {
constexpr wchar_t ClassName[]=L"RmWindowMessageProbe20260909";
constexpr wchar_t Property[]=L"ResourceManager.WindowMessageProbe.20260909";
constexpr UINT VerifyMessage=WM_APP+37;
unsigned checks{};
struct Record { unsigned index; std::atomic<bool> ended{}; };
Record records[2]{{0},{1}};
std::atomic<unsigned> notifications{},windowTerminals{},ordinaryMessages{},wrongThread{},propertyErrors{};
DWORD windowThread{};
HWND parentWindow{},childWindow{},unrelatedWindow{};
HANDLE ready{},start{},destroyed{},finish{};
unsigned long long workerBirth{};
void Check(bool value,const char* name)
{
    ++checks;std::printf("{\"check\":\"%s\",\"passed\":%s}\n",name,value?"true":"false");std::fflush(stdout);
    if(!value)throw std::runtime_error(name);
}
LRESULT CALLBACK Monitor(int code,WPARAM w,LPARAM l)
{
    const DWORD error=GetLastError();
    if(code>=0){
        const auto& message=*reinterpret_cast<const CWPSTRUCT*>(l);
        if(message.message==WM_NCDESTROY){
            auto* record=static_cast<Record*>(GetPropW(message.hwnd,Property));
            if(record==&records[0]||record==&records[1]){
                if(GetCurrentThreadId()!=windowThread)++wrongThread;
                const HANDLE old=RemovePropW(message.hwnd,Property);
                if(old!=record||GetPropW(message.hwnd,Property))++propertyErrors;
                record->ended=true;++notifications;
                std::printf("{\"terminalHook\":%u,\"threadId\":%lu,\"propertyRemoved\":%s}\n",record->index,GetCurrentThreadId(),old==record?"true":"false");std::fflush(stdout);
            }
        }
    }
    SetLastError(error);
    return CallNextHookEx(nullptr,code,w,l);
}
LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM w,LPARAM l)
{
    if(message==VerifyMessage){++ordinaryMessages;return static_cast<LRESULT>(w)+static_cast<LRESULT>(l);}
    if(message==WM_NCDESTROY&&(window==parentWindow||window==childWindow)){
        Record& record=window==parentWindow?records[0]:records[1];
        if(!record.ended||GetPropW(window,Property))++propertyErrors;
        ++windowTerminals;
        std::printf("{\"terminalWindowProcedure\":%u,\"hookAlreadyRetired\":%s,\"propertyAbsent\":%s}\n",record.index,record.ended?"true":"false",GetPropW(window,Property)?"false":"true");std::fflush(stdout);
    }
    return DefWindowProcW(window,message,w,l);
}
DWORD WINAPI Worker(void*)
{
    FILETIME b{},e{},k{},u{};
    if(!GetThreadTimes(GetCurrentThread(),&b,&e,&k,&u))return 1;
    workerBirth=(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime;windowThread=GetCurrentThreadId();
    WNDCLASSW cls{};cls.lpfnWndProc=Procedure;cls.hInstance=GetModuleHandleW(nullptr);cls.lpszClassName=ClassName;
    if(!RegisterClassW(&cls))return 2;
    parentWindow=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,ClassName,L"Owned parent",WS_POPUP,0,0,32,32,nullptr,nullptr,cls.hInstance,nullptr);
    childWindow=CreateWindowExW(WS_EX_NOACTIVATE,ClassName,L"Owned child",WS_CHILD,0,0,16,16,parentWindow,nullptr,cls.hInstance,nullptr);
    unrelatedWindow=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,ClassName,L"Owned unregistered control",WS_POPUP,0,0,16,16,nullptr,nullptr,cls.hInstance,nullptr);
    if(!parentWindow||!childWindow||!unrelatedWindow)return 3;
    if(!SetPropW(parentWindow,Property,&records[0])||!SetPropW(childWindow,Property,&records[1]))return 4;
    SetEvent(ready);
    if(WaitForSingleObject(start,5000)!=WAIT_OBJECT_0)return 5;
    const bool routed=SendMessageW(parentWindow,VerifyMessage,17,23)==40&&SendMessageW(childWindow,VerifyMessage,17,23)==40&&SendMessageW(unrelatedWindow,VerifyMessage,17,23)==40;
    const bool untouched=!records[0].ended&&!records[1].ended&&GetPropW(parentWindow,Property)==&records[0]&&GetPropW(childWindow,Property)==&records[1];
    std::printf("{\"ordinaryBeforeDestroy\":true,\"messagesRouted\":%s,\"recordsUntouched\":%s}\n",routed?"true":"false",untouched?"true":"false");std::fflush(stdout);
    if(!routed||!untouched)return 6;
    if(!DestroyWindow(unrelatedWindow)||!DestroyWindow(parentWindow))return 7;
    if(IsWindow(childWindow)||!UnregisterClassW(ClassName,cls.hInstance))return 8;
    SetEvent(destroyed);
    if(WaitForSingleObject(finish,5000)!=WAIT_OBJECT_0)return 9;
    return 0;
}
}
int main()
{
    SetErrorMode(32771);
    try {
        FILETIME b{},e{},k{},u{};BOOL job{};
        Check(GetProcessTimes(GetCurrentProcess(),&b,&e,&k,&u)&&IsProcessInJob(GetCurrentProcess(),nullptr,&job)&&job,"bounded-process");
        const auto birth=(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime;
        std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"threadId\":%lu,\"errorMode\":32771,\"inJob\":true}\n",GetCurrentProcessId(),birth,GetCurrentThreadId());
        const HWND foreground=GetForegroundWindow();
        ready=CreateEventW(nullptr,TRUE,FALSE,nullptr);start=CreateEventW(nullptr,TRUE,FALSE,nullptr);destroyed=CreateEventW(nullptr,TRUE,FALSE,nullptr);finish=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        Check(ready&&start&&destroyed&&finish,"owned-events");
        DWORD id{};HANDLE thread=CreateThread(nullptr,0,Worker,nullptr,0,&id);
        Check(thread&&WaitForSingleObject(ready,5000)==WAIT_OBJECT_0,"preexisting-other-thread-windows");
        Check(id==windowThread&&id!=GetCurrentThreadId()&&!IsWindowVisible(parentWindow)&&!IsWindowVisible(childWindow)&&!IsWindowVisible(unrelatedWindow),"hidden-other-thread");
        Check(GetThreadTimes(thread,&b,&e,&k,&u),"worker-times");
        const auto exact=(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime;
        Check(exact==workerBirth,"worker-creation-bound");
        const auto oldProcedure=GetWindowLongPtrW(parentWindow,GWLP_WNDPROC);
        HHOOK hook=SetWindowsHookExW(WH_CALLWNDPROC,Monitor,nullptr,id);
        Check(hook!=nullptr,"install-own-process-other-thread-hook");
        Check(GetWindowLongPtrW(parentWindow,GWLP_WNDPROC)==oldProcedure,"no-window-procedure-replacement");
        SetEvent(start);
        Check(WaitForSingleObject(destroyed,5000)==WAIT_OBJECT_0,"both-windows-naturally-destroyed");
        Check(notifications==2&&windowTerminals==2&&ordinaryMessages==3&&!wrongThread&&!propertyErrors,"terminal-before-window-procedure");
        const BOOL unhooked=UnhookWindowsHookEx(hook);const DWORD unhookError=GetLastError();
        Check(unhooked,"explicit-hook-removal");
        SetEvent(finish);
        Check(WaitForSingleObject(thread,5000)==WAIT_OBJECT_0,"worker-naturally-ended");
        DWORD code=259;Check(GetExitCodeThread(thread,&code)&&code==0,"worker-zero-exit");
        std::printf("{\"workerId\":%lu,\"creationFileTime\":%llu,\"exitCode\":%lu,\"unhookReturned\":%s,\"unhookError\":%lu}\n",id,exact,code,unhooked?"true":"false",unhookError);
        CloseHandle(thread);CloseHandle(ready);CloseHandle(start);CloseHandle(destroyed);CloseHandle(finish);
        Check(GetForegroundWindow()==foreground,"foreground-unchanged");
        std::printf("{\"passed\":true,\"checks\":%u,\"terminalHooks\":%u,\"windowTerminals\":%u,\"ordinaryMessages\":%u,\"wrongThread\":%u,\"propertyErrors\":%u,\"gpuCreated\":false,\"globalHook\":false,\"productDeployed\":false}\n",checks,notifications.load(),windowTerminals.load(),ordinaryMessages.load(),wrongThread.load(),propertyErrors.load());
        return 0;
    } catch(const std::exception& error){std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n",checks,error.what());return 1;}
}
