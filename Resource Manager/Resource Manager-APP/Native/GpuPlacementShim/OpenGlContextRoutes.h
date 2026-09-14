#pragma once
#include "OpenGlRuntimeState.h"
#include "OpenGlCreationSelection.h"

namespace ResourceManagerOpenGl::Runtime
{
class ContextCreationCall
{
    const CreationSelection* const previous_ = activeCreationSelection;
    const CreationSelection selection_;
public:
    explicit ContextCreationCall(HDC dc) : selection_(SelectCreation(dc)) { activeCreationSelection = &selection_; }
    ~ContextCreationCall() { activeCreationSelection = previous_; }
    const CreationSelection& Selection() const noexcept { return selection_; }
    ContextCreationCall(const ContextCreationCall&) = delete;
    ContextCreationCall& operator=(const ContextCreationCall&) = delete;
    HGLRC Returned(HGLRC handle)
    {
        if (!handle || previous_ || internalContextWork) return handle;
        const DWORD error = GetLastError();
        {
            std::lock_guard<std::mutex> lock(bindingLock);
            const auto record = Find(handle);
            if (!selection_.target && !record)
                drawables.CommitSystemContextCreation(selection_.drawable.record, contexts.PublishApplicationCreation(nullptr));
            else if (record && record->token.load())
                drawables.CommitTargetPresentation(selection_.drawable.record, record->module, contexts.PublishApplicationCreation(record));
            if (deviceObservations) {
                pendingObservationCount = deviceObservations->Publish(ResourceManagerGpuObservation::Api::OpenGL,
                    ResourceManagerGpuObservation::Identity::Unavailable);
                pendingObservationContext = handle;
            }
        }
        SetLastError(error);
        return handle;
    }
};
DHGLRC WINAPI DriverHandle(HGLRC handle) {
    if(auto record=Find(handle)) return record->token.load();
    if(!sourceDriverHandle){SetLastError(ERROR_NOT_READY);return 0;}
    return sourceDriverHandle(handle);
}
void WINAPI SetTable(PGLCLTPROCTABLE table) { currentTable=table; }
BOOL Unbind() {
    auto owned=current ? current : pendingRelease;
    if (owned) {
        if (!owned->driver.releaseContext(owned->token.load())) {
            pendingRelease=owned; current=nullptr; currentTable=nullptr; boundDc=nullptr;
            return FALSE;
        }
        owned->owner.Reset(); current=nullptr; pendingRelease=nullptr; currentTable=nullptr; boundDc=nullptr;
        return TRUE;
    }
    return systemCurrent() ? systemMakeCurrent(nullptr,nullptr) : TRUE;
}
HGLRC CreateLayerImpl(HDC dc,int layer,const CreationSelection& selection) {
    if(selection.error){SetLastError(selection.error);return nullptr;}
    if(!selection.target) return systemCreateLayer(dc,layer);
    const auto& drawable=selection.drawable;
    if(!drawable.target||!drawable.directory){SetLastError(ERROR_NOT_READY);return nullptr;}
    if(!drawable.installedPixelFormat){SetLastError(ERROR_INVALID_PIXEL_FORMAT);return nullptr;}
    std::lock_guard<std::mutex> lock(bindingLock);
    return Handle(contexts.Create(drawable.target->module,drawable.target->driver,dc,layer,drawable.windowDispatch));
}
HGLRC WINAPI CreateLayer(HDC dc,int layer) {
    ObserveApiCall();
    if (UseNativeObservationRoute()) return systemCreateLayer(dc, layer);
    ContextCreationCall call(dc);
    return call.Returned(CreateLayerImpl(dc,layer,call.Selection()));
}
HGLRC CreateImpl(HDC dc,const CreationSelection& selection) {
    if(selection.error){SetLastError(selection.error);return nullptr;}
    if(!selection.target)return systemCreate(dc);
    const auto& drawable=selection.drawable;
    if(!drawable.target||!drawable.directory){SetLastError(ERROR_NOT_READY);return nullptr;}
    if(!drawable.installedPixelFormat){SetLastError(ERROR_INVALID_PIXEL_FORMAT);return nullptr;}
    std::lock_guard<std::mutex> lock(bindingLock);
    return Handle(contexts.Create(drawable.target->module,drawable.target->driver,dc,0,drawable.windowDispatch));
}
HGLRC WINAPI Create(HDC dc) {
    ObserveApiCall();
    if (UseNativeObservationRoute()) return systemCreate(dc);
    ContextCreationCall call(dc);
    return call.Returned(CreateImpl(dc,call.Selection()));
}
BOOL WINAPI MakeCurrent(HDC dc,HGLRC handle) {
    ObserveApiCall();
    if (UseNativeObservationRoute()) return systemMakeCurrent(dc, handle);
    std::lock_guard<std::mutex> lock(bindingLock);
    if(pendingRelease && handle) return Fail(ERROR_BUSY);
    if(!Unbind()) return FALSE;
    if(!handle) return TRUE;
    auto record=Find(handle);
    if(!record) {
        const BOOL result=systemMakeCurrent(dc,handle);
        if(result) ObserveCurrentContext(handle);
        return result;
    }
    if(!record->token.load()) return Fail(ERROR_INVALID_HANDLE);
    const auto drawable=drawables.Read(dc);
    if(!systemPixelFormat)return Fail(ERROR_NOT_READY);
    if(!drawable || !drawable.target || !drawable.directory || !drawable.installedPixelFormat || record->module!=drawable.target->module || systemPixelFormat(dc)!=drawable.sourcePixelFormat) return Fail(ERROR_INVALID_PIXEL_FORMAT);
    if(!record->owner.CanBind()) return FALSE;
    ResourceManagerOpenGl::ThreadOwner prepared;
    if(!prepared.CaptureCurrent()) return FALSE;
    PGLCLTPROCTABLE table=record->driver.setContext(dc,record->token.load(),&SetTable);
    if(!table) { currentTable=nullptr; return FALSE; }
    record->owner=std::move(prepared);
    if(table->cEntries!=336) {
        pendingRelease=record;
        Unbind();return Fail(ERROR_INVALID_DATA);
    }
    currentTable=table; current=record; boundDc=dc;
    if(!record->modernResolved) {
        // Resolve the currently supported typed modern entries on explicit binding.
        if(!ResolveModern(record->modern,record->driver.getProcAddress)) {Unbind();return Fail(ERROR_NOT_SUPPORTED);}
        record->modernResolved=true;
    }
    if (!internalContextWork && !activeCreationSelection)
        drawables.CommitTargetPresentation(drawable.record, record->module, record->applicationCreationOrder);
    ObserveCurrentContext(handle);
    return TRUE;
}
HGLRC WINAPI Current() { ObserveApiCall(); return current ? Handle(current) : systemCurrent(); }
HDC WINAPI CurrentDC() { ObserveApiCall(); return current ? boundDc : systemCurrentDC(); }
BOOL WINAPI Delete(HGLRC handle) {
    ObserveApiCall();
    if (UseNativeObservationRoute()) return systemDelete(handle);
    std::lock_guard<std::mutex> lock(bindingLock);
    auto record=Find(handle);
    if(!record) {
        const BOOL result=systemDelete(handle);
        if(result) ObserveDeletedContext(handle);
        return result;
    }
    if(!record->token.load()) return Fail(ERROR_INVALID_HANDLE);
    if(!record->owner.CanBind()) return FALSE;
    if((current==record || pendingRelease==record) && !Unbind()) return FALSE;
    const BOOL result=contexts.DestroyUnbound(record);
    if(result) ObserveDeletedContext(handle);
    return result;
}
BOOL WINAPI Share(HGLRC first,HGLRC second) {
    ObserveApiCall();
    if (UseNativeObservationRoute()) return systemShare(first, second);
    auto a=Find(first), b=Find(second);
    if(!a && !b) return systemShare(first,second);
    if(!a || !b) return Fail(ERROR_NOT_SUPPORTED);
    std::lock_guard<std::mutex> lock(bindingLock);
    if(!a->token.load() || !b->token.load()) return Fail(ERROR_INVALID_HANDLE);
    if(a->module!=b->module)return Fail(ERROR_NOT_SUPPORTED);
    return a->driver.shareLists(a->token.load(),b->token.load());
}
HGLRC AttributesImpl(HDC dc,HGLRC share,const int* attributes,const CreationSelection& selection) {
    if(selection.error){SetLastError(selection.error);return nullptr;}
    if(!selection.target) {
        if(Find(share)) {SetLastError(ERROR_NOT_SUPPORTED);return nullptr;}
        if(!systemCreateAttributes) {SetLastError(ERROR_NOT_SUPPORTED);return nullptr;}
        return systemCreateAttributes(dc,share,attributes);
    }
    const auto& drawable=selection.drawable;
    if(!drawable.target||!drawable.directory){SetLastError(ERROR_NOT_READY);return nullptr;}
    if(!drawable.installedPixelFormat){SetLastError(ERROR_INVALID_PIXEL_FORMAT);return nullptr;}
    const auto shared=Find(share);
    if(share && (!shared || shared->module!=drawable.target->module)) {SetLastError(ERROR_NOT_SUPPORTED);return nullptr;}
    ResourceManagerOpenGl::ContextUse use(shared,bindingLock);
    if(shared && !use) return nullptr;
    const auto target=drawable.windowDispatch?drawable.windowDispatch->functions.createAttributes:nullptr;
    if(!target) {SetLastError(ERROR_NOT_SUPPORTED);return nullptr;}
    return target(dc,share,attributes);
}
HGLRC WINAPI Attributes(HDC dc,HGLRC share,const int* attributes) {
    ContextCreationCall call(dc);
    return call.Returned(AttributesImpl(dc,share,attributes,call.Selection()));
}
void APIENTRY Bytes(GLenum name,GLubyte* value) {
    if(current) {
        PROC entry=current->driver.getProcAddress("glGetUnsignedBytevEXT"); GetBytesEntry fn{};
        std::memcpy(&fn,&entry,sizeof(fn)); if(fn) fn(name,value);
    } else if(systemCurrent()) originalVendorBytes(name,value);
}
PROC WINAPI Lookup(LPCSTR name) {
    ObserveApiCall();
    if (UseNativeObservationRoute()) return systemLookup(name);
    if(!name) return nullptr;
    // The argument-owned WGL wrappers select their HDC/context only when called.
    if(current) {
        if(std::strcmp(name,"wglCreateContextAttribsARB")==0)return Callback(&Attributes);
        PROC window{};
        if(WindowLookup(name,nullptr,window))return window;
    }
    PROC entry=current ? current->driver.getProcAddress(name) : systemLookup(name);
    if(!Callable(entry)) return nullptr;
    if(std::strcmp(name,"wglCreateContextAttribsARB")==0) return Callback(&Attributes);
    if(std::strcmp(name,"glGetUnsignedBytevEXT")==0)
        return current || entry==originalVendorBytesAddress ? Callback(&Bytes) : entry;
    PROC window{};
    if(WindowLookup(name,entry,window))return window;
    // Untested WGL extensions must not receive the proxy context handle.
    if(current && std::strncmp(name,"wgl",3)==0) return nullptr;
    return ModernLookup(name,entry);
}
}
