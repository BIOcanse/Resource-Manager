#pragma once
#include "OpenGlRuntimeState.h"

namespace ResourceManagerOpenGl::Runtime
{
template<auto Member,typename Signature> struct CoreRoute;
template<auto Member,typename Result,typename... Args>
struct CoreRoute<Member,Result(APIENTRY*)(Args...)> {
    static inline Result(APIENTRY* original)(Args...){};
    static Result APIENTRY Invoke(Args... args) {
        ObserveApiCall();
        if(current) return (currentTable->glDispatchTable.*Member)(args...);
        return original(args...);
    }
};
#define RM_GL_CORE(Name,Index) \
    using Core_##Name=CoreRoute<&GLDISPATCHTABLE::Name,decltype(GLDISPATCHTABLE::Name)>; \
    static_assert(std::is_same_v<decltype(&::Name),decltype(GLDISPATCHTABLE::Name)>,"Public/table signature mismatch"); \
    static_assert(std::is_same_v<decltype(&Core_##Name::Invoke),decltype(GLDISPATCHTABLE::Name)>,"Proxy signature mismatch"); \
    static_assert(offsetof(GLDISPATCHTABLE,Name)==Index*sizeof(PROC),"Dispatch order mismatch");
#include "OpenGlCoreEntries.inc"
#undef RM_GL_CORE
static_assert(sizeof(GLDISPATCHTABLE)==336*sizeof(PROC),"Complete core table");
bool ResolveModern(ModernEntries& entries,decltype(&DrvGetProcAddress) resolve) {
    bool available=true;
#define RM_GL_MODERN(Name,Type) {PROC entry=resolve(#Name);if(!Callable(entry)||ModernHookAddress(entry))entry=nullptr;std::memcpy(&entries.Name,&entry,sizeof(entry));available=available&&entry;}
#include "OpenGlModernEntries.inc"
#undef RM_GL_MODERN
    return available;
}
template<auto Member,typename Signature> struct ModernRoute;
template<auto Member,typename Result,typename... Args>
struct ModernRoute<Member,Result(APIENTRY*)(Args...)> {
    static inline Result(APIENTRY* original)(Args...){};
    static inline PROC originalAddress{};
    static Result APIENTRY Invoke(Args... args) {
        if(current) return (current->modern.*Member)(args...);
        if(pendingRelease) {
            if constexpr(std::is_same_v<Result,GLint>) return -1;
            else if constexpr(!std::is_void_v<Result>) return Result{};
            else return;
        }
        // Native workers may have a driver context without public WGL binding.
        return original(args...);
    }
};
#define RM_GL_MODERN(Name,Type) \
    using Modern_##Name=ModernRoute<&ModernEntries::Name,Type>; \
    static_assert(std::is_same_v<decltype(&Modern_##Name::Invoke),Type>,"Modern proxy signature mismatch");
#include "OpenGlModernEntries.inc"
#undef RM_GL_MODERN

PROC ModernLookup(LPCSTR name,PROC nativeEntry) {
#define RM_GL_MODERN(Name,Type) \
    if(std::strcmp(name,#Name)==0) return current || nativeEntry==Modern_##Name::originalAddress ? Callback(&Modern_##Name::Invoke) : nativeEntry;
#include "OpenGlModernEntries.inc"
#undef RM_GL_MODERN
    return nativeEntry;
}
bool ModernHookAddress(PROC address) {
#define RM_GL_MODERN(Name,Type) if(address==Modern_##Name::originalAddress || address==Callback(&Modern_##Name::Invoke))return true;
#include "OpenGlModernEntries.inc"
#undef RM_GL_MODERN
    return false;
}
}
