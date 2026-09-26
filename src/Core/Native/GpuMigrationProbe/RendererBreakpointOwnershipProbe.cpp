#include "../GpuPlacementExternal/Controller.h"

static DWORD WINAPI Finish(void*) { return 0; }
__attribute__((noinline)) static void EntryA() { __asm__ volatile("nop"); }
__attribute__((noinline)) static void EntryB() { __asm__ volatile("nop; nop"); }

int main() {
    SetErrorMode(32771);
    unsigned checks=0; bool passed=true;
    for(unsigned scenario=0;scenario<3;++scenario) {
        Handle thread(CreateThread(nullptr,0,Finish,nullptr,CREATE_SUSPENDED,nullptr));
        CONTEXT original{}; original.ContextFlags=CONTEXT_ALL;
        bool captured=thread.h&&GetThreadContext(thread.h,&original);
        try {
            require(captured,"fixture suspended thread context");
            Thread owner; owner.handle=thread.h;
            const auto a=reinterpret_cast<uint64_t>(&EntryA),b=reinterpret_cast<uint64_t>(&EntryB);
            require(a!=b,"distinct fixture entries");
            arm(owner,{a,0,0,0},true);
            CONTEXT trapped{}; trapped.ContextFlags=CONTEXT_ALL;
            require(GetThreadContext(thread.h,&trapped),"fixture armed context");
            trapped.Rip=a; trapped.Dr6=scenario==1?0:1;
            if(scenario==2) trapped.Dr6|=1ULL<<14;
            // Windows sanitizes writes to DR6; classify explicit synthetic event evidence
            // separately from the real suspended-thread rearm/readback below.
            rememberOwnedTrap(owner,trapped);
            arm(owner,{b,0,0,0},true);
            require(owner.pendingTrapAddress==(scenario==0?a:0),"preserve only owned hardware trap"); ++checks;
            require(owner.installedEntries==std::array<uint64_t,4>{b,0,0,0},"installed ownership updated"); ++checks;
            CONTEXT current{}; current.ContextFlags=CONTEXT_ALL;
            require(GetThreadContext(thread.h,&current)&&current.Dr0==b&&current.Rip==original.Rip,"rearm preserves pending instruction"); ++checks;
            require(owner.original.Dr0==original.Dr0&&owner.original.Dr7==original.Dr7,"original debug state retained"); ++checks;
        } catch(const std::exception& error) { fprintf(stderr,"%s\n",error.what()); passed=false; }
        if(!captured||!SetThreadContext(thread.h,&original)) {
            fprintf(stderr,"Fixture restore failed; thread remains suspended\n"); return 1;
        }
        if(ResumeThread(thread.h)!=1||WaitForSingleObject(thread.h,5000)!=WAIT_OBJECT_0) return 1;
        DWORD code=STILL_ACTIVE;
        if(!GetExitCodeThread(thread.h,&code)||code!=0) return 1;
    }
    printf("{\"passed\":%s,\"checks\":%u,\"cases\":3,\"realSuspendedThreadContexts\":true,\"syntheticTrapClassification\":true,\"queuedDebuggerEventReproduced\":false}\n",passed?"true":"false",checks);
    return passed?0:1;
}
