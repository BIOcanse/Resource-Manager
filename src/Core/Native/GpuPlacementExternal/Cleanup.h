#pragma once
#include "Controller.h"

static void cleanupSession(std::map<DWORD,Attached>& attached,DWORD rootId,HANDLE root,
    ULONG originalFlags,DEBUG_EVENT& ev,bool& pending,DWORD& savedDisposition,bool& classified,bool& restoreFault,bool& continueFault) {
    // A pending exit can keep its kernel object unsignalled until it is continued.
    if(pending && (ev.dwDebugEventCode==EXIT_THREAD_DEBUG_EVENT || ev.dwDebugEventCode==EXIT_PROCESS_DEBUG_EVENT)) {
        auto it=attached.find(ev.dwProcessId);
        if(it!=attached.end()) {
            if(ev.dwDebugEventCode==EXIT_PROCESS_DEBUG_EVENT) {
                for(auto& t:it->second.threads) CloseHandle(t.second.handle);
                CloseHandle(it->second.handle); attached.erase(it);
            } else {
                auto ti=it->second.threads.find(ev.dwThreadId);
                if(ti!=it->second.threads.end()) { CloseHandle(ti->second.handle); it->second.threads.erase(ti); }
            }
        }
        require(ContinueDebugEvent(ev.dwProcessId,ev.dwThreadId,DBG_CONTINUE)!=0,"settle pending exit");
        pending=false;
        if(ev.dwDebugEventCode==EXIT_PROCESS_DEBUG_EVENT && ev.dwProcessId==rootId)
            require(WaitForSingleObject(root,2000)==WAIT_OBJECT_0,"complete continued browser exit");
    }
    // Holds prevent additional user code while queued debug events are drained.
    for(auto& pair:attached) if(pair.second.handle) hold(pair.second,pair.first);
    for(auto& pair:attached) if(pair.second.handle) disarm(pair.second,pair.first,restoreFault);
    if(live(root)) {
        ULONG actual=0;
        require(set(root,31,&originalFlags,sizeof originalFlags)>=0 &&
            query(root,31,&actual,sizeof actual,nullptr)>=0 && actual==originalFlags,"restore child debug inheritance");
    }
    auto deadline=GetTickCount64()+5000;
    bool stillAttached=false;
    for(auto& pair:attached) if(!pair.second.detached && pair.second.handle && live(pair.second.handle)) stillAttached=true;
    while(pending || stillAttached) {
        require(GetTickCount64()<deadline,"cleanup event drain deadline");
        if(!pending) {
            if(!WaitForDebugEvent(&ev,20)) {
                require(GetLastError()==ERROR_SEM_TIMEOUT,"cleanup debug wait");
                break;
            }
            pending=true;
            classified=false;
        }
        DWORD continuation=DBG_CONTINUE;
        if(ev.dwDebugEventCode==CREATE_PROCESS_DEBUG_EVENT) {
            // Register each acquired handle before any inspection that can throw.
            auto& p=attached[ev.dwProcessId];
            if(!p.handle) p.handle=OpenProcess(PROCESS_ALL_ACCESS,FALSE,ev.dwProcessId);
            require(p.handle!=nullptr,"cleanup child handle");
            p.ft=creationTime(p.handle);
            auto& t=p.threads[ev.dwThreadId];
            if(!t.handle) t.handle=ownedThread(ev.dwProcessId,ev.dwThreadId);
            if(ev.u.CreateProcessInfo.hFile) { CloseHandle(ev.u.CreateProcessInfo.hFile); ev.u.CreateProcessInfo.hFile=nullptr; }
            hold(p,ev.dwProcessId);
            disarm(p,ev.dwProcessId,restoreFault);
        } else {
            auto it=attached.find(ev.dwProcessId);
            // A process exit may already have been consumed by normal handling before its error.
            if(it==attached.end()) require(ev.dwDebugEventCode==EXIT_PROCESS_DEBUG_EVENT,"cleanup event owner");
            else {
                auto& p=it->second;
                switch(ev.dwDebugEventCode) {
                case CREATE_THREAD_DEBUG_EVENT: {
                    auto& t=p.threads[ev.dwThreadId];
                    if(!t.handle) t.handle=ownedThread(ev.dwProcessId,ev.dwThreadId);
                    hold(p,ev.dwProcessId);
                    break;
                }
                case LOAD_DLL_DEBUG_EVENT:
                    if(ev.u.LoadDll.hFile) { CloseHandle(ev.u.LoadDll.hFile); ev.u.LoadDll.hFile=nullptr; }
                    break;
                case EXCEPTION_DEBUG_EVENT: {
                    auto code=ev.u.Exception.ExceptionRecord.ExceptionCode;
                    if(code==EXCEPTION_SINGLE_STEP) {
                        auto ti=p.threads.find(ev.dwThreadId);
                        if(ti!=p.threads.end() && ti->second.armed)
                            require(ti->second.captured,"retain armed trap until pre-restoration context is captured");
                    }
                    if(classified) { continuation=savedDisposition; break; }
                    continuation=DBG_EXCEPTION_NOT_HANDLED;
                    if(code==EXCEPTION_BREAKPOINT && !p.initialBreak) {
                        p.initialBreak=true; continuation=DBG_CONTINUE;
                    } else if(code==EXCEPTION_SINGLE_STEP) {
                        auto ti=p.threads.find(ev.dwThreadId);
                        if(ti!=p.threads.end() && ti->second.armed) {
                            CONTEXT c{}; c.ContextFlags=CONTEXT_ALL;
                            require(GetThreadContext(ti->second.handle,&c)!=0,"read pending trap");
                            bool ownedTrap=ti->second.pendingTrapAddress && c.Rip==ti->second.pendingTrapAddress
                                && reinterpret_cast<uint64_t>(ev.u.Exception.ExceptionRecord.ExceptionAddress)==c.Rip;
                            const auto& evidence=ti->second.beforeRestore;
                            const auto& entries=ti->second.perThreadEntries?ti->second.installedEntries:p.entries;
                            for(size_t slot=0;slot<entries.size();++slot)
                                if(ti->second.captured && entries[slot] && c.Rip==entries[slot] &&
                                    evidence.Rip==c.Rip && (evidence.Dr6&(1ULL<<slot)) && !(evidence.Dr6&(1ULL<<14))) ownedTrap=true;
                            if(ownedTrap) {
                                const auto& old=ti->second.original;
                                c.Dr0=old.Dr0; c.Dr1=old.Dr1; c.Dr2=old.Dr2;
                                c.Dr3=old.Dr3; c.Dr6=old.Dr6; c.Dr7=old.Dr7;
                                c.EFlags|=0x10000;
                                require(SetThreadContext(ti->second.handle,&c)!=0,"settle owned pending trap");
                                CONTEXT check{}; check.ContextFlags=CONTEXT_DEBUG_REGISTERS;
                                require(GetThreadContext(ti->second.handle,&check) && sameDr(check,old),"verify pending trap restored DR");
                                ti->second.pendingTrapAddress=0;
                                continuation=DBG_CONTINUE;
                                printf("{\"kind\":\"pendingBreakpointDrained\",\"pid\":%lu,\"tid\":%lu}\n",ev.dwProcessId,ev.dwThreadId);
                            }
                        }
                    }
                    break;
                }
                case EXIT_THREAD_DEBUG_EVENT: {
                    auto ti=p.threads.find(ev.dwThreadId);
                    if(ti!=p.threads.end()) { CloseHandle(ti->second.handle); p.threads.erase(ti); }
                    hold(p,ev.dwProcessId);
                    disarm(p,ev.dwProcessId,restoreFault);
                    break;
                }
                case EXIT_PROCESS_DEBUG_EVENT:
                    printf("{\"kind\":\"cleanupExit\",\"pid\":%lu,\"code\":%lu}\n",ev.dwProcessId,ev.u.ExitProcess.dwExitCode);
                    break;
                default: break;
                }
            }
        }
        savedDisposition=continuation; classified=true;
        if(continueFault && ev.dwDebugEventCode==EXCEPTION_DEBUG_EVENT && ev.u.Exception.ExceptionRecord.ExceptionCode==EXCEPTION_BREAKPOINT) {
            continueFault=false; throw std::runtime_error("injected cleanup continuation failure");
        }
        require(ContinueDebugEvent(ev.dwProcessId,ev.dwThreadId,continuation)!=0,"continue cleanup event");
        printf("{\"kind\":\"cleanupContinue\",\"pid\":%lu,\"event\":%lu,\"disposition\":%lu}\n",ev.dwProcessId,ev.dwDebugEventCode,continuation);
        pending=false;
    }
    std::vector<DWORD> order;
    if(attached.count(rootId)) order.push_back(rootId);
    for(auto& pair:attached) if(pair.first!=rootId) order.push_back(pair.first);
    for(auto pid:order) {
        auto& p=attached.at(pid);
        if(p.detached || !live(p.handle)) continue;
        require(p.handle!=nullptr && p.suspended,"cleanup holds incomplete after lifecycle drain");
        for(auto& t:p.threads) require(t.second.handle && (!live(t.second.handle) || t.second.held),"unacquired or unheld survivor");
        disarm(p,pid,restoreFault);
    }
    // All producers remain held until every surviving process has detached.
    for(auto pid:order) {
        auto& p=attached.at(pid);
        if(p.detached || !live(p.handle)) continue;
        require(p.restored && p.suspended,"detach requires verified restoration");
        require(DebugActiveProcessStop(pid)!=0,"detach restored process");
        p.detached=true;
        printf("{\"kind\":\"detach\",\"pid\":%lu,\"passed\":true}\n",pid);
    }
    for(auto pid:order) {
        auto& p=attached.at(pid);
        if(!live(p.handle)) continue;
        BOOL debug=TRUE;
        require(p.detached && CheckRemoteDebuggerPresent(p.handle,&debug) && !debug,"independent debugger absence");
        if(p.suspended) {
            for(auto& pair:p.threads) {
                auto& t=pair.second;
                if(!t.held) continue;
                if(live(t.handle)) {
                    DWORD previous=ResumeThread(t.handle);
                    require(previous!=DWORD(-1) && previous>0,"balance exact owned thread suspension");
                    printf("{\"kind\":\"threadResume\",\"pid\":%lu,\"tid\":%lu,\"previousCount\":%lu}\n",pid,pair.first,previous);
                }
                t.held=false;
            }
            p.suspended=false;
        }
        printf("{\"kind\":\"resumed\",\"pid\":%lu,\"creationFileTime\":\"%llu\",\"passed\":true,\"debuggerAbsent\":true}\n",pid,(unsigned long long)p.ft);
    }
}
