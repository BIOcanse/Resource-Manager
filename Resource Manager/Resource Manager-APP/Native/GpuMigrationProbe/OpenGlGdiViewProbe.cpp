#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include "../GpuPlacementShim/OpenGlPixelFormatRoutes.h"
#include "../GpuPlacementShim/OpenGlCoreRoutes.h"
#include "../GpuPlacementShim/OpenGlWindowRoutes.h"
#include "../GpuPlacementShim/OpenGlWindowThread.h"
#include <cstdio>
#include <stdexcept>
#include <type_traits>

namespace {
using namespace ResourceManagerOpenGl;
using namespace ResourceManagerOpenGl::Runtime;
unsigned checks{},sourceCalls{},driverCalls{};
unsigned targetSetCalls{},sourceSetCalls{};
ResourceManagerGpuPolicy::GpuShimPolicy policy;
ResourceManagerGpuPolicy::GpuShimPolicy ReadPolicy(){return policy;}
BOOL WINAPI SourceSwap(HDC){SetLastError(703);return TRUE;}
BOOL targetSetResult{};
HDC expectedDc{};
const PIXELFORMATDESCRIPTOR* expectedDescription{};
bool sourceForwarded{},targetForwarded{},readDuringSet{},reentryRejected{};
BOOL WINAPI TargetSet(HDC dc,LONG format){
    ++targetSetCalls;targetForwarded=dc==expectedDc&&format==2;
    readDuringSet=ReadPixelFormat(dc)==0;
    reentryRejected=!WritePixelFormat(dc,format,nullptr)&&GetLastError()==ERROR_BUSY;
    SetLastError(1701);return targetSetResult;
}
BOOL WINAPI SourceSet(HDC dc,int format,const PIXELFORMATDESCRIPTOR* p){
    ++sourceSetCalls;sourceForwarded=dc==expectedDc&&format==999&&p==expectedDescription;
    SetLastError(702);return 5;
}
void Check(bool yes,const char* name){++checks;std::printf("{\"check\":\"%s\",\"passed\":%s}\n",name,yes?"true":"false");if(!yes)throw std::runtime_error(name);}
template<typename Signature>struct Unused;
template<typename R,typename... A>struct Unused<R(WINAPI*)(A...)>{static R WINAPI Call(A...){++driverCalls;if constexpr(!std::is_void_v<R>)return R{};}};
IcdExports Driver(){IcdExports d{};
#define FIELD(name) d.name=&Unused<decltype(d.name)>::Call
    FIELD(validateVersion);FIELD(setCallbacks);FIELD(describePixelFormat);FIELD(setPixelFormat);FIELD(createLayerContext);FIELD(setContext);FIELD(releaseContext);FIELD(deleteContext);FIELD(shareLists);FIELD(copyContext);FIELD(swapBuffers);FIELD(getProcAddress);
#undef FIELD
    d.setPixelFormat=&TargetSet;return d;
}
int WINAPI SourceGet(HDC){++sourceCalls;SetLastError(700);return 7;}
int WINAPI SourceDescribe(HDC,int,UINT bytes,PIXELFORMATDESCRIPTOR* p){++sourceCalls;if(p&&bytes>=sizeof(*p))p->cStencilBits=3;SetLastError(701);return 9;}
void Missing(HDC dc){PIXELFORMATDESCRIPTOR p{};p.nSize=19;const auto n=sourceCalls;
    Check(!ReadPixelFormat(dc)&&GetLastError()==ERROR_NOT_READY,"unprepared-get-no-source-fallback");
    Check(!WritePixelFormat(dc,2,&p)&&GetLastError()==ERROR_NOT_READY&&!targetSetCalls,"unprepared-set-no-source-fallback");
    Check(!DescribePixelFormats(dc,1,sizeof(p),&p)&&GetLastError()==ERROR_NOT_READY&&p.nSize==19&&sourceCalls==n,"unprepared-describe-no-source-fallback");
}
}
int main(){SetErrorMode(32771);
 try{
    FILETIME born{},e{},k{},u{};BOOL job{};Check(GetProcessTimes(GetCurrentProcess(),&born,&e,&k,&u)&&IsProcessInJob(GetCurrentProcess(),nullptr,&job)&&job,"owned-native-process");
    std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"errorMode\":%u,\"inJob\":true}\n",GetCurrentProcessId(),(static_cast<unsigned long long>(born.dwHighDateTime)<<32)|born.dwLowDateTime,GetErrorMode());
    const HWND foreground=GetForegroundWindow();const auto exe=GetModuleHandleW(nullptr),source=GetModuleHandleW(L"gdi32.dll"),targetModule=GetModuleHandleW(L"kernel32.dll");
    Check(exe&&source&&targetModule&&exe!=source&&source!=targetModule,"actual-distinct-module-inputs");
    WNDCLASSW wc{};wc.style=CS_OWNDC;wc.lpfnWndProc=DefWindowProcW;wc.hInstance=exe;wc.lpszClassName=L"RmGdiViewProbe";
    Check(RegisterClassW(&wc)!=0,"register-hidden-class");
    const HWND window=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,wc.lpszClassName,L"",WS_POPUP,0,0,16,16,nullptr,nullptr,exe,nullptr);
    Check(window&&!IsWindowVisible(window),"own-hidden-window");const HDC dc=GetDC(window);Check(dc!=nullptr,"own-dc");
    systemPixelFormat=&SourceGet;systemDescribePixelFormat=&SourceDescribe;systemSetPixelFormat=&SourceSet;systemSwap=&SourceSwap;expectedDc=dc;
    creationPolicyReader.store(&ReadPolicy);
    Check(ReadPixelFormat(dc)==7&&GetLastError()==700,"unenrolled-source-get-result-error");
    PIXELFORMATDESCRIPTOR p{};Check(DescribePixelFormats(dc,999,sizeof(p),&p)==9&&p.cStencilBits==3&&GetLastError()==701,"unenrolled-source-describe-result-error");
    expectedDescription=&p;Check(WritePixelFormat(dc,999,&p)==5&&GetLastError()==702&&sourceForwarded&&sourceSetCalls==1,"unenrolled-set-preserves-arguments-result-error");
    policy={ResourceManagerGpuPolicy::GpuShimPolicyMode::TargetLuid,{1,0}};
    const auto unenrolledCalls=sourceCalls;
    Check(!ReadPixelFormat(dc)&&GetLastError()==ERROR_NOT_READY&&sourceCalls==unenrolledCalls,"target-unenrolled-get-never-borrows-source");
    const auto record=PrepareObservedDrawable(window);Check(record!=nullptr,"explicit-original-store-enrollment");Missing(dc);
    pixelFormatSourceCallers.modules[0]=source;pixelFormatSourceCallers.count=1;Missing(dc);
    const auto target=DrawableTarget::Create(targetModule,Driver(),{1,0},{targetModule,nullptr},1);Check(target&&drawables.PublishTarget(record,target,7),"explicit-target-facts");Missing(dc);
    p={};p.nSize=sizeof(p);p.nVersion=1;p.cStencilBits=8;p.dwFlags=PFD_DRAW_TO_WINDOW;
    const auto directory=PixelFormatDirectory::Create({p,p},2);Check(directory&&drawables.PublishDirectory(record,directory),"explicit-target-directory");
    const auto before=sourceCalls;SetLastError(1234);
    Check(ReadPixelFormat(dc)==0&&GetLastError()==1234&&sourceCalls==before,"prepared-is-not-installed");
    Check(!Create(dc)&&GetLastError()==ERROR_INVALID_PIXEL_FORMAT&&!driverCalls,"uninstalled-create-never-enters-driver");
    Check(!CreateLayer(dc,0)&&GetLastError()==ERROR_INVALID_PIXEL_FORMAT&&!driverCalls,"uninstalled-layer-never-enters-driver");
    Check(!Attributes(dc,nullptr,nullptr)&&GetLastError()==ERROR_INVALID_PIXEL_FORMAT&&!driverCalls,"uninstalled-attributed-create-never-enters-driver");
    Check(Swap(dc)&&GetLastError()==703&&!driverCalls,"preparation-does-not-hand-off-presentation");
    for(int format:{-1,0,3})Check(!WritePixelFormat(dc,format,&p)&&GetLastError()==ERROR_INVALID_PIXEL_FORMAT&&!targetSetCalls&&sourceSetCalls==1,"invalid-target-set-never-forwards-to-source");
    Check(!WritePixelFormat(dc,2,&p)&&GetLastError()==1701&&targetSetCalls==1&&targetForwarded&&readDuringSet&&reentryRejected,"target-set-failure-one-exact-call");
    Check(ReadPixelFormat(dc)==0&&drawables.Read(dc).installedPixelFormat==0,"failed-public-set-retains-zero");
    targetSetResult=TRUE;
    Check(WritePixelFormat(dc,2,nullptr)&&GetLastError()==1701&&targetSetCalls==2&&targetForwarded&&readDuringSet&&reentryRejected,"target-set-success-uses-index-not-pfd");
    Check(drawables.Read(dc).installedPixelFormat==2,"public-set-publishes-original-store-fact");
    SetLastError(4321);Check(WritePixelFormat(dc,2,&p)&&GetLastError()==4321&&targetSetCalls==2,"public-repeat-same-no-driver-call");
    Check(!WritePixelFormat(dc,1,&p)&&GetLastError()==ERROR_INVALID_PIXEL_FORMAT&&targetSetCalls==2,"public-different-format-rejected");
    SetLastError(1234);
    Check(ReadPixelFormat(dc)==2&&GetLastError()==1234&&sourceCalls==before,"application-get-target-number-no-source-call");
    PIXELFORMATDESCRIPTOR actual{};SetLastError(1234);
    Check(DescribePixelFormats(dc,2,sizeof(actual),&actual)==2&&actual.cStencilBits==8&&GetLastError()==1234&&sourceCalls==before,"application-describe-target-no-source-call");
    for(const int index:{-1,0,3}){actual.nSize=19;SetLastError(1234);Check(!DescribePixelFormats(dc,index,sizeof(actual),&actual)&&GetLastError()==ERROR_INVALID_PARAMETER&&actual.nSize==19&&sourceCalls==before,"bad-target-number-does-not-borrow-source");}
    actual.nSize=19;Check(!DescribePixelFormats(dc,2,1,&actual)&&GetLastError()==ERROR_INVALID_PARAMETER&&actual.nSize==19,"short-output-unchanged");
    SetLastError(1234);Check(DescribePixelFormats(dc,-1,0,nullptr)==2&&GetLastError()==1234,"count-only-target-view");
    auto view=drawables.Read(dc);bool isTarget=true;
    Check(TargetPixelFormatView(dc,view,source,isTarget)&&!isTarget,"source-module-keeps-source-view");
    Check(TargetPixelFormatView(dc,view,targetModule,isTarget)&&isTarget,"target-module-target-view");
    Check(TargetPixelFormatView(dc,view,exe,isTarget)&&isTarget,"application-module-target-view");
    pixelFormatSourceCallers.modules[0]=exe;
    Check(ReadPixelFormat(dc)==7&&GetLastError()==700,"known-source-caller-original-get");
    Check(DescribePixelFormats(dc,999,sizeof(actual),&actual)==9&&GetLastError()==701&&actual.cStencilBits==3,"known-source-caller-original-describe");
    Check(WritePixelFormat(dc,999,&p)==5&&GetLastError()==702&&sourceForwarded&&sourceSetCalls==2&&drawables.Read(dc).installedPixelFormat==2,"known-source-set-does-not-change-target-fact");
    pixelFormatSourceCallers.modules[0]=targetModule;isTarget=true;
    Check(!TargetPixelFormatView(dc,view,targetModule,isTarget)&&GetLastError()==ERROR_NOT_SUPPORTED&&!isTarget,"overlapping-driver-module-not-guessed");
    pixelFormatSourceCallers.modules[0]=source;
    Check(!current&&!pendingRelease&&contexts.Count()==0&&!driverCalls,"reads-do-not-create-bind-or-resolve");
    Check(ReleaseDC(window,dc)==1&&DestroyWindow(window)&&drawables.Count()==0,"normal-window-terminal-cleanup");
    Check(RemoveWindowObserversForCurrentThread()&&UnregisterClassW(wc.lpszClassName,exe),"normal-observer-class-cleanup");
    Check(GetForegroundWindow()==foreground,"foreground-unchanged");
    std::printf("{\"passed\":true,\"checks\":%u,\"failures\":0,\"remainingRecords\":0,\"gpuCreated\":false,\"thirdPartyInjection\":false}\n",checks);return 0;
 }catch(const std::exception& error){std::printf("{\"passed\":false,\"error\":\"%s\"}\n",error.what());return 1;}
}
