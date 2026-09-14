#include <windows.h>
#include <cstdio>
#include <stdexcept>
#include <thread>

namespace Fault {
bool failDuplicate{}, failWait{};
unsigned captured{}, closed{};
HANDLE latest{};
BOOL WINAPI Duplicate(HANDLE a,HANDLE b,HANDLE c,LPHANDLE d,DWORD access,BOOL inherit,DWORD options)
{
    if(failDuplicate){SetLastError(ERROR_NOT_ENOUGH_MEMORY);return FALSE;}
    const BOOL result=DuplicateHandle(a,b,c,d,access,inherit,options);
    if(result){++captured;latest=*d;}
    return result;
}
DWORD WINAPI Wait(HANDLE h,DWORD milliseconds)
{
    if(failWait){SetLastError(ERROR_INVALID_HANDLE);return WAIT_FAILED;}
    return WaitForSingleObject(h,milliseconds);
}
BOOL WINAPI Close(HANDLE h)
{
    const BOOL result=CloseHandle(h);
    if(result)++closed;
    return result;
}
}
#define DuplicateHandle Fault::Duplicate
#define WaitForSingleObject Fault::Wait
#define CloseHandle Fault::Close
#include "../GpuPlacementShim/OpenGlContextDispatch.h"
#undef CloseHandle
#undef WaitForSingleObject
#undef DuplicateHandle

using namespace ResourceManagerOpenGl;
namespace {
unsigned checks{}, deletes{};
bool failDelete{};
void Check(bool value,const char* name)
{
    ++checks;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n",name,value?"true":"false");
    std::fflush(stdout);
    if(!value)throw std::runtime_error(name);
}
DHGLRC APIENTRY Create(HDC,int){return 1;}
BOOL APIENTRY Delete(DHGLRC){++deletes;SetLastError(ERROR_RETRY);return !failDelete;}
struct Worker {
    ThreadOwner* owner;
    HANDLE ready,finish;
    DWORD id{};
    unsigned long long birth{};
    bool captured{};
};
DWORD WINAPI Execute(void* input)
{
    auto& w=*static_cast<Worker*>(input);
    FILETIME b{},e{},k{},u{};
    if(!GetThreadTimes(GetCurrentThread(),&b,&e,&k,&u))return 1;
    w.birth=(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime;
    w.id=GetCurrentThreadId();w.captured=w.owner->CaptureCurrent();
    SetEvent(w.ready);
    return WaitForSingleObject(w.finish,5000)==WAIT_OBJECT_0&&w.captured?0:2;
}
void EndedOwner(ThreadOwner& owner)
{
    Worker w{&owner,CreateEventW(nullptr,TRUE,FALSE,nullptr),CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    Check(w.ready&&w.finish,"owner-worker-events");
    DWORD id{};HANDLE thread=CreateThread(nullptr,0,&Execute,&w,0,&id);
    Check(thread&&WaitForSingleObject(w.ready,5000)==WAIT_OBJECT_0&&w.captured,"owner-worker-captured");
    Check(owner.Id()==id&&!owner.CanBind()&&GetLastError()==ERROR_BUSY,"live-other-bind-blocked");
    Check(!owner.CanDeleteUnbound()&&GetLastError()==ERROR_BUSY,"live-other-delete-blocked");
    SetEvent(w.finish);
    Check(WaitForSingleObject(thread,5000)==WAIT_OBJECT_0,"owner-worker-ended");
    DWORD code=259;FILETIME b{},e{},k{},u{};
    Check(GetExitCodeThread(thread,&code)&&code==0&&GetThreadTimes(thread,&b,&e,&k,&u),"owner-worker-exit-identity");
    const auto birth=(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime;
    Check(birth==w.birth&&id==w.id,"owner-exact-thread-object");
    std::printf("{\"workerId\":%lu,\"creationFileTime\":%llu,\"exitCode\":%lu}\n",id,birth,code);
    CloseHandle(thread);CloseHandle(w.ready);CloseHandle(w.finish);
    SetLastError(1236);
    Check(owner.CanBind()&&owner.CanDeleteUnbound()&&owner.Id()==id&&GetLastError()==1236,"ended-owner-admits-without-clearing");
}
}
int main()
{
    SetErrorMode(32771);
    try {
        ThreadOwner a;
        Check(!a.Id()&&a.CanBind()&&a.CanDeleteUnbound(),"empty-owner");
        SetLastError(1236);
        Check(a.CaptureCurrent()&&a.Id()==GetCurrentThreadId()&&GetLastError()==1236,"capture-current-preserves-error");
        HANDLE original=Fault::latest;
        DWORD flags{};
        Check(GetHandleInformation(original,&flags)&&!(flags&HANDLE_FLAG_INHERIT),"real-handle-not-inherited");
        Check(a.CanBind()&&!a.CanDeleteUnbound()&&GetLastError()==ERROR_BUSY,"current-bound-not-unbound");
        Fault::failDuplicate=true;
        Check(!a.CaptureCurrent()&&GetLastError()==ERROR_NOT_ENOUGH_MEMORY&&a.Id()==GetCurrentThreadId()&&WaitForSingleObject(original,0)==WAIT_TIMEOUT,"capture-failure-retains-owner");
        Fault::failDuplicate=false;Fault::failWait=true;
        Check(!a.CanBind()&&GetLastError()==ERROR_INVALID_HANDLE,"wait-failure-not-death");
        Check(!a.CanDeleteUnbound()&&GetLastError()==ERROR_INVALID_HANDLE,"delete-wait-failure-not-death");
        Fault::failWait=false;
        ThreadOwner b(std::move(a));
        Check(!a.Id()&&b.Id()==GetCurrentThreadId(),"move-construct-transfers-once");
        ThreadOwner c;Check(c.CaptureCurrent(),"prepare-replacement-owner");
        const HANDLE replaced=Fault::latest;
        c=std::move(b);
        Check(!b.Id()&&c.Id()==GetCurrentThreadId()&&WaitForSingleObject(replaced,0)==WAIT_FAILED&&GetLastError()==ERROR_INVALID_HANDLE,"move-assignment-closes-old-handle");
        auto& self=c;c=std::move(self);
        Check(c.Id()==GetCurrentThreadId()&&c.CanBind(),"self-move-retained");
        SetLastError(1236);c.Reset();
        Check(!c.Id()&&GetLastError()==1236,"reset-preserves-error");
        Check(WaitForSingleObject(original,0)==WAIT_FAILED&&GetLastError()==ERROR_INVALID_HANDLE,"reset-closes-native-handle");
        {
            ThreadOwner local;Check(local.CaptureCurrent(),"destructor-owner");
            original=Fault::latest;
        }
        Check(WaitForSingleObject(original,0)==WAIT_FAILED&&GetLastError()==ERROR_INVALID_HANDLE,"destructor-only-closes-handle");
        for(unsigned i=0;i<128;++i){ThreadOwner local;if(!local.CaptureCurrent())throw std::runtime_error("repeated-capture");}
        Check(Fault::captured==Fault::closed,"repeated-owners-no-handle-leak");
        ContextStore store;IcdExports driver;driver.createLayerContext=&Create;driver.deleteContext=&Delete;
        auto record=store.Create(GetModuleHandleW(nullptr),driver,nullptr,0);
        Check(record!=nullptr,"ended-record-created");
        EndedOwner(record->owner);
        const DWORD oldId=record->owner.Id();original=Fault::latest;
        failDelete=true;
        Check(!store.DestroyUnbound(record)&&GetLastError()==ERROR_RETRY&&record->owner.Id()==oldId&&record->token.load()==1&&store.Count()==1&&WaitForSingleObject(original,0)==WAIT_OBJECT_0,"failed-native-delete-preserves-ended-owner");
        failDelete=false;
        Check(store.DestroyUnbound(record)&&GetLastError()==ERROR_RETRY&&!record->owner.Id()&&!record->token.load()&&!store.Count(),"successful-native-delete-clears-owner");
        Check(WaitForSingleObject(original,0)==WAIT_FAILED&&GetLastError()==ERROR_INVALID_HANDLE,"native-delete-closes-owner-handle");
        Check(deletes==2&&Fault::captured==Fault::closed,"all-native-handle-calls-balanced");
        std::printf("{\"passed\":true,\"checks\":%u,\"captured\":%u,\"closed\":%u,\"deleteCalls\":%u,\"gpuCreated\":false}\n",checks,Fault::captured,Fault::closed,deletes);
        return 0;
    } catch(const std::exception& error){
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n",checks,error.what());return 1;
    }
}
