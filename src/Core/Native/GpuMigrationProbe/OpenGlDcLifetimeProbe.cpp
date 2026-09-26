#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <cstdio>
#include <cstdint>
#include <stdexcept>
#include <array>

namespace {
constexpr wchar_t Property[]=L"ResourceManager.OwnedDcLifetimeProbe.20260909";
unsigned checks{},destroyed{},removed{};
struct Data { unsigned index; bool ended{}; };
void Check(bool result,const char* name)
{
    ++checks;std::printf("{\"check\":\"%s\",\"passed\":%s}\n",name,result?"true":"false");std::fflush(stdout);
    if(!result)throw std::runtime_error(name);
}
unsigned long long Number(const void* value){return reinterpret_cast<uintptr_t>(value);}
LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM w,LPARAM l)
{
    if(message==WM_NCDESTROY){
        auto* data=static_cast<Data*>(GetPropW(window,Property));
        if(data){
            const HANDLE old=RemovePropW(window,Property);
            const bool absent=GetPropW(window,Property)==nullptr;
            if(old==data&&absent)++removed;
            data->ended=true;++destroyed;
            std::printf("{\"windowDestroyed\":%u,\"hwnd\":%llu,\"removedOwnProperty\":%s,\"threadId\":%lu}\n",data->index,Number(window),old==data&&absent?"true":"false",GetCurrentThreadId());std::fflush(stdout);
        }
    }
    return DefWindowProcW(window,message,w,l);
}
void Observe(unsigned index,const char* phase,HWND expected,HDC dc)
{
    const HWND actual=WindowFromDC(dc);const DWORD type=GetObjectType(dc);
    std::printf("{\"dcCase\":%u,\"phase\":\"%s\",\"hwnd\":%llu,\"hdc\":%llu,\"windowFromDc\":%llu,\"objectType\":%lu,\"windowAlive\":%s,\"propertyPresent\":%s}\n",index,phase,Number(expected),Number(dc),Number(actual),type,IsWindow(expected)?"true":"false",GetPropW(expected,Property)?"true":"false");std::fflush(stdout);
}
HWND Make(const wchar_t* cls,Data& data,HWND parent=nullptr)
{
    const HWND window=CreateWindowExW(WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,cls,L"Owned DC lifetime",
        parent?WS_CHILD:WS_POPUP,0,0,32,32,parent,nullptr,GetModuleHandleW(nullptr),nullptr);
    Check(window&&!IsWindowVisible(window),"hidden-own-window-created");
    Check(SetPropW(window,Property,&data)&&GetPropW(window,Property)==&data,"window-record-property");
    return window;
}
void Ordinary(const wchar_t* cls,unsigned index,bool windowDc)
{
    Data data{index};const HWND window=Make(cls,data);
    HDC dc=windowDc?GetWindowDC(window):GetDC(window);
    Check(dc&&WindowFromDC(dc)==window&&GetObjectType(dc)==OBJ_DC,"acquired-real-window-dc");
    Observe(index,"acquired",window,dc);
    const int released=ReleaseDC(window,dc);
    std::printf("{\"dcRelease\":%u,\"returned\":%d}\n",index,released);std::fflush(stdout);
    Observe(index,"released-observation-only",window,dc);
    Check(GetPropW(window,Property)==&data&&!data.ended,"dc-release-does-not-destroy-window-record");
    const HDC next=windowDc?GetWindowDC(window):GetDC(window);
    Check(next&&WindowFromDC(next)==window,"reacquired-window-dc");
    Observe(index,"reacquired",window,next);
    std::printf("{\"dcReacquired\":%u,\"sameNumericHandle\":%s}\n",index,dc==next?"true":"false");
    ReleaseDC(window,next);
    Check(DestroyWindow(window)&&data.ended&&!IsWindow(window),"window-ended-through-ncdestroy");
    Observe(index,"after-window-destroy",window,next);
}
}
int main()
{
    SetErrorMode(32771);
    try{
        FILETIME b{},e{},k{},u{};Check(GetProcessTimes(GetCurrentProcess(),&b,&e,&k,&u),"process-times");
        BOOL inJob{};Check(IsProcessInJob(GetCurrentProcess(),nullptr,&inJob)&&inJob,"bounded-native-job");
        const auto birth=(static_cast<unsigned long long>(b.dwHighDateTime)<<32)|b.dwLowDateTime;
        std::printf("{\"pid\":%lu,\"creationFileTime\":%llu,\"threadId\":%lu,\"inJob\":true,\"errorMode\":32771}\n",GetCurrentProcessId(),birth,GetCurrentThreadId());
        const HWND foreground=GetForegroundWindow();
        const wchar_t* names[]{L"RmDcPrivate20260909",L"RmDcCommon20260909",L"RmDcClass20260909"};
        const UINT styles[]{CS_OWNDC,0,CS_CLASSDC};
        for(unsigned i=0;i<3;++i){WNDCLASSW cls{};cls.style=styles[i];cls.lpfnWndProc=Procedure;cls.hInstance=GetModuleHandleW(nullptr);cls.lpszClassName=names[i];Check(RegisterClassW(&cls),"register-owned-class");}
        Ordinary(names[0],0,false);Ordinary(names[1],1,false);Ordinary(names[0],2,true);
        Data first{3},second{4};HWND a=Make(names[2],first),other=Make(names[2],second);
        HDC ac=GetDC(a);Check(ac&&WindowFromDC(ac)==a,"class-first-associated");Observe(3,"class-first-acquire",a,ac);
        HDC bc=GetDC(other);Check(bc&&WindowFromDC(bc)==other,"class-second-associated");Observe(4,"class-second-acquire",other,bc);
        Observe(3,"after-class-second-acquire",a,ac);
        std::printf("{\"classShared\":true,\"sameNumericHandle\":%s,\"firstAssociationChanged\":%s}\n",ac==bc?"true":"false",WindowFromDC(ac)==other?"true":"false");
        ReleaseDC(other,bc);ReleaseDC(a,ac);
        Check(DestroyWindow(a)&&first.ended&&!second.ended,"class-first-window-only-ended");
        HDC retained=GetDC(other);Check(retained&&WindowFromDC(retained)==other,"class-other-window-still-valid");ReleaseDC(other,retained);
        Check(DestroyWindow(other)&&second.ended,"class-second-ended");
        HDC memory=CreateCompatibleDC(nullptr);Check(memory&&GetObjectType(memory)==OBJ_MEMDC&&!WindowFromDC(memory),"memory-dc-not-window");
        Observe(5,"memory-dc",nullptr,memory);Check(DeleteDC(memory),"memory-native-delete");
        Data parentData{6},childData{7};HWND parent=Make(names[1],parentData),child=Make(names[1],childData,parent);
        Check(DestroyWindow(parent)&&parentData.ended&&childData.ended&&!IsWindow(child),"parent-destroy-retires-child");
        Check(destroyed==7&&removed==7,"all-properties-removed-by-terminal-message");
        for(auto name:names)Check(UnregisterClassW(name,GetModuleHandleW(nullptr)),"normal-class-cleanup");
        Check(GetForegroundWindow()==foreground,"foreground-unchanged");
        std::printf("{\"passed\":true,\"checks\":%u,\"windowCount\":7,\"destroyed\":%u,\"propertiesRemoved\":%u,\"gpuCreated\":false,\"productDeployed\":false}\n",checks,destroyed,removed);
        return 0;
    }catch(const std::exception& error){std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n",checks,error.what());return 1;}
}
