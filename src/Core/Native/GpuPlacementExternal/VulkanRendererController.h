#pragma once
#include "Cleanup.h"
#include "QtD3D12Admission.h"
#include "VulkanLoaderIdentity.h"

namespace ExternalVulkan {
template<class T> static T readState(HANDLE process,uint64_t address) {
    T value{};SIZE_T count=0;
    require(address&&ReadProcessMemory(process,reinterpret_cast<void*>(address),&value,sizeof value,&count)
        &&count==sizeof value,"Vulkan renderer read");return value;
}
struct Call {uint64_t entry,returnAddress,returnStack,a,b,c;uint32_t capacity;};
struct Present {uint64_t rhi=0,physical=0,queue=0,entry=0,frameReturn=0,frameStack=0,returnAddress=0,returnStack=0,luid=0;};

static int runQt(int argc,wchar_t** argv) {
    SetErrorMode(32771);setvbuf(stdout,nullptr,_IONBF,0);
    if(argc!=8)return 2;
    DWORD pid=0;Handle process;ULONG flags=0;std::wstring directory;
    std::map<DWORD,Attached> attached;std::map<DWORD,Present> frames;std::map<DWORD,std::vector<Call>> calls;
    DEBUG_EVENT event{};bool pending=false,classified=false,didAttach=false;
    bool restoreFault=false,continueFault=false,clean=true,triggered=false,arrayDirty=false;
    bool lossPending=false;
    uint64_t lossRip=0,lossRsp=0,lossRax=0,admittedWindow=0,sourceLuid=0,requestedTarget=0;
    LoaderIdentity::Physical source{};
    uint64_t arrayAddress=0;std::vector<uint64_t> originalArray;
    size_t reorderedIndex=0;
    DWORD disposition=DBG_CONTINUE;unsigned presentations=0,enumerations=0,hits=0;std::string error;
    try {
        pid=DWORD(number(argv[1],MAXDWORD));const auto birth=number(argv[2],UINT64_MAX);
        std::wstring path=argv[3];std::replace(path.begin(),path.end(),L'/',L'\\');
        directory=argv[4];const auto target=number(argv[5],UINT64_MAX),deadline=number(argv[6],UINT64_MAX);
        requestedTarget=target;
        require(std::wstring(argv[7])==L"qtquick-vulkan","Vulkan renderer mode");
        process.h=OpenProcess(PROCESS_ALL_ACCESS,FALSE,pid);require(process.h,"renderer process");
        require(creationTime(process.h)==birth&&!_wcsicmp(imagePath(process.h).c_str(),path.c_str()),"exact renderer identity");
        BOOL debug=TRUE;require(CheckRemoteDebuggerPresent(process.h,&debug)&&!debug,"renderer already debugged");
        auto nt=GetModuleHandleW(L"ntdll.dll");query=(NtQuery)GetProcAddress(nt,"NtQueryInformationProcess");
        set=(NtSet)GetProcAddress(nt,"NtSetInformationProcess");require(query&&set,"debug flag APIs");
        require(query(process.h,31,&flags,sizeof flags,nullptr)>=0,"debug flags");
        ExternalDxgi::QtD3D12Admission qt(process.h,pid);LoaderIdentity loader(process.h,pid);
        const auto initial=qt.table();const auto endFrame=initial[1];
        // This profile's public endFrame export is at the recorded RVA.
        const auto gui=endFrame-0x259900;
        const auto enumerate=loader.entry(0x6b310),sorted=loader.entry(0x755a0);
        const std::array<uint64_t,4> recovery{enumerate,sorted,initial[0],0};
        const std::array<uint64_t,4> frameObservation{enumerate,sorted,endFrame,0};
        const auto authorization=directory+L"/controller.authorize";
        while(GetFileAttributesW(authorization.c_str())==INVALID_FILE_ATTRIBUTES&&GetTickCount64()<deadline)Sleep(20);
        require(GetTickCount64()<deadline,"authorization deadline");
        auto& owner=attached[pid];owner.handle=owned(process.h);owner.ft=birth;owner.entries=initial;
        require(DebugActiveProcess(pid),"attach renderer");didAttach=true;
        require(DebugSetProcessKillOnExit(FALSE),"no debugger kill");
        bool ready=false,presentationPublished=false;
        while(GetTickCount64()<deadline) {
            require(GetFileAttributesW((directory+L"/controller.cancel").c_str())==INVALID_FILE_ATTRIBUTES,"cancelled");
            if(GetFileAttributesW((directory+L"/controller.stop").c_str())!=INVALID_FILE_ATTRIBUTES)break;
            if(owner.initialBreak&&!ready){
                Handle marker(CreateFileW((directory+L"/controller.ready").c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
                require(marker.h!=INVALID_HANDLE_VALUE,"Vulkan controller ready");ready=true;
            }
            if(!WaitForDebugEvent(&event,20)){require(GetLastError()==ERROR_SEM_TIMEOUT,"debug wait");continue;}
            pending=true;classified=false;disposition=DBG_CONTINUE;
            require(event.dwProcessId==pid,"unexpected child");
            switch(event.dwDebugEventCode) {
            case CREATE_PROCESS_DEBUG_EVENT:
            case CREATE_THREAD_DEBUG_EVENT:{auto& t=owner.threads[event.dwThreadId];t.handle=ownedThread(pid,event.dwThreadId);arm(t,owner.entries,true);
                if(event.dwDebugEventCode==CREATE_PROCESS_DEBUG_EVENT&&event.u.CreateProcessInfo.hFile){CloseHandle(event.u.CreateProcessInfo.hFile);event.u.CreateProcessInfo.hFile=nullptr;}break;}
            case LOAD_DLL_DEBUG_EVENT:if(event.u.LoadDll.hFile){CloseHandle(event.u.LoadDll.hFile);event.u.LoadDll.hFile=nullptr;}break;
            case UNLOAD_DLL_DEBUG_EVENT:require(reinterpret_cast<uint64_t>(event.u.UnloadDll.lpBaseOfDll)!=loader.base&&reinterpret_cast<uint64_t>(event.u.UnloadDll.lpBaseOfDll)!=gui,"renderer module unloaded");break;
            case EXIT_THREAD_DEBUG_EVENT:{frames.erase(event.dwThreadId);calls.erase(event.dwThreadId);qt.threadExited(event.dwThreadId);auto it=owner.threads.find(event.dwThreadId);if(it!=owner.threads.end()){CloseHandle(it->second.handle);owner.threads.erase(it);}break;}
            case EXIT_PROCESS_DEBUG_EVENT:throw std::runtime_error("renderer exited");
            case EXCEPTION_DEBUG_EVENT:{
                const auto code=event.u.Exception.ExceptionRecord.ExceptionCode;
                if(code==EXCEPTION_BREAKPOINT&&!owner.initialBreak){owner.initialBreak=true;break;}
                if(code!=EXCEPTION_SINGLE_STEP){disposition=DBG_EXCEPTION_NOT_HANDLED;break;}
                auto& thread=owner.threads.at(event.dwThreadId);CONTEXT c{};c.ContextFlags=CONTEXT_ALL;
                require(GetThreadContext(thread.handle,&c),"renderer context");
                if(thread.pendingTrapAddress&&c.Rip==thread.pendingTrapAddress&&reinterpret_cast<uint64_t>(event.u.Exception.ExceptionRecord.ExceptionAddress)==c.Rip){
                    c.EFlags|=0x10000;c.Dr6=0;require(SetThreadContext(thread.handle,&c),"drain previous trap");classified=true;thread.pendingTrapAddress=0;break;
                }
                int slot=-1;for(int i=0;i<4;++i)if(thread.installedEntries[i]&&c.Rip==thread.installedEntries[i]&&(c.Dr6&(1u<<i))&&!(c.Dr6&(1ULL<<14))){slot=i;break;}
                if(slot<0){disposition=DBG_EXCEPTION_NOT_HANDLED;break;}
                require(++hits<4000,"Vulkan observation hit bound");
                auto next=thread.installedEntries;bool phaseChanged=false,frameReady=false;
                auto& stack=calls[event.dwThreadId];
                if(triggered&&!stack.empty()&&c.Rip==stack.back().returnAddress&&c.Rsp==stack.back().returnStack){
                    const auto call=stack.back();stack.pop_back();
                    require(static_cast<LONG>(c.Rax)==0,"Vulkan natural enumeration failed");
                    if(call.entry==sorted){
                        if(call.a==source.instance){
                            loader.capture(call.a,call.b,call.c);
                            sourceLuid=loader.luid(source);
                            require(sourceLuid!=target,"Vulkan source already on target adapter");
                        }
                    }
                    else if(call.c){
                        const auto count=readState<uint32_t>(process.h,call.b);require(count>0&&count<=call.capacity,"Vulkan returned count");
                        std::vector<uint64_t> handles;std::vector<uint64_t> luids;
                        bool matchingInstance=true;
                        for(unsigned i=0;i<count;++i){auto handle=readState<uint64_t>(process.h,call.c+i*8);handles.push_back(handle);matchingInstance&=loader.physical(handle).instance==source.instance;}
                        if(matchingInstance){
                        for(auto handle:handles)luids.push_back(loader.luid(handle));
                        require(std::count(luids.begin(),luids.end(),target)==1,"unique target in real Vulkan enumeration");
                        const auto index=static_cast<size_t>(std::find(luids.begin(),luids.end(),target)-luids.begin());
                        originalArray=handles;arrayAddress=call.c;std::swap(handles[0],handles[index]);arrayDirty=true;
                        SIZE_T bytes=0;require(WriteProcessMemory(process.h,reinterpret_cast<void*>(arrayAddress),handles.data(),handles.size()*8,&bytes)&&bytes==handles.size()*8,"Vulkan returned array reorder");
                        for(unsigned i=0;i<count;++i)require(readState<uint64_t>(process.h,arrayAddress+i*8)==handles[i],"Vulkan returned array verification");
                        reorderedIndex=index;
                        }
                    }
                    next[3]=stack.empty()?0:stack.back().returnAddress;
                }else if(triggered&&(c.Rip==enumerate||c.Rip==sorted)){
                    require(stack.size()<4,"Vulkan nested call bound");
                    const auto capacity=c.Rip==enumerate&&c.R8?readState<uint32_t>(process.h,c.Rdx):0;
                    require(capacity<=32,"Vulkan enumeration capacity");
                    stack.push_back({c.Rip,readState<uint64_t>(process.h,c.Rsp),c.Rsp+8,c.Rcx,c.Rdx,c.R8,capacity});
                    next[3]=stack.back().returnAddress;
                }else if(qt.frameReturned(event.dwThreadId,c)){
                    frames.erase(event.dwThreadId);next=triggered?recovery:initial;
                }else if(triggered&&c.Rip==initial[0]&&c.Rcx!=admittedWindow){
                    next[3]=stack.empty()?0:stack.back().returnAddress;
                }else if(qt.observe(event.dwThreadId,c,frameReady)){
                    if(triggered&&c.Rip==initial[0]){next=frameObservation;if(!stack.empty())next[3]=stack.back().returnAddress;}
                    if(frameReady){
                        require(stack.empty(),"frame entered during enumeration");
                        Present f;f.rhi=readState<uint64_t>(process.h,c.Rcx);
                        require(readState<uint64_t>(process.h,f.rhi)==gui+0x6c20c8,"qualified Vulkan backend vtable");
                        f.physical=readState<uint64_t>(process.h,f.rhi+0x1e8);const auto physical=loader.physical(f.physical);
                        f.queue=readState<uint64_t>(process.h,f.rhi+0x210);f.entry=readState<uint64_t>(process.h,f.rhi+0x7d8);
                        f.frameReturn=readState<uint64_t>(process.h,c.Rsp);f.frameStack=c.Rsp+8;
                        if(triggered){
                            require(qt.window(event.dwThreadId)==admittedWindow&&physical.instance==source.instance,"Vulkan recovery window or instance changed");
                            f.luid=loader.luid(physical);require(sourceLuid&&sourceLuid!=target&&f.luid==target,"Vulkan rebuilt on wrong adapter");
                        }
                        MEMORY_BASIC_INFORMATION memory{};require(VirtualQueryEx(process.h,reinterpret_cast<void*>(f.entry),&memory,sizeof memory)==sizeof memory&&memory.State==MEM_COMMIT&&!(memory.Protect&PAGE_GUARD)&&(memory.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY)),"Vulkan cached Present executable");
                        frames[event.dwThreadId]=f;next={f.entry,f.frameReturn,0,0};
                    }
                }else{
                    auto& f=frames.at(event.dwThreadId);
                    if(c.Rip==f.entry){
                        require(c.Rcx==f.queue,"Vulkan actual queue");const auto info=readState<std::array<uint64_t,8>>(process.h,c.Rdx);
                        require(static_cast<uint32_t>(info[0])==1000001001&&info[1]==0&&info[4]==1&&info[7]==0,"single natural Vulkan presentation");
                        f.returnAddress=readState<uint64_t>(process.h,c.Rsp);f.returnStack=c.Rsp+8;
                        require(f.returnAddress==gui+0x4d373e,"qualified Qt Vulkan Present call site");
                        if(!triggered)qt.submitted(event.dwThreadId);
                        next={0,f.frameReturn,f.returnAddress,0};
                    }else if(c.Rip==f.returnAddress&&c.Rsp==f.returnStack){
                        require(static_cast<LONG>(c.Rax)==0,"natural Vulkan Present did not succeed");
                        if(!triggered&&GetFileAttributesW((directory+L"/device-loss.request").c_str())!=INVALID_FILE_ATTRIBUTES){
                            qt.authorizeLoss(event.dwThreadId,deadline,"QT_VK_PHYSICAL_DEVICE_INDEX");
                            printf("{\"kind\":\"qtVulkanSubmission\",\"tid\":%lu}\n",event.dwThreadId);
                            admittedWindow=qt.window(event.dwThreadId);source=loader.physical(f.physical);
                            lossRip=c.Rip;lossRsp=c.Rsp;lossRax=c.Rax;lossPending=true;
                            c.Rax=static_cast<uint32_t>(-4);phaseChanged=true;next=recovery;
                        }else if(triggered){
                            ++presentations;printf("{\"kind\":\"targetVulkanPresent\",\"result\":0,\"luid\":%llu,\"physical\":%llu,\"queue\":%llu}\n",f.luid,f.physical,f.queue);
                            next={0,f.frameReturn,0,0};
                        }else next={0,f.frameReturn,0,0};
                    }else{require(c.Rip==f.frameReturn&&c.Rsp==f.frameStack,"Qt frame return stack");frames.erase(event.dwThreadId);next=recovery;}
                }
                if(phaseChanged){frames.clear();owner.entries=recovery;for(auto& t:owner.threads)arm(t.second,recovery,true);}
                else arm(thread,next,true);
                // Install recovery breakpoints before publishing a changed return value.
                // Do not overwrite the debug registers just installed by arm().
                c.ContextFlags=CONTEXT_CONTROL|CONTEXT_INTEGER;c.EFlags|=0x10000;
                require(SetThreadContext(thread.handle,&c),"Vulkan call response");classified=true;thread.pendingTrapAddress=0;
                break;
            }
            default:break;
            }
            classified=true;require(ContinueDebugEvent(pid,event.dwThreadId,disposition),"continue renderer");pending=false;
            if(lossPending){
                lossPending=false;triggered=true;
                printf("{\"kind\":\"syntheticVulkanDeviceLoss\",\"originalResult\":0,\"returnedResult\":-4,\"count\":1}\n");
            }
            if(arrayDirty){
                arrayDirty=false;++enumerations;
                printf("{\"kind\":\"vulkanEnumerationReordered\",\"count\":%llu,\"targetLuid\":%llu,\"targetOriginalIndex\":%llu}\n",static_cast<uint64_t>(originalArray.size()),target,static_cast<uint64_t>(reorderedIndex));
            }
            if(presentations>=30&&!presentationPublished){
                Handle marker(CreateFileW((directory+L"/controller.presentation-observed").c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
                require(marker.h!=INVALID_HANDLE_VALUE,"Vulkan presentation publication");presentationPublished=true;
            }
        }
        require(GetFileAttributesW((directory+L"/controller.stop").c_str())!=INVALID_FILE_ATTRIBUTES,"Vulkan controller deadline");
        require(triggered&&enumerations>0&&presentations>=30,"Vulkan migration observation incomplete");
    }catch(const std::exception& ex){error=ex.what();}
    bool cleanupOk=!didAttach;
    while(!cleanupOk){
        try{
            if(lossPending&&live(process.h)){
                require(pending,"Vulkan loss rollback requires pending event");
                auto& thread=attached.at(pid).threads.at(event.dwThreadId);
                CONTEXT c{};c.ContextFlags=CONTEXT_CONTROL|CONTEXT_INTEGER;
                require(GetThreadContext(thread.handle,&c)&&c.Rip==lossRip&&c.Rsp==lossRsp,"Vulkan loss rollback return identity");
                require(c.Rax==lossRax||c.Rax==static_cast<uint32_t>(-4),"Vulkan loss rollback original value");
                c.Rax=lossRax;require(SetThreadContext(thread.handle,&c),"restore Vulkan natural result");
                require(GetThreadContext(thread.handle,&c)&&c.Rax==lossRax&&c.Rip==lossRip&&c.Rsp==lossRsp,"verify Vulkan natural result");
                lossPending=false;
            }
            if(arrayDirty&&live(process.h)){
                require(pending,"Vulkan array rollback requires pending event");
                SIZE_T bytes=0;require(WriteProcessMemory(process.h,reinterpret_cast<void*>(arrayAddress),originalArray.data(),originalArray.size()*8,&bytes)&&bytes==originalArray.size()*8,"restore Vulkan result array");
                for(size_t i=0;i<originalArray.size();++i)require(readState<uint64_t>(process.h,arrayAddress+i*8)==originalArray[i],"verify restored Vulkan array");
                arrayDirty=false;
            }
            cleanupSession(attached,pid,process.h,flags,event,pending,disposition,classified,restoreFault,continueFault);cleanupOk=true;
        }catch(const std::exception& ex){clean=false;printf("{\"kind\":\"recoveryRequired\",\"error\":%s}\n",quote(ex.what()).c_str());const auto request=directory+L"/controller.stop.recover";while(GetFileAttributesW(request.c_str())==INVALID_FILE_ATTRIBUTES||!DeleteFileW(request.c_str()))Sleep(100);}
    }
    for(auto& p:attached){for(auto& t:p.second.threads)CloseHandle(t.second.handle);CloseHandle(p.second.handle);}
    BOOL debug=TRUE;const bool absent=process.h&&live(process.h)&&CheckRemoteDebuggerPresent(process.h,&debug)&&!debug;
    const bool passed=error.empty()&&clean&&absent;
    printf("{\"kind\":\"vulkanObservation\",\"sourceLuid\":%llu,\"luid\":%llu,\"window\":%llu,\"instance\":%llu,\"targetPresents\":%u,\"targetPresentationVerified\":%s}\n",sourceLuid,requestedTarget,admittedWindow,source.instance,presentations,triggered&&sourceLuid&&sourceLuid!=requestedTarget&&presentations>=30?"true":"false");
    printf("{\"kind\":\"summary\",\"passed\":%s,\"cleanupPassed\":%s,\"debuggerAbsent\":%s,\"recoveryRequested\":%s,\"targetPresents\":%u,\"enumerations\":%u,\"error\":%s}\n",passed?"true":"false",clean&&cleanupOk?"true":"false",absent?"true":"false",triggered?"true":"false",presentations,enumerations,quote(error).c_str());
    return passed?0:1;
}
}
