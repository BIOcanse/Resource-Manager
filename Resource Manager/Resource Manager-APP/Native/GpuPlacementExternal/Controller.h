#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <dxgi1_6.h>
#include <winternl.h>
#include <array>
#include <set>
#include <map>
#include <vector>
#include <string>
#include <stdexcept>
#include <cstdio>
#include <cstdint>
#include <climits>
#include <algorithm>
static void require(bool ok,const char* message) {
    if(!ok) throw std::runtime_error(std::string(message)+" win32="+std::to_string(GetLastError()));
}
template<class T> struct Com {
    T* p=nullptr;
    ~Com() { if(p) p->Release(); }
    T** out() { return &p; }
    T* operator->() const { return p; }
};
struct Handle {
    HANDLE h=nullptr;
    explicit Handle(HANDLE value=nullptr):h(value) {}
    ~Handle() { if(h && h!=INVALID_HANDLE_VALUE) CloseHandle(h); }
    Handle(const Handle&)=delete;
    Handle& operator=(const Handle&)=delete;
};
struct Thread {
    HANDLE handle=nullptr; CONTEXT original{}; bool armed=false;
    std::array<uint64_t,4> installedEntries{};
    uint64_t pendingTrapAddress=0;
    bool perThreadEntries=false;
};
static uint64_t creationTime(HANDLE h) {
    FILETIME c{},e{},k{},u{};
    require(GetProcessTimes(h,&c,&e,&k,&u),"process times");
    return (uint64_t(c.dwHighDateTime)<<32)|c.dwLowDateTime;
}
static bool equal(LUID a,LUID b) { return a.LowPart==b.LowPart && a.HighPart==b.HighPart; }
static uint64_t luidBits(LUID a) { return (uint64_t(uint32_t(a.HighPart))<<32)|a.LowPart; }
static std::string quote(const std::string& s) {
    std::string r="\"";
    for(unsigned char c:s) {
        if(c=='\\' || c=='"') { r+='\\'; r+=c; }
        else if(c<32) { char b[8]; snprintf(b,sizeof b,"\\u%04x",c); r+=b; }
        else r+=c;
    }
    return r+"\"";
}
static uint64_t number(const wchar_t* s,uint64_t maximum) {
    require(s && *s,"missing number");
    uint64_t value=0;
    for(;*s;++s) {
        require(*s>=L'0' && *s<=L'9',"invalid number");
        auto digit=uint64_t(*s-L'0');
        require(value<=(maximum-digit)/10,"number overflow");
        value=value*10+digit;
    }
    require(value!=0,"zero identity or deadline");
    return value;
}
using NtQuery = LONG(WINAPI*)(HANDLE,ULONG,PVOID,ULONG,PULONG);
using NtSet = LONG(WINAPI*)(HANDLE,ULONG,PVOID,ULONG);
static NtQuery query;
static NtSet set;
static HANDLE owned(HANDLE borrowed) {
    HANDLE h=nullptr;
    require(DuplicateHandle(GetCurrentProcess(),borrowed,GetCurrentProcess(),&h,0,FALSE,DUPLICATE_SAME_ACCESS),"duplicate event handle");
    return h;
}
static std::wstring cmdline(HANDLE h) {
    ULONG n=0; query(h,60,nullptr,0,&n);
    require(n>0 && n<65536,"command length");
    std::vector<unsigned char> b(n);
    require(query(h,60,b.data(),n,&n)>=0,"command query");
    auto s=(UNICODE_STRING*)b.data(); auto p=(uintptr_t)s->Buffer;
    require(p>=(uintptr_t)b.data() && p+s->Length<=(uintptr_t)b.data()+b.size(),"command bounds");
    return std::wstring(s->Buffer,s->Length/2);
}
static std::wstring imagePath(HANDLE h) {
    wchar_t path[32768]{}; DWORD size=32768;
    require(QueryFullProcessImageNameW(h,0,path,&size),"image path"); return path;
}
static std::wstring dllPath(HANDLE h) {
    wchar_t b[32768]{};
    if(!h || !GetFinalPathNameByHandleW(h,b,32768,FILE_NAME_NORMALIZED)) return {};
    std::wstring s=b; if(s.rfind(L"\\\\?\\",0)==0) s.erase(0,4); return s;
}
struct ControlledThread : Thread {
    bool held=false, captured=false;
    CONTEXT beforeRestore{};
};
#ifdef RM_EXTERNAL_FIXTURE
static DWORD rejectAcquireThread=0, rejectHoldThread=0;
#endif
static HANDLE ownedThread(DWORD pid,DWORD tid) {
#ifdef RM_EXTERNAL_FIXTURE
    if(rejectAcquireThread==tid) { rejectAcquireThread=0; throw std::runtime_error("injected thread acquisition failure"); }
#endif
    HANDLE h=OpenThread(THREAD_GET_CONTEXT|THREAD_SET_CONTEXT|THREAD_SUSPEND_RESUME|THREAD_QUERY_INFORMATION|SYNCHRONIZE,FALSE,tid);
    require(h!=nullptr,"open owned thread");
    if(GetProcessIdOfThread(h)!=pid || GetThreadId(h)!=tid) { CloseHandle(h); throw std::runtime_error("thread owner mismatch"); }
    return h;
}
struct Attached {
    HANDLE handle=nullptr;
    uint64_t ft=0;
    bool gpu=false, initialBreak=false, suspended=false, detached=false;
    bool restored=false;
    std::array<uint64_t,4> entries{};
    std::map<DWORD,ControlledThread> threads;
};
static void rememberOwnedTrap(Thread& t,const CONTEXT& c) {
    if(t.armed && !(c.Dr6&(1ULL<<14))) {
        for(size_t i=0;i<t.installedEntries.size();++i) {
            if(t.installedEntries[i] && c.Rip==t.installedEntries[i] && (c.Dr6&(1ULL<<i))) {
                require(!t.pendingTrapAddress || t.pendingTrapAddress==c.Rip,"unsettled previous trap");
                t.pendingTrapAddress=c.Rip;
            }
        }
    }
}
static void arm(Thread& t,const std::array<uint64_t,4>& a,bool trackChanges=false) {
    CONTEXT c{}; c.ContextFlags=trackChanges?CONTEXT_ALL:CONTEXT_DEBUG_REGISTERS;
    require(GetThreadContext(t.handle,&c),"read DR");
    if(!t.armed) { require((c.Dr7&255)==0,"existing hardware breakpoints"); t.original=c; }
    // A stopped thread may already own a queued trap from the previous table.
    if(trackChanges) rememberOwnedTrap(t,c);
    c.Dr0=a[0]; c.Dr1=a[1]; c.Dr2=a[2]; c.Dr3=a[3];
    c.Dr6=0; c.Dr7=(c.Dr7&~0xffff00ffULL)|0x55;
    c.ContextFlags=CONTEXT_DEBUG_REGISTERS;
    require(SetThreadContext(t.handle,&c),"arm DR"); t.armed=true; t.installedEntries=a; t.perThreadEntries=trackChanges;
}

static bool live(HANDLE h) {
    DWORD state=WaitForSingleObject(h,0);
    require(state==WAIT_TIMEOUT || state==WAIT_OBJECT_0,"process liveness");
    return state==WAIT_TIMEOUT;
}
static void hold(Attached& p,DWORD pid) {
    if(!live(p.handle) || p.detached) return;
    bool complete=true;
    for(auto& pair:p.threads) {
        auto& t=pair.second;
        if(!t.handle) { complete=false; continue; }
        if(t.held || !live(t.handle)) continue;
        DWORD previous;
#ifdef RM_EXTERNAL_FIXTURE
        if(rejectHoldThread==pair.first) { rejectHoldThread=0; SetLastError(ERROR_ACCESS_DENIED); previous=DWORD(-1); }
        else
#endif
            previous=SuspendThread(t.handle);
        if(previous==DWORD(-1)) {
            // An exit event can be queued while the thread object is still nonsignalled.
            // Keep the ledger and let that event progress before deciding cleanup failed.
            complete=false;
            printf("{\"kind\":\"threadHoldDeferred\",\"pid\":%lu,\"tid\":%lu,\"win32\":%lu}\n",pid,pair.first,GetLastError());
            continue;
        }
        t.held=true;
        printf("{\"kind\":\"threadHold\",\"pid\":%lu,\"tid\":%lu,\"previousCount\":%lu}\n",pid,pair.first,previous);
    }
    p.suspended=complete;
}
static bool sameDr(const CONTEXT& a,const CONTEXT& b) {
    return a.Dr0==b.Dr0 && a.Dr1==b.Dr1 && a.Dr2==b.Dr2 &&
        a.Dr3==b.Dr3 && a.Dr6==b.Dr6 && a.Dr7==b.Dr7;
}
static void disarm(Attached& p,DWORD pid,bool& fault) {
    if(!live(p.handle)) { p.restored=true; return; }
    if(p.restored) return;
    unsigned restored=0; bool complete=true;
    for(auto& pair:p.threads) {
        auto& t=pair.second;
        if(!t.handle) { complete=false; continue; }
        if(!t.armed || !live(t.handle)) continue;
        if(!t.held) { complete=false; continue; }
        if(!t.captured) {
            t.beforeRestore.ContextFlags=CONTEXT_ALL;
            require(GetThreadContext(t.handle,&t.beforeRestore)!=0,"capture trap before restoring DR");
            t.captured=true;
        }
        if(fault) { fault=false; throw std::runtime_error("injected restoration failure"); }
        CONTEXT c=t.original; c.ContextFlags=CONTEXT_DEBUG_REGISTERS;
        require(SetThreadContext(t.handle,&c)!=0,"restore owned DR");
        CONTEXT actual{}; actual.ContextFlags=CONTEXT_DEBUG_REGISTERS;
        require(GetThreadContext(t.handle,&actual) && sameDr(actual,t.original),"verify restored DR");
        ++restored;
    }
    p.restored=complete;
    printf("{\"kind\":\"restore\",\"pid\":%lu,\"threadsRestored\":%u,\"passed\":%s}\n",pid,restored,complete?"true":"false");
}
