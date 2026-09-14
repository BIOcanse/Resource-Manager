#include "../GpuPlacementShim/OpenGlPixelFormatDirectory.h"
#include <array>
#include <cstdio>
#include <stdexcept>

unsigned checks{};
void Check(bool value,const char* name){
    ++checks;std::printf("{\"check\":\"%s\",\"passed\":%s}\n",name,value?"true":"false");
    if(!value)throw std::runtime_error(name);
}
int main(){
    SetErrorMode(32771);
    try{
        FILETIME b{},e{},k{},u{};BOOL job{};
        Check(GetProcessTimes(GetCurrentProcess(),&b,&e,&k,&u)&&IsProcessInJob(GetCurrentProcess(),nullptr,&job)&&job,"owned-process");
        std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"inJob\":true,\"errorMode\":%u}\n",GetCurrentProcessId(),(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime,GetErrorMode());
        const HWND foreground=GetForegroundWindow();
        WNDCLASSW wc{};wc.style=CS_OWNDC;wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"ResourceManager.OwnPixelFormatDirectory";
        Check(RegisterClassW(&wc)!=0,"window-class");
        HWND hwnd=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,wc.lpszClassName,L"Owned directory",WS_POPUP,0,0,16,16,nullptr,nullptr,wc.hInstance,nullptr);
        Check(hwnd&&!IsWindowVisible(hwnd),"hidden-window");HDC dc=GetDC(hwnd);Check(dc!=nullptr,"owned-dc");
        const int count=DescribePixelFormat(dc,1,0,nullptr);Check(count>0&&count<=8192,"actual-catalog-count");
        std::vector<PIXELFORMATDESCRIPTOR> formats(static_cast<size_t>(count));
        for(int i=0;i<count;++i)Check(DescribePixelFormat(dc,i+1,sizeof(formats[i]),&formats[i])==count,"full-native-member-read");
        int nativeCount=count;
        while(nativeCount>0&&(formats[static_cast<size_t>(nativeCount-1)].dwFlags&PFD_GENERIC_FORMAT))--nativeCount;
        Check(nativeCount>0&&nativeCount<count,"actual-generic-tail");
        SetLastError(0x1234);
        const auto directory=ResourceManagerOpenGl::PixelFormatDirectory::Create(formats,nativeCount);
        Check(directory&&directory->Count()==count&&directory->NativeCount()==nativeCount&&GetLastError()==0x1234,"complete-directory-created");
        unsigned cases{};
        for(const int index:{-1,0,1,nativeCount,nativeCount+1,count,count+1}){
            for(const UINT size:{0u,1u,2u,3u,4u,8u,16u,39u,40u,41u,48u}){
                for(bool output:{false,true}){
                    alignas(PIXELFORMATDESCRIPTOR) std::array<BYTE,64> expected,actual;
                    expected.fill(0xCD);actual.fill(0xCD);
                    SetLastError(0x1234);const int a=DescribePixelFormat(dc,index,size,output?reinterpret_cast<PIXELFORMATDESCRIPTOR*>(expected.data()):nullptr);const DWORD ae=GetLastError();
                    SetLastError(0x1234);const int bResult=directory->Describe(index,size,output?reinterpret_cast<PIXELFORMATDESCRIPTOR*>(actual.data()):nullptr);const DWORD be=GetLastError();
                    const bool matched=a==bResult&&ae==be&&expected==actual;
                    std::printf("{\"directoryCase\":%u,\"index\":%d,\"capacity\":%u,\"output\":%s,\"nativeResult\":%d,\"directoryResult\":%d,\"nativeError\":%lu,\"directoryError\":%lu,\"all64BytesEqual\":%s}\n",cases++,index,size,output?"true":"false",a,bResult,ae,be,matched?"true":"false");
                    Check(matched,"actual-gdi-differential");
                }
            }
        }
        auto invalid=formats;invalid[0].nSize=0;
        Check(!ResourceManagerOpenGl::PixelFormatDirectory::Create(std::move(invalid),nativeCount)&&GetLastError()==ERROR_INVALID_DATA,"invalid-structure-rejected");
        invalid=formats;invalid[static_cast<size_t>(nativeCount)].dwFlags&=~PFD_GENERIC_FORMAT;
        Check(!ResourceManagerOpenGl::PixelFormatDirectory::Create(std::move(invalid),nativeCount)&&GetLastError()==ERROR_INVALID_DATA,"non-generic-tail-rejected");
        Check(!ResourceManagerOpenGl::PixelFormatDirectory::Create({},1)&&GetLastError()==ERROR_INVALID_PARAMETER,"empty-rejected");
        Check(!ResourceManagerOpenGl::PixelFormatDirectory::Create(formats,0)&&GetLastError()==ERROR_INVALID_PARAMETER,"zero-native-count-rejected");
        Check(!ResourceManagerOpenGl::PixelFormatDirectory::Create(formats,count+1)&&GetLastError()==ERROR_INVALID_PARAMETER,"excess-native-count-rejected");
        PIXELFORMATDESCRIPTOR retained{};
        Check(directory->Describe(1,sizeof(retained),&retained)==count&&std::memcmp(&retained,&formats[0],sizeof(retained))==0,"failed-preparation-old-record-unchanged");
        Check(ReleaseDC(hwnd,dc)&&DestroyWindow(hwnd)&&UnregisterClassW(wc.lpszClassName,wc.hInstance),"window-normal-cleanup");
        Check(GetForegroundWindow()==foreground,"foreground-unchanged");
        std::printf("{\"passed\":true,\"checks\":%u,\"cases\":%u,\"count\":%d,\"nativeCount\":%d,\"gpuContextCreated\":false,\"productionRoutingIntegrated\":false}\n",checks,cases,count,nativeCount);return 0;
    }catch(const std::exception& error){std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n",checks,error.what());return 1;}
}

