#pragma once
#include <MinHook.h>
#include "OpenGlContextRoutes.h"
#include "OpenGlCoreRoutes.h"
#include "OpenGlWindowRoutes.h"

namespace ResourceManagerOpenGl::Runtime
{
template<typename F,typename T>
BOOL QueueHook(PROC address,F replacement,T* original) {
    if(!Callable(address))return Fail(ERROR_PROC_NOT_FOUND);
    if(MH_CreateHook(reinterpret_cast<void*>(address),reinterpret_cast<void*>(replacement),
        reinterpret_cast<void**>(original))!=MH_OK)return Fail(ERROR_GEN_FAILURE);
    if(MH_QueueEnableHook(reinterpret_cast<void*>(address))!=MH_OK)return Fail(ERROR_GEN_FAILURE);
    return TRUE;
}
BOOL ApplyHooks() {
    return MH_ApplyQueued()==MH_OK?TRUE:Fail(ERROR_GEN_FAILURE);
}
// Both callers hold the original configurationLock. Partial installs are retained,
// not retried; a successful public set reuses its own original trampolines.
enum class PublicCoreInstallation { None, Installed, Failed };
inline PublicCoreInstallation publicCoreInstallation{};
BOOL InstallPublicCore(HMODULE module) {
    if(!module)return Fail(ERROR_MOD_NOT_FOUND);
    if(publicCoreInstallation==PublicCoreInstallation::Installed)
        return module==sourceModuleReference?TRUE:Fail(ERROR_INVALID_HANDLE);
    if(publicCoreInstallation==PublicCoreInstallation::Failed)return Fail(ERROR_INVALID_STATE);
    publicCoreInstallation=PublicCoreInstallation::Failed;
    if(sourceModuleReference)return Fail(ERROR_ALREADY_INITIALIZED);
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(module),&sourceModuleReference))return FALSE;
    HMODULE provider{};
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&InstallPublicCore),&provider))return FALSE;
#define RM_WGL_CORE(Name,Replacement,Original) \
    if(!QueueHook(GetProcAddress(module,Name),&Replacement,&Original))return FALSE;
    RM_WGL_CORE("wglCreateContext",Create,systemCreate)
    RM_WGL_CORE("wglCreateLayerContext",CreateLayer,systemCreateLayer)
    RM_WGL_CORE("wglMakeCurrent",MakeCurrent,systemMakeCurrent)
    RM_WGL_CORE("wglGetCurrentContext",Current,systemCurrent)
    RM_WGL_CORE("wglGetCurrentDC",CurrentDC,systemCurrentDC)
    RM_WGL_CORE("wglDeleteContext",Delete,systemDelete)
    RM_WGL_CORE("wglGetProcAddress",Lookup,systemLookup)
    RM_WGL_CORE("wglShareLists",Share,systemShare)
#undef RM_WGL_CORE
#define RM_GL_CORE(Name,Index) if(!QueueHook(GetProcAddress(module,#Name),&Core_##Name::Invoke,&Core_##Name::original))return FALSE;
#include "OpenGlCoreEntries.inc"
#undef RM_GL_CORE
    if(!ApplyHooks())return FALSE;
    publicCoreInstallation=PublicCoreInstallation::Installed;
    return TRUE;
}
BOOL InstallCore(HMODULE module,PFN_GETDHGLRC driverHandle,GetBytesEntry previouslyResolved) {
    if(!module||!driverHandle||!previouslyResolved)return Fail(ERROR_INVALID_PARAMETER);
    if(originalVendorBytesAddress)return Fail(ERROR_ALREADY_INITIALIZED);
    if(!InstallPublicCore(module))return FALSE;
    sourceDriverHandle=driverHandle;
    originalVendorBytesAddress=Callback(previouslyResolved);
    if(!QueueHook(originalVendorBytesAddress,&Bytes,&originalVendorBytes))return FALSE;
    return ApplyHooks();
}
BOOL InstallModern(const ModernEntries& saved) {
    const PROC addresses[]{
#define RM_GL_MODERN(Name,Type) Callback(saved.Name),
#include "OpenGlModernEntries.inc"
#undef RM_GL_MODERN
    };
    for(size_t i=0;i<std::size(addresses);++i){
        if(!Callable(addresses[i]))return Fail(ERROR_PROC_NOT_FOUND);
        for(size_t j=0;j<i;++j)if(addresses[i]==addresses[j])return Fail(ERROR_INVALID_PARAMETER);
    }
#define RM_GL_MODERN(Name,Type) \
    Modern_##Name::originalAddress=Callback(saved.Name); \
    if(!QueueHook(Callback(saved.Name),&Modern_##Name::Invoke,&Modern_##Name::original))return FALSE;
#include "OpenGlModernEntries.inc"
#undef RM_GL_MODERN
    return ApplyHooks();
}
BOOL InstallWindows(const WindowEntries& savedSource,const WindowEntries& preparedTarget) {
    if(!current||pendingRelease)return Fail(ERROR_NOT_READY);
    if(!drawables.Read(boundDc).windowDispatch)return Fail(ERROR_NOT_READY);
    try{
        for(size_t mask=0;mask<windowExtensionStrings.size();++mask){
            auto& text=windowExtensionStrings[mask];text.clear();
            for(size_t i=0;i<std::size(coveredWindowExtensions);++i)if(mask&(size_t{1}<<i)){
                if(!text.empty())text+=' ';
                text+=coveredWindowExtensions[i];
            }
        }
    }catch(const std::bad_alloc&){return Fail(ERROR_NOT_ENOUGH_MEMORY);}
    const PROC sourceAddresses[]{
#define RM_WINDOW_ADDRESS(Name,Member,Type,HasDc,Replacement,PublicFormat) Callback(savedSource.Member),
        RM_WINDOW_ENTRIES(RM_WINDOW_ADDRESS)
#undef RM_WINDOW_ADDRESS
    };
    const PROC targetAddresses[]{
#define RM_WINDOW_ADDRESS(Name,Member,Type,HasDc,Replacement,PublicFormat) Callback(preparedTarget.Member),
        RM_WINDOW_ENTRIES(RM_WINDOW_ADDRESS)
#undef RM_WINDOW_ADDRESS
    };
    for(size_t i=0;i<std::size(sourceAddresses);++i)if(sourceAddresses[i]){
        if(!Callable(sourceAddresses[i]))return Fail(ERROR_INVALID_PARAMETER);
        for(size_t j=0;j<i;++j)if(sourceAddresses[i]==sourceAddresses[j])return Fail(ERROR_INVALID_PARAMETER);
        for(const auto target:targetAddresses)if(sourceAddresses[i]==target)return Fail(ERROR_INVALID_PARAMETER);
    }
#define RM_WINDOW_INSTALL(Name,Member,Type,HasDc,Replacement,PublicFormat) \
    if(savedSource.Member){ \
        Window_##Member::originalAddress=Callback(savedSource.Member); \
        if(!QueueHook(Callback(savedSource.Member),&Replacement,&Window_##Member::original))return FALSE; \
    }
    RM_WINDOW_ENTRIES(RM_WINDOW_INSTALL)
#undef RM_WINDOW_INSTALL
    return ApplyHooks();
}
BOOL InstallSwap(HMODULE module) {
    if(!module)return Fail(ERROR_INVALID_PARAMETER);
    if(!QueueHook(GetProcAddress(module,"SwapBuffers"),&Swap,&systemSwap))return FALSE;
    return ApplyHooks();
}
BOOL InstallPresent(decltype(&DrvPresentBuffers) source,decltype(&DrvPresentBuffers) target) {
    if(!source||!target||source==target)return Fail(ERROR_NOT_SUPPORTED);
    if(!QueueHook(Callback(source),&PresentBuffers,&sourcePresentBuffers))return FALSE;
    return ApplyHooks();
}
#undef RM_WINDOW_ENTRIES
}
