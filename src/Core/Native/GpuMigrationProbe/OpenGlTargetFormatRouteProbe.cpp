#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlCoreRoutes.h"
#include "../GpuPlacementShim/OpenGlWindowPreparation.h"
#include "../GpuPlacementShim/OpenGlWindowThread.h"
#include <cstdio>
#include <stdexcept>

namespace {
using namespace ResourceManagerOpenGl;
using namespace ResourceManagerOpenGl::Runtime;
unsigned checks{},calls[3]{},unexpectedDriver{},resolverCalls{};
HDC expectedDc{};
const int keys[]{WGL_COLOR_BITS_ARB,24,0};
const FLOAT floatKeys[]{1.5f,2.5f,0};
bool forwarded{};
void Check(bool value,const char* label){++checks;std::printf("{\"check\":\"%s\",\"passed\":%s}\n",label,value?"true":"false");if(!value)throw std::runtime_error(label);}
template<typename Signature> struct UnusedDriver;
template<typename Result,typename... Args> struct UnusedDriver<Result(WINAPI*)(Args...)>{
    static Result WINAPI Invoke(Args...){++unexpectedDriver;if constexpr(!std::is_void_v<Result>)return Result{};}
};
PROC WINAPI Resolve(LPCSTR){++resolverCalls;return nullptr;}
template<unsigned Id> BOOL WINAPI Choose(HDC dc,const int* i,const FLOAT* f,UINT n,int* result,UINT* count){
    ++calls[Id];forwarded=dc==expectedDc&&i==keys&&f==floatKeys&&n==2&&result&&count;
    result[0]=40+Id;*count=Id==1?1:0;SetLastError(1100+Id);return Id==1;
}
template<unsigned Id> BOOL WINAPI Ints(HDC dc,int format,int layer,UINT n,const int* names,int* result){
    ++calls[Id];forwarded=dc==expectedDc&&format==42&&layer==-1&&n==2&&names==keys&&result;
    result[0]=80+Id;SetLastError(2200+Id);return Id==1;
}
template<unsigned Id> BOOL WINAPI Floats(HDC dc,int format,int layer,UINT n,const int* names,FLOAT* result){
    ++calls[Id];forwarded=dc==expectedDc&&format==42&&layer==-1&&n==2&&names==keys&&result;
    result[0]=120.5f+Id;SetLastError(3300+Id);return Id==1;
}
constexpr const char* formatDeclarations[]{"WGL_ARB_multisample","WGL_ARB_framebuffer_sRGB","WGL_EXT_framebuffer_sRGB","WGL_ARB_pixel_format_float"};
constexpr const char* nvidiaDeclarations="WGL_ARB_extensions_string WGL_EXT_extensions_string WGL_ARB_pixel_format WGL_ARB_multisample WGL_EXT_framebuffer_sRGB WGL_ARB_pixel_format_float WGL_ARB_pbuffer";
constexpr const char* amdDeclarations="WGL_ARB_extensions_string WGL_EXT_extensions_string WGL_ARB_pixel_format WGL_ARB_multisample WGL_ARB_framebuffer_sRGB WGL_ARB_pixel_format_float WGL_AMD_gpu_association";
const char* nativeDeclarations=amdDeclarations;
unsigned discoveryCalls{};
const char* WINAPI Arb(HDC){++discoveryCalls;SetLastError(4400);return nativeDeclarations;}
const char* WINAPI Ext(){++discoveryCalls;SetLastError(4500);return nativeDeclarations;}
void InitializeTestStrings(){
    // Only the focused fixture prepares strings here; real installation is tested separately.
    for(size_t mask=0;mask<windowExtensionStrings.size();++mask){
        auto& value=windowExtensionStrings[mask];
        for(size_t i=0;i<std::size(coveredWindowExtensions);++i)if(mask&(size_t{1}<<i)){
            if(!value.empty())value+=' ';
            value+=coveredWindowExtensions[i];
        }
    }
}
void ExtensionCases(HDC dc,bool ready){
    for(const auto text:{nvidiaDeclarations,amdDeclarations}){
        nativeDeclarations=text;const auto before=discoveryCalls;
        const char* actual=WindowExtensionsArb(dc);const DWORD error=GetLastError();
        Check(actual&&error==4400&&discoveryCalls==before+1,"target-discovery-preserves-one-call-and-error");
        for(const auto token:formatDeclarations)
            Check(WindowExtensionToken(actual,token)==(ready&&WindowExtensionToken(text,token)),"target-format-declaration-is-native-and-query-ready");
        Check(!WindowExtensionToken(actual,"WGL_ARB_pbuffer")&&!WindowExtensionToken(actual,"WGL_AMD_gpu_association")&&!WindowExtensionToken(actual,"WGL_EXT_pixel_format"),"unimplemented-families-not-invented");
    }
}
void ExtensionBoundaries(const WindowFunctions& functions){
    const auto mask=PreparedWindowMask(functions);
    const char* stable=CoveredWindowExtensions(amdDeclarations,mask);const std::string copy=stable;
    for(const auto text:{"", "WGL_ARB_multisample WGL_ARB_framebuffer_sRGB WGL_EXT_framebuffer_sRGB WGL_ARB_pixel_format_float", "WGL_ARB_extensions_string WGL_ARB_multisample WGL_ARB_framebuffer_sRGB WGL_EXT_framebuffer_sRGB WGL_ARB_pixel_format_float", "WGL_ARB_pixel_format WGL_ARB_multisample WGL_ARB_framebuffer_sRGB WGL_EXT_framebuffer_sRGB WGL_ARB_pixel_format_float"}){
        const char* actual=CoveredWindowExtensions(text,mask);
        for(const auto token:formatDeclarations)Check(!WindowExtensionToken(actual,token),"format-declaration-needs-native-query-discovery-and-base");
    }
    for(size_t i=0;i<std::size(formatDeclarations);++i){
        const std::string text=std::string("WGL_ARB_extensions_string WGL_ARB_pixel_format prefix_")+formatDeclarations[i]+" "+formatDeclarations[i]+"_suffix";
        const char* actual=CoveredWindowExtensions(text.c_str(),mask);
        for(const auto token:formatDeclarations)Check(!WindowExtensionToken(actual,token),"format-token-boundaries-are-exact");
    }
    auto missingDiscovery=functions;missingDiscovery.arb=nullptr;
    const char* missing=CoveredWindowExtensions(amdDeclarations,PreparedWindowMask(missingDiscovery));
    for(const auto token:formatDeclarations)Check(!WindowExtensionToken(missing,token),"format-declaration-needs-prepared-discovery");
    const std::string duplicate=std::string(amdDeclarations)+" "+amdDeclarations;
    Check(CoveredWindowExtensions(duplicate.c_str(),mask)==stable,"duplicate-native-tokens-use-original-stable-string");
    Check(std::string(stable)==copy&&CoveredWindowExtensions(amdDeclarations,mask)==stable,"previous-string-and-pointer-stay-valid");
    Check(CoveredWindowExtensions(nullptr,mask)==nullptr,"null-native-discovery-remains-null");
}
IcdExports Driver(){
    IcdExports d;
#define FIELD(name) d.name=&UnusedDriver<decltype(d.name)>::Invoke
    FIELD(validateVersion);FIELD(setCallbacks);FIELD(describePixelFormat);FIELD(setPixelFormat);
    FIELD(createLayerContext);FIELD(setContext);FIELD(releaseContext);FIELD(deleteContext);
    FIELD(shareLists);FIELD(copyContext);FIELD(swapBuffers);
#undef FIELD
    d.getProcAddress=&Resolve;return d;
}
void Calls(HDC dc,unsigned id){
    expectedDc=dc;int result[2]{};FLOAT floating[2]{};UINT count=999;forwarded=false;
    auto a=calls[1],b=calls[2];
    const BOOL choose=Window_choose::Invoke(dc,keys,floatKeys,2,result,&count);const DWORD ce=GetLastError();
    Check(forwarded&&choose==(id==1)&&result[0]==static_cast<int>(40+id)&&count==(id==1?1u:0u)&&ce==1100+id,"choose-forwards-request-result-and-error");
    forwarded=false;const BOOL ints=Window_ints::Invoke(dc,42,-1,2,keys,result);const DWORD ie=GetLastError();
    Check(forwarded&&ints==(id==1)&&result[0]==static_cast<int>(80+id)&&ie==2200+id,"integer-query-forwards-request-result-and-error");
    forwarded=false;const BOOL floats=Window_floats::Invoke(dc,42,-1,2,keys,floating);const DWORD fe=GetLastError();
    Check(forwarded&&floats==(id==1)&&floating[0]==120.5f+id&&fe==3300+id,"float-query-forwards-request-result-and-error");
    Check(calls[1]==a+(id==1?3:0)&&calls[2]==b+(id==2?3:0),"only-requested-owner-called");
}
void RejectAll(HDC dc,DWORD error){
    int value[2]{71,72};FLOAT floating[2]{73,74};UINT count=75;const auto a=calls[1],b=calls[2];
    Check(!Window_choose::Invoke(dc,keys,floatKeys,2,value,&count)&&GetLastError()==error&&value[0]==71&&count==75,"choose-reject-does-not-invent-output");
    Check(!Window_ints::Invoke(dc,42,-1,2,keys,value)&&GetLastError()==error&&value[0]==71,"integer-reject-does-not-invent-output");
    Check(!Window_floats::Invoke(dc,42,-1,2,keys,floating)&&GetLastError()==error&&floating[0]==73,"float-reject-does-not-invent-output");
    Check(calls[1]==a&&calls[2]==b,"rejection-never-calls-source-or-target");
}
}
int main(){
    SetErrorMode(32771);
    try{
        FILETIME born{},e{},k{},u{};BOOL job{};
        Check(GetProcessTimes(GetCurrentProcess(),&born,&e,&k,&u)&&IsProcessInJob(GetCurrentProcess(),nullptr,&job)&&job,"owned-native-process");
        std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"errorMode\":%u,\"inJob\":true}\n",GetCurrentProcessId(),(static_cast<unsigned long long>(born.dwHighDateTime)<<32)|born.dwLowDateTime,GetErrorMode());
        const auto foreground=GetForegroundWindow();
        WNDCLASSW wc{};wc.style=CS_OWNDC;wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"RmTargetFormatQueryProbe";
        Check(RegisterClassW(&wc)!=0,"register-hidden-window-class");
        HWND windows[6]{};HDC dcs[6]{};
        for(int i=0;i<6;++i){
            windows[i]=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,wc.lpszClassName,L"",WS_POPUP,0,0,16,16,nullptr,nullptr,wc.hInstance,nullptr);
            Check(windows[i]&&!IsWindowVisible(windows[i]),"owned-hidden-window");
            dcs[i]=GetDC(windows[i]);Check(dcs[i]!=nullptr,"owned-window-dc");
        }
        Window_choose::original=&Choose<1>;Window_ints::original=&Ints<1>;Window_floats::original=&Floats<1>;
        InitializeTestStrings();Window_arb::original=&Arb;Window_ext::original=&Ext;
        nativeDeclarations=nvidiaDeclarations;
        Check(WindowExtensionsArb(dcs[0])==nativeDeclarations&&GetLastError()==4400,"unowned-native-discovery-pointer-and-error-unchanged");
        Check(WindowExtensionsExt()==nativeDeclarations&&GetLastError()==4500,"source-current-native-ext-string-unchanged");
        Window_choose::originalAddress=Callback(&Choose<1>);
        Window_ints::originalAddress=Callback(&Ints<1>);Window_floats::originalAddress=Callback(&Floats<1>);
        Calls(dcs[0],1);
        auto incomplete=PrepareObservedDrawable(windows[1]);Check(incomplete!=nullptr,"explicit-unprepared-record");
        RejectAll(dcs[1],ERROR_NOT_READY);
        const auto driver=Driver();
        const auto target=DrawableTarget::Create(wc.hInstance,driver,{1,0},{wc.hInstance,nullptr},1);
        PIXELFORMATDESCRIPTOR p{};p.nSize=sizeof(p);p.nVersion=1;
        const auto directory=PixelFormatDirectory::Create({p},1);
        Check(target&&directory,"explicit-fixture-driver-and-directory");
        for(int i=2;i<6;++i){
            const auto record=PrepareObservedDrawable(windows[i]);
            Check(record&&drawables.PublishTarget(record,target,1)&&drawables.PublishDirectory(record,directory),"publish-existing-window-facts");
            current=std::make_shared<ContextRecord>(wc.hInstance,driver,nullptr);boundDc=dcs[i];
            WindowEntries entries{};entries.arb=&Arb;entries.ext=&Ext;entries.choose=&Choose<2>;entries.ints=&Ints<2>;entries.floats=&Floats<2>;
            if(i==3)entries.choose=nullptr;
            if(i==4)entries.ints=nullptr;
            if(i==5)entries.floats=nullptr;
            SetLastError(1234);const auto prepared=PrepareWindowDispatch(entries);
            Check(prepared&&GetLastError()==1234&&prepared->functions.choose==entries.choose&&prepared->functions.ints==entries.ints&&prepared->functions.floats==entries.floats,"production-preparation-retains-three-exact-entries");
            Check(drawables.PublishWindowDispatch(record,prepared),"publish-one-window-dispatch");
            Check(bool(PreparedWindowMask(prepared->functions)&ExtensionBit(PixelFormat))==(i==2),"format-advertisement-requires-target-triple");
            current.reset();boundDc=nullptr;
        }
        const auto prepared=drawables.Read(dcs[2]).windowDispatch;
        for(int i=2;i<6;++i)ExtensionCases(dcs[i],i==2);
        ExtensionBoundaries(prepared->functions);
        current=std::make_shared<ContextRecord>(wc.hInstance,driver,prepared);
        boundDc=dcs[2];nativeDeclarations=amdDeclarations;
        const char* ext=WindowExtensionsExt();
        Check(ext&&GetLastError()==4500&&WindowExtensionToken(ext,"WGL_ARB_framebuffer_sRGB")&&!WindowExtensionToken(ext,"WGL_EXT_framebuffer_sRGB"),"target-ext-discovery-follows-current-context-native-spelling");
        Check(WindowExtensionsArb(dcs[0])==nativeDeclarations,"target-current-does-not-filter-unowned-hdc");
        nativeDeclarations=nullptr;
        Check(WindowExtensionsArb(dcs[2])==nullptr&&GetLastError()==4400,"target-arb-null-native-not-replaced");
        Check(WindowExtensionsExt()==nullptr&&GetLastError()==4500,"target-ext-null-native-not-replaced");
        current.reset();boundDc=nullptr;
        for(int slot=0;slot<3;++slot)for(INT_PTR invalid:{1,2,3,-1}){
            auto f=prepared->functions;
            if(slot==0)f.choose=reinterpret_cast<PFNWGLCHOOSEPIXELFORMATARBPROC>(invalid);
            if(slot==1)f.ints=reinterpret_cast<PFNWGLGETPIXELFORMATATTRIBIVARBPROC>(invalid);
            if(slot==2)f.floats=reinterpret_cast<PFNWGLGETPIXELFORMATATTRIBFVARBPROC>(invalid);
            Check(!WindowDispatch::Create(wc.hInstance,f)&&GetLastError()==ERROR_INVALID_PARAMETER,"invalid-native-format-entry-rejected");
        }
        Calls(dcs[2],2);
        current=std::make_shared<ContextRecord>(wc.hInstance,driver,nullptr);boundDc=dcs[1];
        Calls(dcs[0],1);Calls(dcs[2],2);
        Check(Lookup("wglChoosePixelFormatARB")==Callback(&Window_choose::Invoke),"target-lookup-returns-argument-owned-wrapper");
        Window_choose::original=nullptr;Window_ints::original=nullptr;Window_floats::original=nullptr;
        Calls(dcs[2],2);RejectAll(dcs[0],ERROR_NOT_SUPPORTED);
        Window_choose::original=&Choose<1>;Window_ints::original=&Ints<1>;Window_floats::original=&Floats<1>;
        for(int i=3;i<6;++i){
            int v[2]{71,72};FLOAT f[2]{73,74};UINT count=75;const auto a=calls[1],b=calls[2];BOOL okay{};
            if(i==3)okay=Window_choose::Invoke(dcs[i],keys,floatKeys,2,v,&count);
            if(i==4)okay=Window_ints::Invoke(dcs[i],42,-1,2,keys,v);
            if(i==5)okay=Window_floats::Invoke(dcs[i],42,-1,2,keys,f);
            Check(!okay&&GetLastError()==ERROR_NOT_SUPPORTED&&v[0]==71&&f[0]==73&&count==75&&calls[1]==a&&calls[2]==b,"missing-target-entry-does-not-fall-back");
        }
        pendingRelease=current;RejectAll(dcs[2],ERROR_BUSY);Calls(dcs[0],1);
        pendingRelease.reset();current.reset();boundDc=nullptr;
        PROC other=Callback(&Choose<2>),resolved{};
        Check(WindowLookup("wglChoosePixelFormatARB",other,resolved)&&resolved==other,"different-native-source-entry-preserved");
        Check(resolverCalls==4&&unexpectedDriver==0,"reads-never-reacquire-or-call-driver-lifecycle");
        for(int i=0;i<6;++i){Check(ReleaseDC(windows[i],dcs[i])==1&&DestroyWindow(windows[i]),"normal-window-release-and-destroy");}
        Check(drawables.Count()==0&&contexts.Count()==0&&RemoveWindowObserversForCurrentThread(),"original-store-and-observers-cleaned");
        Check(UnregisterClassW(wc.lpszClassName,wc.hInstance)&&GetForegroundWindow()==foreground,"class-cleaned-foreground-unchanged");
        std::printf("{\"passed\":true,\"checks\":%u,\"failures\":0,\"remainingRecords\":0,\"gpuCreated\":false,\"thirdPartyInjection\":false,\"sourceCalls\":%u,\"targetCalls\":%u,\"explicitPreparations\":%u}\n",checks,calls[1],calls[2],resolverCalls);
        return 0;
    }catch(const std::exception& error){std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n",checks,error.what());return 1;}
}
