#include "Cleanup.h"
#include "DxgiEntryDiscovery.h"
#include "D3D12ResultObservation.h"
#include "QtD3D12Admission.h"
#include "VulkanRendererController.h"
#include <memory>

static bool returnsToQtGui(HANDLE process,DWORD pid,uint64_t stack) {
    uint64_t address=0; SIZE_T bytes=0;
    require(ReadProcessMemory(process,reinterpret_cast<void*>(stack),&address,sizeof address,&bytes)
        &&bytes==sizeof address,"Qt renderer caller");
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE,pid));
    require(snapshot.h!=INVALID_HANDLE_VALUE,"Qt renderer modules");
    MODULEENTRY32W module{}; module.dwSize=sizeof module;
    for(BOOL ok=Module32FirstW(snapshot.h,&module);ok;ok=Module32NextW(snapshot.h,&module))
        if(_wcsicmp(module.szModule,L"Qt6Gui.dll")==0)
            return address>=reinterpret_cast<uint64_t>(module.modBaseAddr)
                &&address-reinterpret_cast<uint64_t>(module.modBaseAddr)<module.modBaseSize;
    return false;
}

int wmain(int argc,wchar_t** argv) {
    SetErrorMode(32771); setvbuf(stdout,nullptr,_IONBF,0);
    if(argc!=8) return 2;
    if(std::wstring(argv[7])==L"qtquick-vulkan") return ExternalVulkan::runQt(argc,argv);
    DWORD pid=0; ULONG originalFlags=0;
    Handle process; std::map<DWORD,Attached> attached;
    DEBUG_EVENT event{}; bool pending=false,classified=false,didAttach=false;
    DWORD disposition=DBG_CONTINUE;
    bool restoreFault=false,continueFault=false,clean=true,triggered=false;
    DWORD presentThread=0; uint64_t presentStack=0,returnAddress=0;
    uint64_t selectedPresentEntry=0;std::array<unsigned char,32> selectedPresentBytes{};
    unsigned hits=0,enumerations=0; std::string error; std::wstring stop;
    std::unique_ptr<ExternalDxgi::D3D12ResultObservation> observation;
    bool presentationPublished=false;
    std::set<DWORD> qtSubmissionThreads;
    std::unique_ptr<ExternalDxgi::QtD3D12Admission> qtAdmission;
    try {
        pid=DWORD(number(argv[1],MAXDWORD)); const auto birth=number(argv[2],UINT64_MAX);
        std::wstring expected=argv[3]; std::replace(expected.begin(),expected.end(),L'/',L'\\');
        const std::wstring directory=argv[4]; stop=directory+L"/controller.stop";
        const auto target=number(argv[5],UINT64_MAX),deadline=number(argv[6],UINT64_MAX);
        const std::wstring api=argv[7];
        const bool qtRenderer=api==L"qtquick-d3d12";
        require(api==L"dxgi-d3d11"||api==L"dxgi-d3d12"||qtRenderer,"renderer API");
        process.h=OpenProcess(PROCESS_ALL_ACCESS,FALSE,pid); require(process.h!=nullptr,"renderer process");
        require(creationTime(process.h)==birth&&_wcsicmp(imagePath(process.h).c_str(),expected.c_str())==0,"exact renderer identity");
        BOOL wow64=TRUE;
        require(IsWow64Process(process.h,&wow64)&&!wow64,"native x64 renderer required");
        BOOL debug=TRUE; require(CheckRemoteDebuggerPresent(process.h,&debug)&&!debug,"renderer already debugged");
        auto nt=GetModuleHandleW(L"ntdll.dll");
        query=(NtQuery)GetProcAddress(nt,"NtQueryInformationProcess");
        set=(NtSet)GetProcAddress(nt,"NtSetInformationProcess"); require(query&&set,"native exports");
        require(query(process.h,31,&originalFlags,sizeof originalFlags,nullptr)>=0,"debug flags");
        const auto present=ExternalDxgi::Discover(process.h,pid,api==L"dxgi-d3d11"?ExternalDxgi::Renderer::D3D11:ExternalDxgi::Renderer::D3D12,qtRenderer);
        if(qtRenderer) qtAdmission=std::make_unique<ExternalDxgi::QtD3D12Admission>(process.h,pid);
        if(api!=L"dxgi-d3d11") observation=std::make_unique<ExternalDxgi::D3D12ResultObservation>(process.h,present,target);
        Com<IDXGIFactory1> factory; Com<IDXGIFactory6> factory6;
        require(SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory1),(void**)factory.out())),"factory");
        require(SUCCEEDED(factory->QueryInterface(__uuidof(IDXGIFactory6),(void**)factory6.out())),"factory6");
        UINT targetIndex=UINT_MAX; std::array<UINT,3> preferenceIndices{UINT_MAX,UINT_MAX,UINT_MAX};
        for(UINT i=0;i<16;++i) {
            Com<IDXGIAdapter1> adapter; auto result=factory->EnumAdapters1(i,adapter.out());
            if(result==DXGI_ERROR_NOT_FOUND) break;
            require(SUCCEEDED(result)&&adapter.p,"adapter enumeration"); DXGI_ADAPTER_DESC1 desc{};
            require(SUCCEEDED(adapter->GetDesc1(&desc)),"adapter description");
            if(luidBits(desc.AdapterLuid)==target&&!(desc.Flags&DXGI_ADAPTER_FLAG_SOFTWARE)) targetIndex=i;
        }
        require(targetIndex!=UINT_MAX,"target hardware adapter");
        for(UINT preference=0;preference<3;++preference) for(UINT i=0;i<16;++i) {
            Com<IDXGIAdapter1> adapter;
            const auto result=factory6->EnumAdapterByGpuPreference(i,static_cast<DXGI_GPU_PREFERENCE>(preference),__uuidof(IDXGIAdapter1),(void**)adapter.out());
            if(result==DXGI_ERROR_NOT_FOUND) break;
            require(SUCCEEDED(result)&&adapter.p,"preference enumeration"); DXGI_ADAPTER_DESC1 desc{};
            require(SUCCEEDED(adapter->GetDesc1(&desc)),"preference description");
            if(luidBits(desc.AdapterLuid)==target) preferenceIndices[preference]=i;
        }
        for(auto index:preferenceIndices) require(index!=UINT_MAX,"target in preference order");
        const auto entryRole=qtRenderer?ExternalDxgi::EntryRole::QualifiedRenderer:ExternalDxgi::EntryRole::Strict;
        const std::array<uint64_t,4> enumerationEntries{
            ExternalDxgi::Resolve(process.h,pid,(uint64_t)(*(void***)factory.p)[7],entryRole),
            ExternalDxgi::Resolve(process.h,pid,(uint64_t)(*(void***)factory.p)[12],entryRole),
            ExternalDxgi::Resolve(process.h,pid,(uint64_t)(*(void***)factory6.p)[26],entryRole),
            ExternalDxgi::Resolve(process.h,pid,(uint64_t)(*(void***)factory6.p)[29],entryRole)};
        std::set<uint64_t> entryModules;
        std::vector<uint64_t> lifetimeEntries{present.present,present.present1,enumerationEntries[0],enumerationEntries[1],enumerationEntries[2],enumerationEntries[3],present.createDevice,present.adapterLuid,present.createQueue,present.createSwapChain,present.releaseSwapChain,present.executeCommandLists};
        if(qtAdmission) for(auto entry:qtAdmission->table()) if(entry) lifetimeEntries.push_back(entry);
        for(auto address:lifetimeEntries) {
            if(!address) continue;
            MEMORY_BASIC_INFORMATION memory{};
            require(VirtualQueryEx(process.h,(void*)address,&memory,sizeof memory)==sizeof memory&&memory.Type==MEM_IMAGE,"renderer entry module lifetime");
            entryModules.insert(reinterpret_cast<uint64_t>(memory.AllocationBase));
        }
        const auto authorization=directory+L"/controller.authorize";
        while(GetFileAttributesW(authorization.c_str())==INVALID_FILE_ATTRIBUTES&&GetTickCount64()<deadline) Sleep(20);
        require(GetFileAttributesW(authorization.c_str())!=INVALID_FILE_ATTRIBUTES&&GetTickCount64()<deadline,"identity publication authorization");
        auto& owner=attached[pid]; owner.handle=owned(process.h); owner.ft=birth;
        owner.entries={present.present,present.present1,0,present.executeCommandLists};
        if(qtAdmission) owner.entries=qtAdmission->table();
        require(DebugActiveProcess(pid),"attach renderer"); didAttach=true;
        require(DebugSetProcessKillOnExit(FALSE),"no debugger kill");
        bool ready=false;
        while(GetTickCount64()<deadline) {
            require(GetFileAttributesW((directory+L"/controller.cancel").c_str())==INVALID_FILE_ATTRIBUTES,"cancelled");
            if(GetFileAttributesW(stop.c_str())!=INVALID_FILE_ATTRIBUTES) break;
            if(owner.initialBreak&&!ready) {
                Handle file(CreateFileW((directory+L"/controller.ready").c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
                require(file.h!=INVALID_HANDLE_VALUE,"controller ready"); ready=true;
            }
            if(!WaitForDebugEvent(&event,20)) {
                require(GetLastError()==ERROR_SEM_TIMEOUT,"debug wait");
                continue;
            }
            pending=true; classified=false; disposition=DBG_CONTINUE;
            require(event.dwProcessId==pid,"unexpected child");
            switch(event.dwDebugEventCode) {
            case CREATE_PROCESS_DEBUG_EVENT:
            case CREATE_THREAD_DEBUG_EVENT: {
                auto& thread=owner.threads[event.dwThreadId]; thread.handle=ownedThread(pid,event.dwThreadId);
                arm(thread,owner.entries,true);
                if(event.dwDebugEventCode==CREATE_PROCESS_DEBUG_EVENT&&event.u.CreateProcessInfo.hFile) {
                    CloseHandle(event.u.CreateProcessInfo.hFile); event.u.CreateProcessInfo.hFile=nullptr;
                }
                break;
            }
            case EXIT_THREAD_DEBUG_EVENT: {
                qtSubmissionThreads.erase(event.dwThreadId);
                if(qtAdmission) qtAdmission->threadExited(event.dwThreadId);
                if(observation) observation->threadExited(event.dwThreadId);
                auto it=owner.threads.find(event.dwThreadId);
                if(it!=owner.threads.end()) { CloseHandle(it->second.handle); owner.threads.erase(it); }
                require(event.dwThreadId!=presentThread,"present thread exited during recovery observation"); break;
            }
            case LOAD_DLL_DEBUG_EVENT:
                if(event.u.LoadDll.hFile) { CloseHandle(event.u.LoadDll.hFile); event.u.LoadDll.hFile=nullptr; }
                break;
            case UNLOAD_DLL_DEBUG_EVENT:
                require(!entryModules.count(reinterpret_cast<uint64_t>(event.u.UnloadDll.lpBaseOfDll)),"renderer entry module unloaded");
                break;
            case EXCEPTION_DEBUG_EVENT: {
                const auto code=event.u.Exception.ExceptionRecord.ExceptionCode;
                if(code==EXCEPTION_BREAKPOINT&&!owner.initialBreak) { owner.initialBreak=true; break; }
                if(code!=EXCEPTION_SINGLE_STEP) { disposition=DBG_EXCEPTION_NOT_HANDLED; break; }
                auto& thread=owner.threads.at(event.dwThreadId); CONTEXT context{}; context.ContextFlags=CONTEXT_ALL;
                require(GetThreadContext(thread.handle,&context),"renderer context");
                bool observedPrevious=false;
                if(thread.pendingTrapAddress && context.Rip==thread.pendingTrapAddress
                    && reinterpret_cast<uint64_t>(event.u.Exception.ExceptionRecord.ExceptionAddress)==context.Rip) {
                    observedPrevious=triggered&&observation&&observation->hit(event.dwThreadId,context);
                    if(!observedPrevious) {
                        context.EFlags|=0x10000; context.Dr6=0;
                        require(SetThreadContext(thread.handle,&context),"settle previous renderer trap");
                        classified=true;thread.pendingTrapAddress=0;
                        printf("{\"kind\":\"previousRendererTrapDrained\",\"tid\":%lu}\n",event.dwThreadId);
                        break;
                    }
                    printf("{\"kind\":\"previousRendererTrapObserved\",\"tid\":%lu}\n",event.dwThreadId);
                }
                int slot=observedPrevious?4:-1;
                for(int i=0;i<4;++i) if(thread.installedEntries[i]&&context.Rip==thread.installedEntries[i]&&(context.Dr6&(1u<<i))&&!(context.Dr6&(1ULL<<14))) { slot=i; break; }
                if(slot<0) { disposition=DBG_EXCEPTION_NOT_HANDLED; break; }
                require(++hits<4000,"renderer hit limit"); bool changePhase=false,qtFrameReady=false,qtFrameFinished=false;
                if(!triggered) {
                    if(qtAdmission&&qtAdmission->frameReturned(event.dwThreadId,context)) {
                        qtFrameFinished=true;
                    } else if(qtAdmission&&qtAdmission->observe(event.dwThreadId,context,qtFrameReady)) {
                        // A natural frame proves the current device's configuration before any mutation.
                    } else if(qtRenderer&&slot==3&&context.Rip==present.executeCommandLists&&context.Rdx>0&&returnsToQtGui(process.h,pid,context.Rsp)) {
                        std::array<unsigned char,32> bytes{};SIZE_T read=0;
                        require(ReadProcessMemory(process.h,reinterpret_cast<void*>(context.Rip),bytes.data(),bytes.size(),&read)
                            &&read==bytes.size()&&bytes==present.submissionEntryBytes,"submission observation entry changed");
                        qtAdmission->submitted(event.dwThreadId);
                        if(qtSubmissionThreads.insert(event.dwThreadId).second)
                            printf("{\"kind\":\"qtD3D12Submission\",\"tid\":%lu}\n",event.dwThreadId);
                    } else if(slot<2&&!presentThread&&!(context.R8&DXGI_PRESENT_TEST)
                        &&(!qtRenderer||(qtAdmission->canPresent(event.dwThreadId)&&returnsToQtGui(process.h,pid,context.Rsp)))
                        &&GetFileAttributesW((directory+L"/device-loss.request").c_str())!=INVALID_FILE_ATTRIBUTES) {
                        SIZE_T bytes=0;
                        selectedPresentEntry=context.Rip;
                        selectedPresentBytes=context.Rip==present.present?present.presentBytes:present.present1Bytes;
                        std::array<unsigned char,32> currentEntry{};
                        require(ReadProcessMemory(process.h,reinterpret_cast<void*>(selectedPresentEntry),currentEntry.data(),currentEntry.size(),&bytes)
                            &&bytes==currentEntry.size()&&currentEntry==selectedPresentBytes,"present entry changed");
                        if(qtAdmission) qtAdmission->validate(event.dwThreadId);
                        require(ReadProcessMemory(process.h,(void*)context.Rsp,&returnAddress,sizeof returnAddress,&bytes)&&bytes==sizeof returnAddress,"present return");
                        presentThread=event.dwThreadId; presentStack=context.Rsp;
                        if(qtRenderer) context.Dr3=returnAddress;
                        else context.Dr2=returnAddress;
                        qtSubmissionThreads.erase(event.dwThreadId);
                    } else if(slot==(qtRenderer?3:2)&&event.dwThreadId==presentThread&&context.Rsp==presentStack+sizeof(uint64_t)) {
                        if(static_cast<HRESULT>(context.Rax)==S_OK) {
                            std::array<unsigned char,32> currentEntry{};SIZE_T bytes=0;
                            require(ReadProcessMemory(process.h,reinterpret_cast<void*>(selectedPresentEntry),currentEntry.data(),currentEntry.size(),&bytes)
                                &&bytes==currentEntry.size()&&currentEntry==selectedPresentBytes,"present entry changed before recovery");
                            if(qtAdmission) qtAdmission->authorizeLoss(event.dwThreadId,deadline);
                            context.Rax=static_cast<uint32_t>(DXGI_ERROR_DEVICE_REMOVED);
                            triggered=true; changePhase=true;
                            printf("{\"kind\":\"syntheticDeviceLoss\",\"originalResult\":0,\"returnedResult\":%u,\"count\":1}\n",static_cast<uint32_t>(context.Rax));
                        }
                        if(qtRenderer) {context.Dr3=0;qtAdmission->threadExited(event.dwThreadId);qtFrameFinished=true;}
                        else context.Dr2=0;
                        presentThread=0;
                    }
                } else if(observedPrevious||(observation&&observation->hit(event.dwThreadId,context))) {
                    // Observation reads natural call results; it never invokes target code.
                } else if(context.Rip!=enumerationEntries[2]) {
                    const bool preferenceCall=context.Rip==enumerationEntries[3];
                    const auto preference=static_cast<UINT>(context.R8);
                    require(!preferenceCall||preference<preferenceIndices.size(),"enumeration preference");
                    const UINT index=preferenceCall?preferenceIndices[preference]:targetIndex;
                    const UINT requested=static_cast<UINT>(context.Rdx);
                    context.Rdx=requested==0?index:(requested==index?0:requested);
                    ++enumerations;
                    printf("{\"kind\":\"rendererEnumeration\",\"slot\":%d,\"requested\":%u,\"selected\":%llu}\n",slot,requested,static_cast<unsigned long long>(context.Rdx));
                }
                context.EFlags|=0x10000; context.Dr6=0;
                require(SetThreadContext(thread.handle,&context),"renderer response"); classified=true;
                thread.pendingTrapAddress=0;
                if(!triggered) {thread.installedEntries[2]=context.Dr2;thread.installedEntries[3]=context.Dr3;}
                if(qtFrameReady) arm(thread,{present.present,present.present1,qtAdmission->frameReturn(event.dwThreadId),present.executeCommandLists},true);
                if(qtFrameFinished&&!triggered) arm(thread,qtAdmission->table(),true);
                if(triggered&&observation) {
                    owner.entries=observation->table(enumerationEntries);
                    for(auto& pair:owner.threads) {
                        const auto next=observation->threadTable(pair.first,owner.entries);
                        if(pair.second.installedEntries!=next) arm(pair.second,next,true);
                    }
                    if(observation->verified()&&!presentationPublished) {
                        Handle file(CreateFileW((directory+L"/controller.presentation-observed").c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
                        require(file.h!=INVALID_HANDLE_VALUE,"presentation observation publication");
                        presentationPublished=true;
                    }
                } else if(changePhase) {
                    owner.entries=enumerationEntries;
                    for(auto& pair:owner.threads) arm(pair.second,owner.entries,true);
                }
                break;
            }
            case EXIT_PROCESS_DEBUG_EVENT: throw std::runtime_error("renderer process exited");
            default: break;
            }
            classified=true; require(ContinueDebugEvent(pid,event.dwThreadId,disposition),"continue renderer"); pending=false;
        }
        require(GetFileAttributesW(stop.c_str())!=INVALID_FILE_ATTRIBUTES,"renderer deadline");
        require(triggered&&enumerations>0,"renderer did not re-enumerate after recovery request");
        require(!observation||observation->verified(),"D3D12 target presentation not observed");
    } catch(const std::exception& exception) { error=exception.what(); }
    bool cleanupOk=!didAttach;
    while(!cleanupOk) {
        try { cleanupSession(attached,pid,process.h,originalFlags,event,pending,disposition,classified,restoreFault,continueFault); cleanupOk=true; }
        catch(const std::exception& exception) {
            clean=false; printf("{\"kind\":\"recoveryRequired\",\"error\":%s}\n",quote(exception.what()).c_str());
            const auto recovery=stop+L".recover";
            while(GetFileAttributesW(recovery.c_str())==INVALID_FILE_ATTRIBUTES||!DeleteFileW(recovery.c_str())) Sleep(100);
        }
    }
    for(auto& pair:attached) { for(auto& thread:pair.second.threads) CloseHandle(thread.second.handle); CloseHandle(pair.second.handle); }
    BOOL debug=TRUE; const bool absent=process.h&&live(process.h)&&CheckRemoteDebuggerPresent(process.h,&debug)&&!debug;
    const bool passed=error.empty()&&clean&&absent;
    if(observation) observation->report();
    printf("{\"kind\":\"summary\",\"passed\":%s,\"cleanupPassed\":%s,\"debuggerAbsent\":%s,\"recoveryRequested\":%s,\"enumerations\":%u,\"migrationVerified\":false,\"error\":%s}\n",
        passed?"true":"false",clean&&cleanupOk?"true":"false",absent?"true":"false",triggered?"true":"false",enumerations,quote(error).c_str());
    return passed?0:1;
}
