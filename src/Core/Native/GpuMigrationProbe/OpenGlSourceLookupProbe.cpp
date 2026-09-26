#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlCoreRoutes.h"
#include "../GpuPlacementShim/OpenGlWindowRoutes.h"
#include <cstdio>

namespace {
using namespace ResourceManagerOpenGl::Runtime;
unsigned checks{},failures{},resolveCalls{};
PROC resolved{};
void Check(bool passed,const char* name) {
    ++checks;if(!passed)++failures;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n",name,passed?"true":"false");
}
PROC WINAPI Resolve(LPCSTR) {++resolveCalls;SetLastError(812);return resolved;}
HGLRC WINAPI NativeCurrent() {return reinterpret_cast<HGLRC>(1);}
BOOL WINAPI ChooseA(HDC,const int*,const FLOAT*,UINT,int*,UINT*) {SetLastError(11);return TRUE;}
BOOL WINAPI ChooseB(HDC,const int*,const FLOAT*,UINT,int*,UINT*) {SetLastError(12);return FALSE;}
BOOL WINAPI IntsA(HDC,int,int,UINT,const int*,int*) {SetLastError(21);return TRUE;}
BOOL WINAPI IntsB(HDC,int,int,UINT,const int*,int*) {SetLastError(22);return FALSE;}
BOOL WINAPI FloatsA(HDC,int,int,UINT,const int*,FLOAT*) {SetLastError(31);return TRUE;}
BOOL WINAPI FloatsB(HDC,int,int,UINT,const int*,FLOAT*) {SetLastError(32);return FALSE;}
void APIENTRY VendorA(GLenum,GLubyte* value) {*value=1;}
void APIENTRY VendorB(GLenum,GLubyte* value) {*value=2;}
template<typename Route,typename Function>
void VerifyWindow(const char* name,Function a,Function b) {
    Route::original=a;Route::originalAddress=Callback(a);
    resolved=Callback(a);
    Check(Lookup(name)==Callback(&Route::Invoke),"matching-source-window-entry-wrapped");
    resolved=Callback(b);
    Check(Lookup(name)==resolved && GetLastError()==812,"different-source-window-entry-preserved");
    Route::original=nullptr;Route::originalAddress=nullptr;
    Check(Lookup(name)==resolved,"bootstrap-missing-source-window-entry-preserved");
    Route::original=a;Route::originalAddress=Callback(a);
}
}
int main() {
    SetErrorMode(32771);
    FILETIME born{},end{},kernel{},user{};BOOL inJob{};
    if(!GetProcessTimes(GetCurrentProcess(),&born,&end,&kernel,&user) ||
        !IsProcessInJob(GetCurrentProcess(),nullptr,&inJob) || !inJob)return 2;
    std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"errorMode\":%u,\"inJob\":true}\n",GetCurrentProcessId(),
        (static_cast<unsigned long long>(born.dwHighDateTime)<<32)|born.dwLowDateTime,GetErrorMode());
    systemLookup=&Resolve;systemCurrent=&NativeCurrent;originalVendorBytes=&VendorA;
    originalVendorBytesAddress=Callback(&VendorA);
    VerifyWindow<Window_choose>("wglChoosePixelFormatARB",&ChooseA,&ChooseB);
    VerifyWindow<Window_ints>("wglGetPixelFormatAttribivARB",&IntsA,&IntsB);
    VerifyWindow<Window_floats>("wglGetPixelFormatAttribfvARB",&FloatsA,&FloatsB);
    resolved=Callback(&VendorB);
    const auto vendor=Lookup("glGetUnsignedBytevEXT");
    Check(vendor==resolved && GetLastError()==812,"different-source-vendor-entry-preserved");
    GetBytesEntry call{};std::memcpy(&call,&vendor,sizeof(call));GLubyte value{};
    if(call)call(0,&value);
    Check(value==2,"actual-source-vendor-body-called");
    Check(Lookup("unrecognizedNativeEntry")==resolved,"unknown-native-entry-preserved");
    resolved=Callback(&VendorA);
    Check(Lookup("glGetUnsignedBytevEXT")==Callback(&Bytes),"matching-source-vendor-entry-wrapped");
    const auto matching=Lookup("glGetUnsignedBytevEXT");std::memcpy(&call,&matching,sizeof(call));
    value=0;call(0,&value);Check(value==1,"matching-source-vendor-trampoline-called");
    ResourceManagerOpenGl::IcdExports driver{};driver.getProcAddress=&Resolve;
    current=std::make_shared<ContextRecord>(reinterpret_cast<HMODULE>(1),driver,nullptr);
    resolved=Callback(&VendorB);
    Check(Lookup("glGetUnsignedBytevEXT")==Callback(&Bytes),"target-vendor-entry-still-routed");
    value=0;Bytes(0,&value);Check(value==2,"target-vendor-body-still-used");
    Check(Lookup("wglChoosePixelFormatARB")==Callback(&Window_choose::Invoke),"choose-wrapper-defers-to-explicit-dc");
    Check(Lookup("wglGetPixelFormatAttribivARB")==Callback(&Window_ints::Invoke),"ints-wrapper-defers-to-explicit-dc");
    Check(Lookup("wglGetPixelFormatAttribfvARB")==Callback(&Window_floats::Invoke),"floats-wrapper-defers-to-explicit-dc");
    Window_choose::original=nullptr;
    Check(Lookup("wglChoosePixelFormatARB")==Callback(&Window_choose::Invoke),"target-lookup-independent-of-source-availability");
    Window_choose::original=&ChooseA;current.reset();
    for(const uintptr_t invalid:{uintptr_t{0},uintptr_t{1},uintptr_t{2},uintptr_t{3},UINTPTR_MAX}) {
        resolved=reinterpret_cast<PROC>(invalid);
        Check(!Lookup("glGetUnsignedBytevEXT"),"invalid-native-lookup-remains-null");
    }
    const auto before=resolveCalls;
    Check(!Lookup(nullptr) && before==resolveCalls,"null-name-does-not-resolve");
    Check(!current && !pendingRelease && contexts.Count()==0 && drawables.Count()==0,"lookup-creates-no-runtime-records");
    std::printf("{\"passed\":%s,\"checks\":%u,\"failures\":%u,\"gpuCreated\":false,\"thirdPartyInjection\":false,\"remainingRecords\":0}\n",failures?"false":"true",checks,failures);
    return failures?1:0;
}
