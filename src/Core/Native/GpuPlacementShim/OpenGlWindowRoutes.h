#pragma once
#include "OpenGlContextRoutes.h"

namespace ResourceManagerOpenGl::Runtime
{
#define RM_WINDOW_ENTRIES(X) \
 X(wglGetExtensionsStringARB,arb,PFNWGLGETEXTENSIONSSTRINGARBPROC,true,WindowExtensionsArb,false) \
 X(wglGetExtensionsStringEXT,ext,PFNWGLGETEXTENSIONSSTRINGEXTPROC,false,WindowExtensionsExt,false) \
 X(wglChoosePixelFormatARB,choose,PFNWGLCHOOSEPIXELFORMATARBPROC,true,Window_choose::Invoke,true) \
 X(wglGetPixelFormatAttribivARB,ints,PFNWGLGETPIXELFORMATATTRIBIVARBPROC,true,Window_ints::Invoke,true) \
 X(wglGetPixelFormatAttribfvARB,floats,PFNWGLGETPIXELFORMATATTRIBFVARBPROC,true,Window_floats::Invoke,true) \
 X(wglSwapIntervalEXT,setInterval,PFNWGLSWAPINTERVALEXTPROC,false,Window_setInterval::Invoke,false) \
 X(wglGetSwapIntervalEXT,getInterval,PFNWGLGETSWAPINTERVALEXTPROC,false,Window_getInterval::Invoke,false) \
 X(wglMakeContextCurrentARB,makeRead,PFNWGLMAKECONTEXTCURRENTARBPROC,true,WindowMakeRead,false) \
 X(wglGetCurrentReadDCARB,readDc,PFNWGLGETCURRENTREADDCARBPROC,false,Window_readDc::Invoke,false)
struct WindowEntries {
#define RM_WINDOW_FIELD(Name,Member,Type,HasDc,Replacement,PublicFormat) Type Member{};
    RM_WINDOW_ENTRIES(RM_WINDOW_FIELD)
#undef RM_WINDOW_FIELD
};
using WindowFunctions = ResourceManagerOpenGl::WindowFunctions;
enum WindowExtensionIndex : size_t { ArbDiscovery,ExtDiscovery,PixelFormat,SwapControl,CreateContext,CreateProfile,MakeCurrentRead,Multisample,ArbFramebufferSrgb,ExtFramebufferSrgb,PixelFormatFloat,WindowExtensionCount };
constexpr const char* coveredWindowExtensions[]{
    "WGL_ARB_extensions_string","WGL_EXT_extensions_string","WGL_ARB_pixel_format",
    "WGL_EXT_swap_control","WGL_ARB_create_context","WGL_ARB_create_context_profile","WGL_ARB_make_current_read",
    "WGL_ARB_multisample","WGL_ARB_framebuffer_sRGB","WGL_EXT_framebuffer_sRGB","WGL_ARB_pixel_format_float"
};
std::array<std::string,size_t{1}<<std::size(coveredWindowExtensions)> windowExtensionStrings;
static_assert(std::size(coveredWindowExtensions)==WindowExtensionCount);
constexpr size_t ExtensionBit(WindowExtensionIndex index) {return size_t{1}<<index;}
size_t PreparedWindowMask(const WindowFunctions& entries) {
    const bool createAvailable=entries.createAttributes!=nullptr;
    const bool formatAvailable=entries.choose&&entries.ints&&entries.floats;
    const bool available[]{bool(entries.arb),bool(entries.ext),formatAvailable,bool(entries.setInterval&&entries.getInterval),createAvailable,createAvailable,bool(entries.makeRead&&entries.readDc),formatAvailable,formatAvailable,formatAvailable,formatAvailable};
    static_assert(std::size(available)==WindowExtensionCount);
    size_t mask=0;for(size_t i=0;i<std::size(available);++i)if(available[i])mask|=size_t{1}<<i;
    return mask;
}

template<auto Member,typename Signature,bool HasDc> struct WindowRoute;
template<auto Member,bool HasDc,typename Result,typename... Args>
struct WindowRoute<Member,Result(WINAPI*)(Args...),HasDc> {
    static inline Result(WINAPI* original)(Args...){};
    static inline PROC originalAddress{};
    static bool Available(const ContextReference& record) {
        return record->windowDispatch && record->windowDispatch->functions.*Member;
    }
    static Result WINAPI Invoke(Args... args) {
        ResourceManagerOpenGl::WindowDispatchReference prepared;
        bool owned{};
        if constexpr(HasDc) {
            const auto selection=SelectCreation(std::get<0>(std::forward_as_tuple(args...)));
            if(selection.error){SetLastError(selection.error);return Result{};}
            owned=selection.target;prepared=selection.drawable.windowDispatch;
        }else {const auto record=current;owned=bool(record);if(record)prepared=record->windowDispatch;}
        if(pendingRelease&&(owned||!HasDc)){SetLastError(ERROR_BUSY);return Result{};}
        if(owned&&!prepared){SetLastError(ERROR_NOT_READY);return Result{};}
        const auto function=owned?prepared->functions.*Member:original;
        if(!function){SetLastError(ERROR_NOT_SUPPORTED);return Result{};}
        return function(args...);
    }
};
using Window_arb=WindowRoute<&WindowFunctions::arb,PFNWGLGETEXTENSIONSSTRINGARBPROC,true>;
using Window_ext=WindowRoute<&WindowFunctions::ext,PFNWGLGETEXTENSIONSSTRINGEXTPROC,false>;
using Window_setInterval=WindowRoute<&WindowFunctions::setInterval,PFNWGLSWAPINTERVALEXTPROC,false>;
using Window_getInterval=WindowRoute<&WindowFunctions::getInterval,PFNWGLGETSWAPINTERVALEXTPROC,false>;
using Window_makeRead=WindowRoute<&WindowFunctions::makeRead,PFNWGLMAKECONTEXTCURRENTARBPROC,true>;
using Window_readDc=WindowRoute<&WindowFunctions::readDc,PFNWGLGETCURRENTREADDCARBPROC,false>;
using Window_choose=WindowRoute<&WindowFunctions::choose,PFNWGLCHOOSEPIXELFORMATARBPROC,true>;
using Window_ints=WindowRoute<&WindowFunctions::ints,PFNWGLGETPIXELFORMATATTRIBIVARBPROC,true>;
using Window_floats=WindowRoute<&WindowFunctions::floats,PFNWGLGETPIXELFORMATATTRIBFVARBPROC,true>;
#define RM_WINDOW_TYPE(Name,Member,Type,HasDc,Replacement,PublicFormat) static_assert(std::is_same_v<decltype(&Window_##Member::Invoke),Type>,"WGL route type mismatch");
RM_WINDOW_ENTRIES(RM_WINDOW_TYPE)
#undef RM_WINDOW_TYPE

BOOL WINAPI WindowMakeRead(HDC draw,HDC read,HGLRC context) {
    if(!context)return MakeCurrent(nullptr,nullptr);
    if(pendingRelease)return Fail(ERROR_BUSY);
    const auto record=Find(context);
    const bool owned=record!=nullptr;
    const auto drawRecord=drawables.Read(draw),readRecord=drawables.Read(read);
    if(owned && (!drawRecord||(read&&!readRecord)))
        return Fail(ERROR_INCOMPATIBLE_DEVICE_CONTEXTS_ARB);
    if(owned){
        if(!record->windowDispatch||!drawRecord.target||!drawRecord.directory||(read&&(!readRecord.target||!readRecord.directory)))
            return Fail(ERROR_NOT_READY);
        if(!drawRecord.installedPixelFormat||(read&&!readRecord.installedPixelFormat))
            return Fail(ERROR_INVALID_PIXEL_FORMAT);
        if(drawRecord.target->module!=record->module||(read&&readRecord.target->module!=record->module))
            return Fail(ERROR_INCOMPATIBLE_DEVICE_CONTEXTS_ARB);
    }
    const auto function=owned?record->windowDispatch->functions.makeRead:Window_makeRead::original;
    if(!function)return Fail(ERROR_NOT_SUPPORTED);
    ResourceManagerOpenGl::ContextUse use(record,bindingLock);
    if(owned&&!use)return FALSE;
    // The native extension re-enters MakeCurrent on a context change.
    return function(draw,read,context);
}
static_assert(std::is_same_v<decltype(&WindowMakeRead),PFNWGLMAKECONTEXTCURRENTARBPROC>);

bool WindowExtensionToken(const char* text,const char* name) {
    if(!text) return false;
    const size_t length=std::strlen(name);
    for(const char* at=text;*at;) {
        while(*at==' ')++at;
        const char* end=at;while(*end && *end!=' ')++end;
        if(static_cast<size_t>(end-at)==length && std::memcmp(at,name,length)==0)return true;
        at=end;
    }
    return false;
}
const char* CoveredWindowExtensions(const char* native,size_t availableWindowExtensions) {
    if(!native)return nullptr;
    size_t mask=0;
    for(size_t i=0;i<std::size(coveredWindowExtensions);++i)
        if((availableWindowExtensions&(size_t{1}<<i)) && WindowExtensionToken(native,coveredWindowExtensions[i]))mask|=size_t{1}<<i;
    if(!(mask&ExtensionBit(ArbDiscovery)))mask&=~(ExtensionBit(PixelFormat)|ExtensionBit(CreateContext)|ExtensionBit(CreateProfile)|ExtensionBit(MakeCurrentRead));
    if(!(mask&ExtensionBit(ExtDiscovery)))mask&=~ExtensionBit(SwapControl);
    if(!(mask&ExtensionBit(CreateContext)))mask&=~ExtensionBit(CreateProfile);
    if(!(mask&ExtensionBit(PixelFormat)))mask&=~(ExtensionBit(Multisample)|ExtensionBit(ArbFramebufferSrgb)|ExtensionBit(ExtFramebufferSrgb)|ExtensionBit(PixelFormatFloat));
    return windowExtensionStrings[mask].c_str();
}
const char* WINAPI WindowExtensionsArb(HDC dc) {
    const auto selection=SelectCreation(dc);
    if(selection.error){SetLastError(selection.error);return nullptr;}
    const auto& drawable=selection.drawable;
    if(pendingRelease&&selection.target){SetLastError(ERROR_BUSY);return nullptr;}
    if(selection.target&&!drawable.windowDispatch){SetLastError(ERROR_NOT_READY);return nullptr;}
    const auto function=selection.target?drawable.windowDispatch->functions.arb:Window_arb::original;
    if(!function){SetLastError(ERROR_NOT_SUPPORTED);return nullptr;}
    const char* native=function(dc);
    return selection.target ? CoveredWindowExtensions(native,PreparedWindowMask(drawable.windowDispatch->functions)) : native;
}
const char* WINAPI WindowExtensionsExt() {
    const auto record=current;
    if(pendingRelease){SetLastError(ERROR_BUSY);return nullptr;}
    if(record&&!record->windowDispatch){SetLastError(ERROR_NOT_READY);return nullptr;}
    const auto function=record?record->windowDispatch->functions.ext:Window_ext::original;
    if(!function){SetLastError(ERROR_NOT_SUPPORTED);return nullptr;}
    const char* native=function();
    return record ? CoveredWindowExtensions(native,PreparedWindowMask(record->windowDispatch->functions)) : native;
}
bool WindowLookup(LPCSTR name,PROC nativeEntry,PROC& result) {
#define RM_WINDOW_LOOKUP(Name,Member,Type,HasDc,Replacement,PublicFormat) \
 if(std::strcmp(name,#Name)==0) { \
  if constexpr(PublicFormat) { \
   if(current)result=Callback(&Replacement); \
   else result=nativeEntry==Window_##Member::originalAddress ? Callback(&Replacement) : nativeEntry; \
  } \
  else if(current) { \
   if constexpr(HasDc)result=Callback(&Replacement); \
   else result=Window_##Member::Available(current) ? Callback(&Replacement) : nullptr; \
  }else result=nativeEntry==Window_##Member::originalAddress ? Callback(&Replacement) : nativeEntry; \
  return true; \
 }
    RM_WINDOW_ENTRIES(RM_WINDOW_LOOKUP)
#undef RM_WINDOW_LOOKUP
    return false;
}
WindowEntries CaptureWindowEntries(decltype(&DrvGetProcAddress) resolver) {
    WindowEntries result{};
    if(!resolver){SetLastError(ERROR_INVALID_PARAMETER);return result;}
#define RM_WINDOW_CAPTURE(Name,Member,Type,HasDc,Replacement,PublicFormat) \
 {const PROC address=resolver(#Name);if(Callable(address))std::memcpy(&result.Member,&address,sizeof(address));}
    RM_WINDOW_ENTRIES(RM_WINDOW_CAPTURE)
#undef RM_WINDOW_CAPTURE
    return result;
}
inline decltype(&SwapBuffers) systemSwap{};
inline decltype(&DrvPresentBuffers) sourcePresentBuffers{};
BOOL WINAPI PresentBuffers(HDC dc, LPPRESENTBUFFERS data) {
    const auto drawable=drawables.Read(dc);
    if(data && drawable.target
        && data->luidAdapter.LowPart==drawable.target->adapterLuid.LowPart
        && data->luidAdapter.HighPart==drawable.target->adapterLuid.HighPart) {
        const auto present=drawable.target->driver.presentBuffers;
        if(!present)return Fail(ERROR_NOT_SUPPORTED);
        return present(dc,data);
    }
    return sourcePresentBuffers(dc,data);
}
BOOL WINAPI Swap(HDC dc) {
    const auto drawable=drawables.Read(dc);
    if(!drawable || !drawable.targetPresentation)return systemSwap(dc);
    if(!drawable.target||!drawable.directory)return Fail(ERROR_NOT_READY);
    if(!drawable.installedPixelFormat)return Fail(ERROR_INVALID_PIXEL_FORMAT);
    return drawable.target->driver.swapBuffers(dc);
}
}
