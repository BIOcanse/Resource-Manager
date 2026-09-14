#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlWindowThread.h"
#include <cstdio>
#include <cstdlib>
#include <new>

static bool rejectAllocations{};
static unsigned rejected{};
void* operator new(std::size_t size) {
    if(rejectAllocations){++rejected;throw std::bad_alloc();}
    if(void* p=std::malloc(size?size:1))return p;
    throw std::bad_alloc();
}
void operator delete(void* p) noexcept {std::free(p);}
void operator delete(void* p,std::size_t) noexcept {std::free(p);}
namespace {
using namespace ResourceManagerOpenGl::Runtime;
ResourceManagerOpenGl::DrawableReference retained;
HDC dc{};
bool revoked{},reentryRejected{},readEmpty{};
LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM w,LPARAM l) {
    if(message==WM_NCDESTROY) {
        revoked=retained&&!retained->active.load();
        readEmpty=!drawables.Read(dc);
        reentryRejected=!PrepareObservedDrawable(window);
    }
    return DefWindowProcW(window,message,w,l);
}
}
int main() {
    SetErrorMode(32771);
    FILETIME b{},e{},k{},u{};BOOL job{};
    if(!GetProcessTimes(GetCurrentProcess(),&b,&e,&k,&u)||!IsProcessInJob(GetCurrentProcess(),nullptr,&job)||!job)return 2;
    std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"errorMode\":32771,\"inJob\":true}\n",GetCurrentProcessId(),(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime);
    const HWND foreground=GetForegroundWindow();
    const auto instance=GetModuleHandleW(nullptr);
    WNDCLASSW c{};c.lpfnWndProc=Procedure;c.hInstance=instance;c.lpszClassName=L"RmWindowAllocation20260910";c.style=CS_OWNDC;
    if(!RegisterClassW(&c))return 2;
    HWND window=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,c.lpszClassName,L"Allocation fixture",WS_POPUP,0,0,16,16,nullptr,nullptr,instance,nullptr);
    if(!window||(dc=GetDC(window))==nullptr||(retained=PrepareObservedDrawable(window))==nullptr)return 2;
    rejectAllocations=true;
    const bool destroyed=DestroyWindow(window)!=FALSE;
    rejectAllocations=false;
    const auto remaining=drawables.Count();
    SetLastError(0);
    const bool removed=RemoveWindowObserversForCurrentThread()!=FALSE;
    const DWORD removalError=GetLastError();
    const bool noHooks=!WindowThreadDetail::observers.before&&!WindowThreadDetail::observers.after;
    const bool clean=UnregisterClassW(c.lpszClassName,instance)&&GetForegroundWindow()==foreground;
    const bool outcome=(removed&&rejected==0)||(!removed&&rejected>0&&removalError==ERROR_NOT_ENOUGH_MEMORY);
    const bool passed=destroyed&&revoked&&readEmpty&&reentryRejected&&remaining==0&&noHooks&&clean&&outcome;
    std::printf("{\"passed\":%s,\"destroyed\":%s,\"revoked\":%s,\"readEmpty\":%s,\"reentryRejected\":%s,\"rejectedAllocations\":%u,\"remainingRecords\":%zu,\"removed\":%s,\"removalError\":%lu,\"noHooks\":%s,\"clean\":%s}\n",passed?"true":"false",destroyed?"true":"false",revoked?"true":"false",readEmpty?"true":"false",reentryRejected?"true":"false",rejected,remaining,removed?"true":"false",removalError,noHooks?"true":"false",clean?"true":"false");
    return passed?0:1;
}
