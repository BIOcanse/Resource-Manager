#include "../GpuPlacementExternal/D3D12ResultObservation.h"

int main() {
    SetErrorMode(32771);
    (void)set;
    try {
        const ExternalDxgi::Entries entries{11,12,13,14,15,16,17};
        const std::array<uint64_t,4> enumeration{21,22,23,24};
        unsigned cases=0;
        for(unsigned scenario=0;scenario<12;++scenario) {
            ExternalDxgi::D3D12ResultObservation observer(GetCurrentProcess(),entries,42);
            uint64_t deviceTable[44]{},chainTable[23]{};
            deviceTable[8]=entries.createQueue; deviceTable[43]=entries.adapterLuid;
            chainTable[8]=entries.present; chainTable[22]=entries.present1;
            chainTable[2]=entries.releaseSwapChain;
            uint64_t deviceObject=reinterpret_cast<uint64_t>(deviceTable),chainObject=reinterpret_cast<uint64_t>(chainTable);
            uint64_t device=reinterpret_cast<uint64_t>(&deviceObject),chain=reinterpret_cast<uint64_t>(&chainObject),queue=1234;
            uint64_t stack[9]{}; stack[0]=1000; stack[7]=reinterpret_cast<uint64_t>(&chain);
            CONTEXT context{};
            auto enter=[&](uint64_t address,uint64_t object,uint64_t output) {
                context={}; context.Rip=address; context.Rsp=reinterpret_cast<uint64_t>(stack);
                context.Rcx=object; context.R9=output;
                return observer.hit(7,context);
            };
            auto leave=[&](uint64_t result) {
                context.Rip=stack[0]; context.Rsp=reinterpret_cast<uint64_t>(stack)+8; context.Rax=result;
                require(observer.hit(7,context),"observed return");
            };
            require(enter(entries.createDevice,0,scenario==1?0:reinterpret_cast<uint64_t>(&device)),"creation entry");
            if(scenario==1) {
                require(!observer.verified()&&observer.threadTable(7,observer.table(enumeration))[3]==entries.createDevice,"null-output probe rejected");
                ++cases; continue;
            }
            leave(scenario==2?static_cast<uint32_t>(E_FAIL):(scenario==9?S_FALSE:S_OK));
            if(scenario==2||scenario==9) { require(!observer.verified(),"failed or validation-only device rejected"); ++cases; continue; }
            require(!observer.verified(),"created device alone rejected");
            LUID luid{scenario==3?43u:42u,0};
            context={}; context.Rip=entries.adapterLuid; context.Rsp=reinterpret_cast<uint64_t>(stack);
            context.Rcx=device; context.Rdx=reinterpret_cast<uint64_t>(&luid);
            if(scenario!=7) { require(observer.hit(7,context),"LUID query"); leave(reinterpret_cast<uint64_t>(&luid)); }
            require(enter(entries.createQueue,device,reinterpret_cast<uint64_t>(&queue)),"queue entry");
            if(scenario==0) {
                const auto outer=context;
                uint64_t nestedStack[1]{2000};
                context.Rip=entries.adapterLuid; context.Rsp=reinterpret_cast<uint64_t>(nestedStack); context.Rdx=reinterpret_cast<uint64_t>(&luid);
                require(observer.hit(7,context),"nested LUID query");
                require(observer.threadTable(7,observer.table(enumeration))[3]==2000,"nested return stays thread-local");
                require(observer.threadTable(8,observer.table(enumeration))[3]==0,"other thread inherits no return");
                context.Rip=2000; context.Rsp+=8; context.Rax=reinterpret_cast<uint64_t>(&luid);
                require(observer.hit(7,context),"nested return");
                require(observer.threadTable(7,observer.table(enumeration))[3]==1000,"outer return retained");
                context=outer;
            }
            leave(S_OK);
            context={}; context.Rip=entries.createSwapChain; context.Rsp=reinterpret_cast<uint64_t>(stack); context.Rdx=scenario==4?9999:queue;
            require(observer.hit(7,context),"chain entry");
            if(scenario==4) { require(!observer.verified(),"unrelated queue rejected"); ++cases; continue; }
            if(scenario!=7) {
                const auto outer=context;
                uint64_t nestedStack[1]{3000};
                context={}; context.Rip=entries.adapterLuid; context.Rsp=reinterpret_cast<uint64_t>(nestedStack);
                context.Rcx=device; context.Rdx=reinterpret_cast<uint64_t>(&luid);
                require(observer.hit(7,context),"LUID inside swapchain creation");
                context.Rip=3000; context.Rsp+=8; context.Rax=reinterpret_cast<uint64_t>(&luid);
                require(observer.hit(7,context),"swapchain LUID return");
                context=outer;
            }
            leave(S_OK);
            if(scenario>=10) {
                require(enter(entries.releaseSwapChain,chain,0),"chain release entry");
                leave(scenario==10?0:1);
            }
            for(unsigned frame=0;frame<30;++frame) {
                context={}; context.Rip=entries.present; context.Rsp=reinterpret_cast<uint64_t>(stack);
                context.Rcx=scenario==5?chain+8:chain; context.R8=scenario==6?DXGI_PRESENT_TEST:0;
                require(observer.hit(7,context),"present entry");
                if(scenario!=5&&scenario!=6&&scenario!=10) leave(scenario==8?DXGI_STATUS_OCCLUDED:S_OK);
                if(frame<29) require(!observer.verified(),"insufficient target presents rejected");
            }
            require(observer.verified()==(scenario==0||scenario==11),"exact device/queue/chain/LUID/lifetime/present outcome");
            ++cases;
        }
        printf("{\"passed\":true,\"cases\":%u,\"syntheticCallContexts\":true}\n",cases);
        return 0;
    } catch(const std::exception& error) { fprintf(stderr,"%s\n",error.what()); return 1; }
}
