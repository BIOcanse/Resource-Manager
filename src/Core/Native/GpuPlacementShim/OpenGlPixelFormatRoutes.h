#pragma once
#include "OpenGlCreationSelection.h"
#include "OpenGlNewWindowPreparation.h"
#if defined(_MSC_VER)
#include <intrin.h>
#endif

namespace ResourceManagerOpenGl::Runtime
{
inline decltype(&DescribePixelFormat) systemDescribePixelFormat{};
inline decltype(&SetPixelFormat) systemSetPixelFormat{};
inline decltype(&ChoosePixelFormat) systemChoosePixelFormat{};
inline decltype(&DrvDescribePixelFormat) sourceDescribePixelFormat{};

struct PixelFormatQuery;
inline thread_local const PixelFormatQuery* pixelFormatQuery{};
struct PixelFormatQuery final {
    const HDC dc;
    const PixelFormatDirectory& directory;
};
class PixelFormatQueryScope final {
    const PixelFormatQuery* const previous_;
public:
    explicit PixelFormatQueryScope(const PixelFormatQuery* query) noexcept
        : previous_(pixelFormatQuery) { pixelFormatQuery=query; }
    ~PixelFormatQueryScope() { pixelFormatQuery=previous_; }
    PixelFormatQueryScope(const PixelFormatQueryScope&)=delete;
    PixelFormatQueryScope& operator=(const PixelFormatQueryScope&)=delete;
};

LONG WINAPI DescribeChoiceFormats(HDC dc, INT format, ULONG capacity, PIXELFORMATDESCRIPTOR* output)
{
    const auto query=pixelFormatQuery;
    if(!query||query->dc!=dc)return sourceDescribePixelFormat(dc,format,capacity,output);
    // The system chooser adds its generic formats; this ICD view is native-only.
    if(!output||!capacity)return query->directory.NativeCount();
    if(format<=0||format>query->directory.NativeCount()){
        SetLastError(ERROR_INVALID_PARAMETER);return 0;
    }
    return query->directory.Describe(format,capacity,output)?query->directory.NativeCount():0;
}
struct PixelFormatSourceCallers {
    std::array<HMODULE,4> modules{};
    size_t count{};
    bool Contains(HMODULE module) const noexcept {
        for(size_t i=0;i<count;++i)if(modules[i]==module)return true;
        return false;
    }
};
// Published only by the original serialized installer, before enabling GDI hooks.
inline PixelFormatSourceCallers pixelFormatSourceCallers;

bool TargetPixelFormatView(HDC dc, const DrawableSnapshot& drawable, const void* caller, bool& targetView)
{
    targetView=false;
    if(!pixelFormatSourceCallers.count && drawable){SetLastError(ERROR_NOT_READY);return false;}
    MEMORY_BASIC_INFORMATION memory{};
    if(!VirtualQuery(caller,&memory,sizeof(memory)))return false;
    const auto module=static_cast<HMODULE>(memory.AllocationBase);
    if(pixelFormatSourceCallers.Contains(module)){
        if(drawable.target&&drawable.target->OwnsCaller(module)){
            SetLastError(ERROR_NOT_SUPPORTED);return false;
        }
        return true;
    }
    if(drawable.target && drawable.target->OwnsCaller(module)) {
        targetView=true;
        return true;
    }
    const auto selection=SelectCreation(dc);
    if(selection.error){SetLastError(selection.error);return false;}
    targetView=selection.target;
    return true;
}

int WINAPI ReadPixelFormat(HDC dc)
{
#if defined(_MSC_VER)
    const void* caller = _ReturnAddress();
#else
    const void* caller = __builtin_return_address(0);
#endif
    const DWORD incoming=GetLastError();
    const auto drawable = ReadWindowFormats(dc);
    bool target{};
    if(!TargetPixelFormatView(dc,drawable,caller,target))return 0;
    SetLastError(incoming);
    if(!target)return systemPixelFormat(dc);
    if(!drawable.target||!drawable.directory){SetLastError(ERROR_NOT_READY);return 0;}
    return drawable.installedPixelFormat;
}

int WINAPI ChoosePixelFormats(HDC dc, const PIXELFORMATDESCRIPTOR* description)
{
#if defined(_MSC_VER)
    const void* caller = _ReturnAddress();
#else
    const void* caller = __builtin_return_address(0);
#endif
    const DWORD incoming=GetLastError();
    const auto drawable=ReadWindowFormats(dc);
    bool target{};
    if(!TargetPixelFormatView(dc,drawable,caller,target))return 0;
    SetLastError(incoming);
    if(!target){
        const PixelFormatQueryScope source(nullptr);
        return systemChoosePixelFormat(dc,description);
    }
    if(!drawable.target||!drawable.directory){SetLastError(ERROR_NOT_READY);return 0;}
    if(pixelFormatQuery){SetLastError(ERROR_BUSY);return 0;}
    const PixelFormatQuery query{dc,*drawable.directory};
    const PixelFormatQueryScope scope(&query);
    return systemChoosePixelFormat(dc,description);
}

BOOL WINAPI WritePixelFormat(HDC dc, int format, const PIXELFORMATDESCRIPTOR* description)
{
#if defined(_MSC_VER)
    const void* caller = _ReturnAddress();
#else
    const void* caller = __builtin_return_address(0);
#endif
    const DWORD incoming = GetLastError();
    const auto drawable = ReadWindowFormats(dc);
    bool target{};
    if(!TargetPixelFormatView(dc,drawable,caller,target))return FALSE;
    SetLastError(incoming);
    if(!target)return drawables.InstallSystemPixelFormat(dc,format,description,systemSetPixelFormat,systemPixelFormat);
    if(!drawable && !PrepareNewWindowPixelFormat(dc,drawable))return FALSE;
    return drawables.InstallPixelFormat(dc,format);
}

int WINAPI DescribePixelFormats(HDC dc, int format, UINT capacity, PIXELFORMATDESCRIPTOR* output)
{
#if defined(_MSC_VER)
    const void* caller = _ReturnAddress();
#else
    const void* caller = __builtin_return_address(0);
#endif
    const DWORD incoming = GetLastError();
    const auto drawable = ReadWindowFormats(dc);
    bool target{};
    if(!TargetPixelFormatView(dc,drawable,caller,target))return 0;
    SetLastError(incoming);
    if(!target)return systemDescribePixelFormat(dc,format,capacity,output);
    if(!drawable.target||!drawable.directory){SetLastError(ERROR_NOT_READY);return 0;}
    return drawable.directory->Describe(format,capacity,output);
}
}
