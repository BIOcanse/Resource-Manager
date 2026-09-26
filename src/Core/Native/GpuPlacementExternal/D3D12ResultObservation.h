#pragma once
#include "DxgiEntryDiscovery.h"

namespace ExternalDxgi {
// Observes only naturally executed calls. A probe device without its own queue,
// swapchain and successful presentation never becomes a verified destination.
class D3D12ResultObservation {
    enum class Phase { Creation, Binding, Presentation };
    enum class Call { Device, Adapter, Queue, SwapChain, Present, Release };
    struct Pending { Call call; uint64_t stack, address, output; };
    HANDLE process;
    Entries entries;
    uint64_t target, device=0, queue=0, chain=0, observedLuid=0;
    bool haveLuid=false, chainReleased=false;
    unsigned presentations=0;
    Phase phase=Phase::Creation;
    std::map<DWORD,std::vector<Pending>> calls;

    template<class T> T read(uint64_t address) const {
        T value{}; SIZE_T bytes=0;
        require(address&&ReadProcessMemory(process,reinterpret_cast<void*>(address),&value,sizeof value,&bytes)
            &&bytes==sizeof value,"D3D12 natural return data");
        return value;
    }
    void enter(DWORD tid,const CONTEXT& context,Call call,uint64_t output) {
        auto& stack=calls[tid]; require(stack.size()<16,"D3D12 observation call depth");
        const auto address=read<uint64_t>(context.Rsp);
        stack.push_back({call,context.Rsp,address,output});
    }
    void leave(DWORD tid,const Pending& pending,const CONTEXT& context) {
        if(pending.call==Call::Adapter) {
            require(context.Rax==pending.output,"D3D12 LUID aggregate return");
            const auto value=luidBits(read<LUID>(pending.output));
            bool insideCreation=false;
            for(const auto& call:calls.at(tid)) if(call.call==Call::SwapChain) insideCreation=true;
            if(insideCreation) {
                require(!haveLuid||observedLuid==value,"D3D12 swapchain device LUID changed");
                observedLuid=value; haveLuid=true;
            }
            printf("{\"kind\":\"observedDeviceLuid\",\"device\":%llu,\"luid\":%llu}\n",
                static_cast<unsigned long long>(device),static_cast<unsigned long long>(value));
            return;
        }
        if(pending.call==Call::Release) {
            if(static_cast<uint32_t>(context.Rax)==0) chainReleased=true;
            printf("{\"kind\":\"observedSwapChainRelease\",\"swapChain\":%llu,\"references\":%u,\"lifetimeEnded\":%s}\n",
                static_cast<unsigned long long>(chain),static_cast<uint32_t>(context.Rax),chainReleased?"true":"false");
            return;
        }
        if(static_cast<HRESULT>(context.Rax)!=S_OK) return;
        if(pending.call==Call::Present) {
            if(!chainReleased&&haveLuid&&observedLuid==target) {
                ++presentations;
                if(presentations==1||presentations==30)
                    printf("{\"kind\":\"observedTargetPresentation\",\"swapChain\":%llu,\"count\":%u}\n",
                        static_cast<unsigned long long>(chain),presentations);
            }
            return;
        }
        const auto pointer=read<uint64_t>(pending.output);
        if(!pointer) return;
        if(pending.call==Call::Device && !device) {
            const auto table=read<uint64_t>(pointer);
            require(read<uint64_t>(table+8*8)==entries.createQueue&&read<uint64_t>(table+43*8)==entries.adapterLuid,
                "D3D12 returned device entry identity");
            device=pointer; phase=Phase::Binding;
            printf("{\"kind\":\"observedD3D12Device\",\"device\":%llu}\n",static_cast<unsigned long long>(device));
        } else if(pending.call==Call::Queue && !queue) {
            queue=pointer;
            printf("{\"kind\":\"observedD3D12Queue\",\"device\":%llu,\"queue\":%llu}\n",
                static_cast<unsigned long long>(device),static_cast<unsigned long long>(queue));
        } else if(pending.call==Call::SwapChain && !chain) {
            const auto table=read<uint64_t>(pointer);
            require(read<uint64_t>(table+8*8)==entries.present&&read<uint64_t>(table+22*8)==entries.present1
                &&read<uint64_t>(table+2*8)==entries.releaseSwapChain,
                "D3D12 returned swapchain entry identity");
            chain=pointer; phase=Phase::Presentation;
            printf("{\"kind\":\"observedD3D12SwapChain\",\"queue\":%llu,\"swapChain\":%llu}\n",
                static_cast<unsigned long long>(queue),static_cast<unsigned long long>(chain));
        }
    }
public:
    D3D12ResultObservation(HANDLE handle,const Entries& discovered,uint64_t targetLuid)
        :process(handle),entries(discovered),target(targetLuid) {}

    std::array<uint64_t,4> table(const std::array<uint64_t,4>& enumeration) const {
        if(phase==Phase::Creation) return {enumeration[0],enumeration[1],enumeration[3],entries.createDevice};
        if(phase==Phase::Binding) return {entries.adapterLuid,entries.createQueue,entries.createSwapChain,0};
        return {entries.releaseSwapChain,entries.present,entries.present1,0};
    }
    std::array<uint64_t,4> threadTable(DWORD tid,const std::array<uint64_t,4>& common) const {
        auto result=common;
        const auto it=calls.find(tid);
        if(it!=calls.end()&&!it->second.empty()) result[3]=it->second.back().address;
        return result;
    }
    // Returns true for an observation call, false for an enumeration entry.
    bool hit(DWORD tid,const CONTEXT& context) {
        auto it=calls.find(tid);
        if(it!=calls.end()&&!it->second.empty()) {
            const auto pending=it->second.back();
            if(context.Rip==pending.address&&context.Rsp==pending.stack+8) {
                it->second.pop_back(); leave(tid,pending,context); return true;
            }
        }
        if(context.Rip==entries.createDevice&&phase==Phase::Creation) {
            // A null output pointer is a feature-level probe, not a device.
            if(context.R9) enter(tid,context,Call::Device,context.R9);
            return true;
        }
        if(context.Rip==entries.adapterLuid) {
            if(context.Rcx==device) enter(tid,context,Call::Adapter,context.Rdx);
            return true;
        }
        if(context.Rip==entries.createQueue) {
            if(context.Rcx==device&&!queue) enter(tid,context,Call::Queue,context.R9);
            return true;
        }
        if(context.Rip==entries.createSwapChain) {
            if(queue&&context.Rdx==queue&&!chain) {
                haveLuid=false;
                enter(tid,context,Call::SwapChain,read<uint64_t>(context.Rsp+56));
            }
            return true;
        }
        if(context.Rip==entries.releaseSwapChain) {
            if(chain&&context.Rcx==chain&&!chainReleased) enter(tid,context,Call::Release,0);
            return true;
        }
        if(context.Rip==entries.present||context.Rip==entries.present1) {
            if(chain&&!chainReleased&&context.Rcx==chain&&!(context.R8&DXGI_PRESENT_TEST)) enter(tid,context,Call::Present,0);
            return true;
        }
        // An unrelated nested call may reach the same return instruction.
        if(it!=calls.end()&&!it->second.empty()&&context.Rip==it->second.back().address) return true;
        return false;
    }
    void threadExited(DWORD tid) { calls.erase(tid); }
    bool verified() const { return device&&queue&&chain&&!chainReleased&&haveLuid&&observedLuid==target&&presentations>=30; }
    void report() const {
        printf("{\"kind\":\"d3d12Observation\",\"device\":%llu,\"queue\":%llu,\"swapChain\":%llu,\"luidObservedInsideSwapChainCreation\":%s,\"luid\":%llu,\"targetPresents\":%u,\"swapChainLifetimeEnded\":%s,\"targetPresentationVerified\":%s}\n",
            static_cast<unsigned long long>(device),static_cast<unsigned long long>(queue),static_cast<unsigned long long>(chain),
            haveLuid?"true":"false",static_cast<unsigned long long>(observedLuid),presentations,chainReleased?"true":"false",verified()?"true":"false");
    }
};
}
