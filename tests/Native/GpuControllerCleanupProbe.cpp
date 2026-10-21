#include "../../src/Core/Native/GpuPlacementExternal/Cleanup.h"
#include "../../src/Core/Native/GpuPlacementCommon/ControllerOutputPipes.h"

// Compile as x64 with a Windows GUI subsystem (for Zig: -municode
// '-Wl,--subsystem,windows'); a console subsystem may add a console host.
// Only debug this executable's own child. Closing the job on any failed
// assertion terminates that child; no user application or GPU is involved.
static bool exists(const std::wstring& path) {
    return GetFileAttributesW(path.c_str())!=INVALID_FILE_ATTRIBUTES;
}
static void signal(const std::wstring& path) {
    Handle file(CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr));
    require(file.h!=INVALID_HANDLE_VALUE,"create probe signal");
}
static int target(const std::wstring& directory) {
    const auto deadline=GetTickCount64()+20000;
    while(!exists(directory+L"\\target.release")) {
        if(GetTickCount64()>=deadline) return 6;
        Sleep(20);
    }
    signal(directory+L"\\target.completed");
    return 37;
}
static void run(const std::wstring& mode,const std::wstring& directory) {
    require(mode==L"restore" || mode==L"continue","probe fault mode");
    query=reinterpret_cast<NtQuery>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"NtQueryInformationProcess"));
    set=reinterpret_cast<NtSet>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"NtSetInformationProcess"));
    require(query && set,"native process flag functions");
    wchar_t image[32768]{};
    require(GetModuleFileNameW(nullptr,image,32768)>0,"probe image");
    Handle job(CreateJobObjectW(nullptr,nullptr));
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    require(job.h && SetInformationJobObject(job.h,JobObjectExtendedLimitInformation,&limits,sizeof limits),"probe job");
    auto command=L"\""+std::wstring(image)+L"\" --cleanup-target \""+directory+L"\"";
    STARTUPINFOW startup{}; startup.cb=sizeof startup;
    PROCESS_INFORMATION created{};
    require(CreateProcessW(image,command.data(),nullptr,nullptr,FALSE,
        DEBUG_ONLY_THIS_PROCESS|CREATE_NO_WINDOW,nullptr,directory.c_str(),&startup,&created),"create owned debug target");
    Handle process(created.hProcess), initialThread(created.hThread);
    require(AssignProcessToJobObject(job.h,process.h)!=0,"assign owned debug target");
    require(DebugSetProcessKillOnExit(FALSE)!=0,"retain owned target during cleanup recovery");
    ULONG originalFlags=0;
    require(query(process.h,31,&originalFlags,sizeof originalFlags,nullptr)>=0,"read original process debug flags");

    std::map<DWORD,Attached> attached;
    DEBUG_EVENT event{}; bool pending=false,classified=false;
    DWORD disposition=DBG_CONTINUE;
    const auto attachDeadline=GetTickCount64()+8000;
    for(;;) {
        require(GetTickCount64()<attachDeadline,"owned initial breakpoint deadline");
        if(!WaitForDebugEvent(&event,100)) {
            require(GetLastError()==ERROR_SEM_TIMEOUT,"owned initial debug wait");
            continue;
        }
        if(event.dwProcessId!=created.dwProcessId) {
            std::fprintf(stderr,"unexpected debug owner: code=%lu actual=%lu owned=%lu\n",
                event.dwDebugEventCode,event.dwProcessId,created.dwProcessId);
        }
        require(event.dwProcessId==created.dwProcessId,"debug event belongs to owned target");
        pending=true;
        auto& owner=attached[event.dwProcessId];
        if(event.dwDebugEventCode==CREATE_PROCESS_DEBUG_EVENT) {
            owner.handle=owned(process.h); owner.ft=creationTime(process.h);
            owner.threads[event.dwThreadId].handle=ownedThread(event.dwProcessId,event.dwThreadId);
            if(event.u.CreateProcessInfo.hFile) CloseHandle(event.u.CreateProcessInfo.hFile);
        } else if(event.dwDebugEventCode==CREATE_THREAD_DEBUG_EVENT) {
            owner.threads[event.dwThreadId].handle=ownedThread(event.dwProcessId,event.dwThreadId);
        } else if(event.dwDebugEventCode==LOAD_DLL_DEBUG_EVENT) {
            if(event.u.LoadDll.hFile) CloseHandle(event.u.LoadDll.hFile);
        } else if(event.dwDebugEventCode==EXCEPTION_DEBUG_EVENT) {
            require(event.u.Exception.ExceptionRecord.ExceptionCode==EXCEPTION_BREAKPOINT,"owned initial breakpoint");
            break; // Cleanup must classify and settle the actual queued event.
        } else require(event.dwDebugEventCode!=EXIT_PROCESS_DEBUG_EVENT,"owned target survived startup");
        require(ContinueDebugEvent(event.dwProcessId,event.dwThreadId,DBG_CONTINUE)!=0,"continue owned startup event");
        pending=false;
    }
    auto& owner=attached.at(created.dwProcessId);
    // Wait until startup is stopped before changing inheritance; otherwise
    // Windows may include its console host in the fixture's debug session.
    ULONG changedFlags=originalFlags^1;
    require(set(process.h,31,&changedFlags,sizeof changedFlags)>=0,"change owned process debug flags");
    require(SuspendThread(initialThread.h)==0,"fixture adds one preexisting suspension");
    // Unmapped low addresses cannot execute in this ordinary test child.
    // These are actual thread debug registers, not graphics hardware writes.
    owner.entries={0x1000,0x2000,0x3000,0x4000};
    for(auto& pair:owner.threads) arm(pair.second,owner.entries);
    bool restoreFault=mode==L"restore",continueFault=mode==L"continue";
    bool failed=false;
    try {
        cleanupSession(attached,created.dwProcessId,process.h,originalFlags,event,pending,
            disposition,classified,restoreFault,continueFault);
    } catch(const std::runtime_error& error) {
        const auto expected=mode==L"restore"?"injected restoration failure":"injected cleanup continuation failure";
        require(std::string(error.what()).find(expected)!=std::string::npos,"expected cleanup failure");
        failed=true;
    }
    BOOL debug=FALSE;
    require(failed && owner.suspended && !owner.detached && pending,"failed cleanup retains ownership and holds");
    require(CheckRemoteDebuggerPresent(process.h,&debug) && debug,"failed cleanup retains debugger");
    signal(directory+L"\\cleanup.failed");
    const auto recoveryDeadline=GetTickCount64()+12000;
    while(!exists(directory+L"\\stop.recover")) {
        require(GetTickCount64()<recoveryDeadline,"owned recovery signal deadline");
        Sleep(20);
    }
    cleanupSession(attached,created.dwProcessId,process.h,originalFlags,event,pending,
        disposition,classified,restoreFault,continueFault);
    ULONG actualFlags=0;
    require(query(process.h,31,&actualFlags,sizeof actualFlags,nullptr)>=0 && actualFlags==originalFlags,
        "independent restored process debug flags");
    require(CheckRemoteDebuggerPresent(process.h,&debug) && !debug && owner.detached && !owner.suspended && !pending,
        "independent completed debugger cleanup");
    for(auto& pair:owner.threads) {
        auto& thread=pair.second;
        const DWORD expected=pair.first==created.dwThreadId?1:0;
        require(SuspendThread(thread.handle)==expected,"cleanup balanced only its own suspension");
        CONTEXT actual{}; actual.ContextFlags=CONTEXT_DEBUG_REGISTERS;
        require(GetThreadContext(thread.handle,&actual) && sameDr(actual,thread.original),"independent restored debug registers");
        require(ResumeThread(thread.handle)==expected+1,"balance independent inspection suspension");
    }
    require(ResumeThread(initialThread.h)==1,"release fixture suspension preserved by cleanup");
    signal(directory+L"\\target.release");
    require(WaitForSingleObject(process.h,5000)==WAIT_OBJECT_0,"owned target continues and exits");
    DWORD exitCode=0;
    require(GetExitCodeProcess(process.h,&exitCode) && exitCode==37 && exists(directory+L"\\target.completed"),
        "owned target completed normally after recovery");
    for(auto& pair:attached) {
        for(auto& thread:pair.second.threads) CloseHandle(thread.second.handle);
        CloseHandle(pair.second.handle);
    }
    std::puts("owned-cleanup-recovery-passed: registers, flags, debugger absence, exact suspension balance and normal exit");
}
int wmain(int argc,wchar_t** argv) {
    try {
        if(argc==3 && std::wstring(argv[1])==L"--cleanup-target") return target(argv[2]);
        if(!ResourceManagerGpuController::ConfigureOutput(argc,argv)) return 7;
        require(argc==4 && std::wstring(argv[1])==L"--cleanup-probe","probe arguments");
        run(argv[2],argv[3]);
        return 0;
    } catch(const std::exception& error) {
        std::fprintf(stderr,"owned cleanup probe: %s\n",error.what());
        return 1;
    }
}
