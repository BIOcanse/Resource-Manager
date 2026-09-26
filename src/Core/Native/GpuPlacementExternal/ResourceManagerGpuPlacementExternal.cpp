#include "Cleanup.h"
#include "ChromiumArguments.h"

int wmain(int argc,wchar_t** argv) {
    SetErrorMode(32771); setvbuf(stdout,nullptr,_IONBF,0);
    if(argc!=11
#ifdef RM_EXTERNAL_FIXTURE
       && argc!=12
#endif
    ) { fprintf(stderr,"root-pid root-ft gpu-pid gpu-ft stop-file ready-file target-luid deadline-ms root-image gpu-image\n"); return 2; }
    DWORD rootId=0,gpuId=0;
    uint64_t rootFt=0,gpuFt=0,targetKey=0,deadline=0;
    std::map<DWORD,Attached> attached;
    Handle root, oldGpu;
    bool attachedRoot=false, rootInitialBreak=false, switched=false, cleaned=true;
    ULONG originalFlags=0;
    UINT observed=0, filtered=0, newGpus=0;
    UINT startupRearms=0;
    std::map<DWORD,DWORD> initialThreadIds;
    DEBUG_EVENT pendingEvent{}; bool hasPending=false;
    DWORD pendingDisposition=DBG_CONTINUE; bool pendingClassified=false;
    std::wstring fault;
#ifdef RM_EXTERNAL_FIXTURE
    if(argc==12) fault=argv[11];
#endif
    bool restoreFault=fault==L"restore";
    bool continueFault=fault==L"continue-cleanup";
    std::string error;
    try {
        rootId=DWORD(number(argv[1],MAXDWORD)); rootFt=number(argv[2],UINT64_MAX);
        gpuId=DWORD(number(argv[3],MAXDWORD)); gpuFt=number(argv[4],UINT64_MAX);
        targetKey=number(argv[7],UINT64_MAX); deadline=number(argv[8],UINT64_MAX);
        require(deadline>GetTickCount64(),"expired action deadline");
        HMODULE nt=GetModuleHandleW(L"ntdll.dll");
        query=(NtQuery)GetProcAddress(nt,"NtQueryInformationProcess");
        set=(NtSet)GetProcAddress(nt,"NtSetInformationProcess");
        require(query && set,"native exports");
        root.h=OpenProcess(PROCESS_ALL_ACCESS,FALSE,rootId);
        oldGpu.h=OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ|PROCESS_TERMINATE|SYNCHRONIZE,FALSE,gpuId);
        require(root.h && oldGpu.h,"open exact renderer targets");
        require(creationTime(root.h)==rootFt && creationTime(oldGpu.h)==gpuFt,"native creation mismatch");
        require(rootFt<=gpuFt,"parent creation order");
        require(_wcsicmp(imagePath(root.h).c_str(),argv[9])==0 && _wcsicmp(imagePath(oldGpu.h).c_str(),argv[10])==0,"renderer image mismatch");
        if(!fault.empty()) require(cmdline(root.h).find(L"--user-data-dir=")!=std::wstring::npos &&
            cmdline(root.h).find(L"external-gpu-enum-20260920-v1")!=std::wstring::npos,"faults require owned rehearsal profile");
        require(!ChromiumArguments::HasType(ChromiumArguments::Parse(cmdline(root.h))) &&
            ChromiumArguments::IsCompatibleGpu(ChromiumArguments::Parse(cmdline(oldGpu.h))),"renderer role/backend mismatch");
        PROCESS_BASIC_INFORMATION info{};
        require(query(oldGpu.h,0,&info,sizeof info,nullptr)>=0 && (DWORD)info.InheritedFromUniqueProcessId==rootId,"GPU parent mismatch");
        BOOL debugging=TRUE; require(CheckRemoteDebuggerPresent(root.h,&debugging) && !debugging,"already debugged root");
        require(query(root.h,31,&originalFlags,sizeof originalFlags,nullptr)>=0,"read original debug flags");
        PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY policy{};
        require(GetProcessMitigationPolicy(oldGpu.h,ProcessSignaturePolicy,&policy,sizeof policy),"old GPU policy");
        printf("{\"kind\":\"baseline\",\"rootPid\":%lu,\"rootFileTime\":\"%llu\",\"gpuPid\":%lu,\"gpuFileTime\":\"%llu\",\"signaturePolicy\":%lu,\"debugFlags\":%lu}\n",rootId,(unsigned long long)rootFt,gpuId,(unsigned long long)gpuFt,policy.Flags,originalFlags);
        Com<IDXGIFactory1> f; Com<IDXGIFactory6> f6;
        require(SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory1),(void**)f.out())),"factory");
        require(SUCCEEDED(f->QueryInterface(__uuidof(IDXGIFactory6),(void**)f6.out())),"factory6");
        UINT targetIndex=UINT_MAX; std::array<UINT,3> preferenceIndices{UINT_MAX,UINT_MAX,UINT_MAX}; LUID target{};
        for(UINT i=0;i<16;++i) {
            Com<IDXGIAdapter1> a; auto hr=f->EnumAdapters1(i,a.out());
            if(hr==DXGI_ERROR_NOT_FOUND) break;
            require(SUCCEEDED(hr)&&a.p,"adapter");
            DXGI_ADAPTER_DESC1 d{}; require(SUCCEEDED(a->GetDesc1(&d)),"adapter desc");
            if(luidBits(d.AdapterLuid)==targetKey && !(d.Flags&DXGI_ADAPTER_FLAG_SOFTWARE)) { require(targetIndex==UINT_MAX,"ambiguous target"); targetIndex=i; target=d.AdapterLuid; }
        }
        require(targetIndex!=UINT_MAX,"target adapter");
        for(UINT preference=0;preference<preferenceIndices.size();++preference) for(UINT i=0;i<16;++i) {
            Com<IDXGIAdapter1> a; auto hr=f6->EnumAdapterByGpuPreference(i,(DXGI_GPU_PREFERENCE)preference,__uuidof(IDXGIAdapter1),(void**)a.out());
            if(hr==DXGI_ERROR_NOT_FOUND) break;
            require(SUCCEEDED(hr)&&a.p,"power adapter");
            DXGI_ADAPTER_DESC1 d{}; require(SUCCEEDED(a->GetDesc1(&d)),"power desc");
            if(equal(d.AdapterLuid,target)) preferenceIndices[preference]=i;
        }
        for(auto index:preferenceIndices) require(index!=UINT_MAX,"preference target");
        std::array<uint64_t,4> local{(uint64_t)(*(void***)f.p)[7],(uint64_t)(*(void***)f.p)[12],(uint64_t)(*(void***)f6.p)[26],(uint64_t)(*(void***)f6.p)[29]};
        HMODULE module=nullptr;
        require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,(LPCWSTR)local[0],&module),"local DXGI module");
        wchar_t moduleName[MAX_PATH]{}; require(GetModuleFileNameW(module,moduleName,MAX_PATH)>0,"module path");
        for(auto entry:local) { HMODULE owner=nullptr; require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,(LPCWSTR)entry,&owner)&&owner==module,"entry owner"); }
        // The caller persists the controller identity before allowing any target mutation.
        std::wstring authorization=std::wstring(argv[5])+L".authorize";
        while(GetFileAttributesW(authorization.c_str())==INVALID_FILE_ATTRIBUTES && GetTickCount64()<deadline) {
            require(GetFileAttributesW((std::wstring(argv[5])+L".cancel").c_str())==INVALID_FILE_ATTRIBUTES,"cancelled before authorization");
            Sleep(20);
        }
        require(GetFileAttributesW(authorization.c_str())!=INVALID_FILE_ATTRIBUTES && GetTickCount64()<deadline,"action not authorized");
        auto& rootRecord=attached[rootId]; rootRecord.handle=owned(root.h); rootRecord.ft=rootFt;
        require(DebugActiveProcess(rootId),"attach browser"); attachedRoot=true;
        require(DebugSetProcessKillOnExit(FALSE),"disable debugger kill on exit");
        while(GetTickCount64()<deadline) {
            require(GetFileAttributesW((std::wstring(argv[5])+L".cancel").c_str())==INVALID_FILE_ATTRIBUTES,"cancelled");
            if(GetFileAttributesW(argv[5])!=INVALID_FILE_ATTRIBUTES) break;
            DEBUG_EVENT ev{};
            if(!WaitForDebugEvent(&ev,20)) {
                require(GetLastError()==ERROR_SEM_TIMEOUT,"debug wait");
                if(rootInitialBreak && !switched) {
                    ULONG inherit=1, actual=0;
                    require(set(root.h,31,&inherit,sizeof inherit)>=0 && query(root.h,31,&actual,sizeof actual,nullptr)>=0 && actual==inherit,"enable child inheritance");
                    require(WaitForSingleObject(oldGpu.h,0)==WAIT_TIMEOUT,"original GPU already gone");
                    // The handle stays open from exact identity validation through the single restart.
                    require(TerminateProcess(oldGpu.h,0x47505552),"request one GPU child restart"); switched=true;
                    printf("{\"kind\":\"restart\",\"pid\":%lu,\"requestedExitCode\":%lu}\n",gpuId,0x47505552UL);
                    Handle ready(CreateFileW(argv[6],GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
                    require(ready.h!=INVALID_HANDLE_VALUE,"publish ready");
                }
                continue;
            }
            pendingEvent=ev; hasPending=true; pendingClassified=false;
            DWORD continuation=DBG_CONTINUE;
            try {
                if(ev.dwDebugEventCode==CREATE_PROCESS_DEBUG_EVENT) {
                    auto& p=attached[ev.dwProcessId];
                    if(!p.handle) p.handle=OpenProcess(PROCESS_ALL_ACCESS,FALSE,ev.dwProcessId);
                    require(p.handle!=nullptr,"open owned debug process with suspend rights");
                    p.ft=creationTime(p.handle);
                    require(p.ft==creationTime(ev.u.CreateProcessInfo.hProcess),"event identity");
                    if(!p.threads[ev.dwThreadId].handle)
                        p.threads[ev.dwThreadId].handle=ownedThread(ev.dwProcessId,ev.dwThreadId);
                    if(ev.u.CreateProcessInfo.hFile) { CloseHandle(ev.u.CreateProcessInfo.hFile); pendingEvent.u.CreateProcessInfo.hFile=nullptr; }
                    if(fault==L"child" && ev.dwProcessId!=rootId) throw std::runtime_error("injected child inspection failure");
                    p.gpu=ChromiumArguments::IsCompatibleGpu(ChromiumArguments::Parse(cmdline(p.handle)));
                    if(p.gpu) {
                        initialThreadIds[ev.dwProcessId]=ev.dwThreadId;
                        PROCESS_BASIC_INFORMATION childInfo{};
                        require(gpuFt<=p.ft && _wcsicmp(imagePath(p.handle).c_str(),argv[10])==0 && query(p.handle,0,&childInfo,sizeof childInfo,nullptr)>=0 && childInfo.InheritedFromUniqueProcessId==rootId,"replacement GPU ownership");
                        ++newGpus;
                    }
                    printf("{\"kind\":\"process\",\"pid\":%lu,\"creationFileTime\":\"%llu\",\"gpu\":%s}\n",ev.dwProcessId,(unsigned long long)p.ft,p.gpu?"true":"false");

                } else {
                    auto it=attached.find(ev.dwProcessId); require(it!=attached.end(),"unknown event owner"); auto& p=it->second;
                    auto rearmMissingInitial=[&]() {
                        if(!p.gpu || !p.initialBreak || !p.entries[0]) return;
                        auto owner=initialThreadIds.find(ev.dwProcessId);
                        require(owner!=initialThreadIds.end(),"missing initial thread identity");
                        auto thread=p.threads.find(owner->second);
                        if(thread==p.threads.end()) return;
                        CONTEXT context{}; context.ContextFlags=CONTEXT_DEBUG_REGISTERS;
                        require(GetThreadContext(thread->second.handle,&context),"read startup thread DR");
                        bool intact=(context.Dr7&0xff)==0x55;
                        std::array<uint64_t,4> installed{context.Dr0,context.Dr1,context.Dr2,context.Dr3};
                        intact &= installed==p.entries;
                        if(intact) return;
                        arm(thread->second,p.entries,true);
                        ++startupRearms;
                        printf("{\"kind\":\"startupRearm\",\"pid\":%lu,\"tid\":%lu,\"beforeDr7\":\"%llu\"}\n",
                            ev.dwProcessId,owner->second,(unsigned long long)context.Dr7);
                    };
                    switch(ev.dwDebugEventCode) {
                    case CREATE_THREAD_DEBUG_EVENT:
                        p.threads[ev.dwThreadId].handle=ownedThread(ev.dwProcessId,ev.dwThreadId);
                        if(p.entries[0]) arm(p.threads.at(ev.dwThreadId),p.entries);
                        rearmMissingInitial();
                        break;
                    case EXIT_THREAD_DEBUG_EVENT: {
                        auto t=p.threads.find(ev.dwThreadId);
                        if(t!=p.threads.end()) { CloseHandle(t->second.handle); p.threads.erase(t); } break;
                    }
                    case LOAD_DLL_DEBUG_EVENT: {
                        auto path=dllPath(ev.u.LoadDll.hFile); if(ev.u.LoadDll.hFile) { CloseHandle(ev.u.LoadDll.hFile); pendingEvent.u.LoadDll.hFile=nullptr; }
                        if(p.gpu && _wcsicmp(path.c_str(),moduleName)==0) {
                            for(size_t i=0;i<4;++i) {
                                p.entries[i]=(uint64_t)ev.u.LoadDll.lpBaseOfDll+local[i]-(uint64_t)module;
                                unsigned char bytes[16]{}; SIZE_T n=0;
                                require(ReadProcessMemory(p.handle,(void*)p.entries[i],bytes,16,&n)&&n==16&&memcmp(bytes,(void*)local[i],16)==0,"remote DXGI entry mismatch");
                            }
                            for(auto& t:p.threads) arm(t.second,p.entries);
                            if(fault==L"event") throw std::runtime_error("injected event failure after arming");
                            if(fault==L"deadline") deadline=GetTickCount64();
                            PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY policy{};
                            require(GetProcessMitigationPolicy(p.handle,ProcessSignaturePolicy,&policy,sizeof policy),"replacement policy");
                            printf("{\"kind\":\"armed\",\"pid\":%lu,\"signaturePolicyAtLoad\":%lu,\"targetLuid\":\"%llu\"}\n",ev.dwProcessId,policy.Flags,(unsigned long long)luidBits(target));
                        }
                        rearmMissingInitial();
                        break;
                    }
                    case EXCEPTION_DEBUG_EVENT: {
                        auto code=ev.u.Exception.ExceptionRecord.ExceptionCode;
                        if(code==EXCEPTION_SINGLE_STEP && p.entries[0]) {
                            auto t=p.threads.find(ev.dwThreadId); require(t!=p.threads.end(),"breakpoint thread");
                            CONTEXT c{}; c.ContextFlags=CONTEXT_ALL; require(GetThreadContext(t->second.handle,&c),"entry context");
                            int slot=-1; for(int i=0;i<4;++i) if((c.Dr6&(1u<<i))&&!(c.Dr6&(1ULL<<14))&&c.Rip==p.entries[i]) { slot=i; break; }
                            if(slot<0) { continuation=DBG_EXCEPTION_NOT_HANDLED; break; }
                            ++observed; require(observed<2000,"hit bound"); auto old=c.Rdx;
                            if(fault==L"pending") throw std::runtime_error("injected pending breakpoint failure");
#ifdef RM_EXTERNAL_FIXTURE
                            if(fault==L"pending-hold") { rejectHoldThread=ev.dwThreadId; throw std::runtime_error("injected pending breakpoint with rejected hold"); }
#endif
                            // Keep explicit LUID requests on their original adapter; the display path may still need it.
                            if(slot==2) {
                                c.Rdx=old;
                                ++filtered;
                            } else {
                                require(slot!=3 || c.R8<preferenceIndices.size(),"unknown GPU preference");
                                UINT index=slot==3?preferenceIndices[c.R8]:targetIndex;
                                c.Rdx=UINT(old)==0?index:UINT_MAX;
                                ++filtered;
                            } c.EFlags|=0x10000; c.Dr6=0;
                            require(SetThreadContext(t->second.handle,&c),"redirect enumeration");
                            printf("{\"kind\":\"enum\",\"pid\":%lu,\"slot\":%d,\"oldArgument\":\"%llu\",\"newArgument\":\"%llu\"}\n",ev.dwProcessId,slot,(unsigned long long)old,(unsigned long long)c.Rdx);
                        } else if(code==EXCEPTION_BREAKPOINT && !p.initialBreak) {
                            if(fault==L"continue-cleanup") throw std::runtime_error("injected transfer of initial breakpoint to cleanup");
                            p.initialBreak=true; if(ev.dwProcessId==rootId) rootInitialBreak=true;
                        } else continuation=DBG_EXCEPTION_NOT_HANDLED;
                        break;
                    }
                    case EXIT_PROCESS_DEBUG_EVENT:
                        printf("{\"kind\":\"exit\",\"pid\":%lu,\"code\":%lu}\n",ev.dwProcessId,ev.u.ExitProcess.dwExitCode);
                        for(auto& t:p.threads) CloseHandle(t.second.handle);
                        initialThreadIds.erase(ev.dwProcessId);
                        CloseHandle(p.handle); attached.erase(it); require(ev.dwProcessId!=rootId,"browser exited"); break;
                    default: break;
                    }
                }
            } catch(...) { throw; }
            pendingDisposition=continuation; pendingClassified=true;
            if(fault==L"continue" && ev.dwDebugEventCode==EXCEPTION_DEBUG_EVENT && ev.u.Exception.ExceptionRecord.ExceptionCode==EXCEPTION_BREAKPOINT)
                throw std::runtime_error("injected normal continuation failure");
            require(ContinueDebugEvent(ev.dwProcessId,ev.dwThreadId,continuation),"continue debug event");
            hasPending=false;
        }
        require(GetFileAttributesW(argv[5])!=INVALID_FILE_ATTRIBUTES,"action deadline");
        require(switched && filtered>0 && newGpus>0,"no replacement interception");
    } catch(const std::exception& e) { error=e.what(); }


    // Every exit reaches the same cleanup owner, including a still-pending debug event.
    bool recoverable=attachedRoot;
    bool cleanupOk=!attachedRoot;
    while(recoverable && !cleanupOk) {
        try {
            cleanupSession(attached,rootId,root.h,originalFlags,pendingEvent,hasPending,pendingDisposition,pendingClassified,restoreFault,continueFault);
            cleanupOk=true;
        } catch(const std::exception& ex) {
            cleaned=false;
            printf("{\"kind\":\"recoveryRequired\",\"error\":%s}\n",quote(ex.what()).c_str());
            // Retain handles and debug ownership. Only an explicit cleanup request may retry.
            // This command never retries migration or terminates a surviving user process.
            std::wstring recover=std::wstring(argv[5])+L".recover";
            DWORD lastFailure=0;
            while(true) {
                if(GetFileAttributesW(recover.c_str())!=INVALID_FILE_ATTRIBUTES) {
                    if(DeleteFileW(recover.c_str())) break;
                    DWORD failure=GetLastError();
                    if(failure!=lastFailure) printf("{\"kind\":\"recoveryRequestRejected\",\"win32\":%lu}\n",failure);
                    lastFailure=failure;
                }
                Sleep(100);
            }
        }
    }
    if(!cleanupOk) cleaned=false;
    for(auto& pair:attached) {
        for(auto& t:pair.second.threads) if(t.second.handle) CloseHandle(t.second.handle);
        if(pair.second.handle) CloseHandle(pair.second.handle);
    }
    BOOL debugger=TRUE; ULONG flags=0;
    bool rootAlive=root.h && WaitForSingleObject(root.h,0)==WAIT_TIMEOUT;
    bool rootRestored=rootAlive && CheckRemoteDebuggerPresent(root.h,&debugger)&&!debugger && query(root.h,31,&flags,sizeof flags,nullptr)>=0 && flags==originalFlags;
    DWORD gpuExit=STILL_ACTIVE; if(oldGpu.h) GetExitCodeProcess(oldGpu.h,&gpuExit);
    bool passed=error.empty()&&cleaned&&rootRestored;
    printf("{\"kind\":\"summary\",\"passed\":%s,\"cleanupPassed\":%s,\"browserAlive\":%s,\"rootRestored\":%s,\"originalGpuExit\":%lu,\"observed\":%u,\"filtered\":%u,\"startupRearms\":%u,\"newGpuProcesses\":%u,\"error\":%s}\n",passed?"true":"false",cleaned?"true":"false",rootAlive?"true":"false",rootRestored?"true":"false",gpuExit,observed,filtered,startupRearms,newGpus,quote(error).c_str());
    return passed?0:1;
}
