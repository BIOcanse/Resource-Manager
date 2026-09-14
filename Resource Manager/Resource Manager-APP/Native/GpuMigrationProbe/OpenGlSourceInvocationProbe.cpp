#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlCoreRoutes.h"
#include "../GpuPlacementShim/OpenGlWindowRoutes.h"
#include <cstdio>

namespace {
using namespace ResourceManagerOpenGl::Runtime;
unsigned checks{},failures{},sourceCalls{},targetCalls{},body{};
PROC sourceEntry{},targetEntry{};
bool forwarded{};
const int integers[]{101,202};const FLOAT floats[]{1.25f,2.5f};
HDC Dc(){return reinterpret_cast<HDC>(0x1234);}
void Check(bool yes,const char* label){++checks;if(!yes)++failures;std::printf("{\"check\":\"%s\",\"passed\":%s}\n",label,yes?"true":"false");}
PROC WINAPI Source(LPCSTR){++sourceCalls;return sourceEntry;}
PROC WINAPI Target(LPCSTR){++targetCalls;return targetEntry;}
HGLRC WINAPI NativeCurrent(){return reinterpret_cast<HGLRC>(1);}
template<unsigned Id> BOOL WINAPI Choose(HDC dc,const int* a,const FLOAT* f,UINT size,int* values,UINT* count){
    body=Id;forwarded=dc==Dc() && a==integers && f==floats && size==2 && values && count;
    if(values){values[0]=100+Id;}
    if(count){*count=1;}
    SetLastError(1000+Id);return Id==1;
}
template<unsigned Id> BOOL WINAPI Ints(HDC dc,int format,int layer,UINT size,const int* a,int* values){
    body=Id;forwarded=dc==Dc() && format==7 && layer==0 && size==2 && a==integers && values;
    if(values){values[0]=200+Id;}
    SetLastError(2000+Id);return Id==1;
}
template<unsigned Id> BOOL WINAPI Floats(HDC dc,int format,int layer,UINT size,const int* a,FLOAT* values){
    body=Id;forwarded=dc==Dc() && format==7 && layer==0 && size==2 && a==integers && values;
    if(values){values[0]=static_cast<FLOAT>(300+Id);}
    SetLastError(3000+Id);return Id==1;
}
template<unsigned Id> void APIENTRY Vendor(GLenum name,GLubyte* value){body=Id;forwarded=name==0x9599 && value;if(value)*value=Id;}
template<typename Function> Function As(PROC address){Function result{};std::memcpy(&result,&address,sizeof(result));return result;}
void CallChoose(PROC address,unsigned expected){
    int result[2]{};UINT count{};body=0;forwarded=false;SetLastError(0);
    const BOOL returned=As<PFNWGLCHOOSEPIXELFORMATARBPROC>(address)(Dc(),integers,floats,2,result,&count);
    const DWORD error=GetLastError();
    Check(forwarded && body==expected && result[0]==static_cast<int>(100+expected) && count==1 && returned==(expected==1) && error==1000+expected,"choose-arguments-body-results-error");
}
void CallInts(PROC address,unsigned expected){
    int result[2]{};body=0;forwarded=false;SetLastError(0);
    const BOOL returned=As<PFNWGLGETPIXELFORMATATTRIBIVARBPROC>(address)(Dc(),7,0,2,integers,result);
    const DWORD error=GetLastError();
    Check(forwarded && body==expected && result[0]==static_cast<int>(200+expected) && returned==(expected==1) && error==2000+expected,"ints-arguments-body-results-error");
}
void CallFloats(PROC address,unsigned expected){
    FLOAT result[2]{};body=0;forwarded=false;SetLastError(0);
    const BOOL returned=As<PFNWGLGETPIXELFORMATATTRIBFVARBPROC>(address)(Dc(),7,0,2,integers,result);
    const DWORD error=GetLastError();
    Check(forwarded && body==expected && result[0]==static_cast<FLOAT>(300+expected) && returned==(expected==1) && error==3000+expected,"floats-arguments-body-results-error");
}
template<typename Route,typename Function,typename Invoke>
void WindowCases(const char* name,Function installed,Function original,Function other,Invoke invoke,const ContextReference& owned){
    Check(Callback(installed)!=Callback(original) && Callback(installed)!=Callback(other) && Callback(original)!=Callback(other),"three-distinct-window-function-addresses");
    Route::originalAddress=Callback(installed);Route::original=original;
    sourceEntry=Callback(installed);PROC entry=Lookup(name);
    Check(entry==Callback(&Route::Invoke),"installed-window-address-selects-wrapper");invoke(entry,1);
    sourceEntry=Callback(other);entry=Lookup(name);
    Check(entry==sourceEntry,"other-native-window-address-preserved");invoke(entry,2);
    sourceEntry=Callback(original);entry=Lookup(name);
    Check(entry==sourceEntry,"saved-original-is-not-installed-window-address");invoke(entry,1);
    current=owned;targetEntry=Callback(other);const auto a=sourceCalls,b=targetCalls;entry=Lookup(name);
    Check(entry==Callback(&Route::Invoke) && sourceCalls==a && targetCalls==b,"unowned-dc-retains-source-with-target-current-without-resolving");invoke(entry,1);
    Route::original=nullptr;Check(Lookup(name)==Callback(&Route::Invoke) && sourceCalls==a && targetCalls==b,"target-lookup-does-not-require-source-original");
    Route::original=original;current.reset();
}
void CallVendor(PROC entry,unsigned expected){GLubyte value{};body=0;forwarded=false;As<GetBytesEntry>(entry)(0x9599,&value);Check(forwarded && body==expected && value==expected,"vendor-actual-body-and-arguments");}
}
int main(){
    SetErrorMode(32771);FILETIME born{},end{},kernel{},user{};BOOL inJob{};
    if(!GetProcessTimes(GetCurrentProcess(),&born,&end,&kernel,&user) || !IsProcessInJob(GetCurrentProcess(),nullptr,&inJob) || !inJob)return 2;
    std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"errorMode\":%u,\"inJob\":true}\n",GetCurrentProcessId(),(static_cast<unsigned long long>(born.dwHighDateTime)<<32)|born.dwLowDateTime,GetErrorMode());
    systemLookup=&Source;systemCurrent=&NativeCurrent;
    ResourceManagerOpenGl::IcdExports driver{};driver.getProcAddress=&Target;
    const auto owned=std::make_shared<ContextRecord>(reinterpret_cast<HMODULE>(1),driver,nullptr);
    WindowCases<Window_choose>("wglChoosePixelFormatARB",&Choose<9>,&Choose<1>,&Choose<2>,&CallChoose,owned);
    WindowCases<Window_ints>("wglGetPixelFormatAttribivARB",&Ints<9>,&Ints<1>,&Ints<2>,&CallInts,owned);
    WindowCases<Window_floats>("wglGetPixelFormatAttribfvARB",&Floats<9>,&Floats<1>,&Floats<2>,&CallFloats,owned);
    originalVendorBytesAddress=Callback(&Vendor<9>);originalVendorBytes=&Vendor<1>;
    Check(originalVendorBytesAddress!=Callback(originalVendorBytes) && Callback(&Vendor<2>)!=Callback(&Vendor<3>),"distinct-vendor-entry-original-source-target");
    sourceEntry=originalVendorBytesAddress;PROC entry=Lookup("glGetUnsignedBytevEXT");
    Check(entry==Callback(&Bytes),"installed-vendor-address-selects-wrapper");CallVendor(entry,1);
    sourceEntry=Callback(originalVendorBytes);entry=Lookup("glGetUnsignedBytevEXT");
    Check(entry==sourceEntry,"saved-original-is-not-installed-vendor-address");CallVendor(entry,1);
    sourceEntry=Callback(&Vendor<2>);entry=Lookup("glGetUnsignedBytevEXT");
    Check(entry==sourceEntry,"different-native-vendor-keeps-own-body");CallVendor(entry,2);
    current=owned;targetEntry=Callback(&Vendor<3>);const auto a=sourceCalls,b=targetCalls;
    entry=Lookup("glGetUnsignedBytevEXT");Check(entry==Callback(&Bytes) && sourceCalls==a && targetCalls==b+1,"target-lookup-never-uses-source-resolver");
    CallVendor(entry,3);Check(sourceCalls==a && targetCalls==b+2,"target-invocation-never-uses-source-resolver");
    current.reset();Check(!pendingRelease && contexts.Count()==0 && drawables.Count()==0,"no-runtime-store-adoption");
    std::printf("{\"passed\":%s,\"checks\":%u,\"failures\":%u,\"gpuCreated\":false,\"thirdPartyInjection\":false,\"remainingRecords\":0}\n",failures?"false":"true",checks,failures);
    return failures?1:0;
}
