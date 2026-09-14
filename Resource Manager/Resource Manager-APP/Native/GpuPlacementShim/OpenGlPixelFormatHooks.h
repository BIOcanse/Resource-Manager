#pragma once
#include "OpenGlRuntimeHooks.h"
#include "OpenGlPixelFormatRoutes.h"

namespace ResourceManagerOpenGl::Runtime
{
// Same serialized, one-shot installation as the other runtime entries.
BOOL InstallPixelFormats(HMODULE gdi, HMODULE openGl, HMODULE sourceDriver)
{
    if (!gdi||!openGl||!sourceDriver) return Fail(ERROR_INVALID_PARAMETER);
    if(pixelFormatSourceCallers.count)return Fail(ERROR_ALREADY_INITIALIZED);
    IcdExports source{};
    if(!ResolveIcdExports(sourceDriver,source))return FALSE;
    const PROC attributes=source.getProcAddress("wglCreateContextAttribsARB");
    PixelFormatSourceCallers prepared;
    const void* addresses[]{openGl,sourceDriver,reinterpret_cast<const void*>(source.createLayerContext),
        Callable(attributes)?reinterpret_cast<const void*>(attributes):nullptr};
    for(const auto address:addresses){
        if(!address)continue;
        MEMORY_BASIC_INFORMATION memory{};
        if(!VirtualQuery(address,&memory,sizeof(memory)))return FALSE;
        const auto module=static_cast<HMODULE>(memory.AllocationBase);
        if(!prepared.Contains(module))prepared.modules[prepared.count++]=module;
    }
    for(size_t i=0;i<prepared.count;++i){
        HMODULE retained{};
        if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(prepared.modules[i]),&retained))return FALSE;
    }
    pixelFormatSourceCallers=prepared;
    if (!QueueHook(Callback(source.describePixelFormat), &DescribeChoiceFormats, &sourceDescribePixelFormat))
        return FALSE;
    if (!QueueHook(GetProcAddress(gdi, "ChoosePixelFormat"), &ChoosePixelFormats, &systemChoosePixelFormat))
        return FALSE;
    if (!QueueHook(GetProcAddress(gdi, "GetPixelFormat"), &ReadPixelFormat, &systemPixelFormat))
        return FALSE;
    if (!QueueHook(GetProcAddress(gdi, "DescribePixelFormat"), &DescribePixelFormats, &systemDescribePixelFormat))
        return FALSE;
    if (!QueueHook(GetProcAddress(gdi, "SetPixelFormat"), &WritePixelFormat, &systemSetPixelFormat))
        return FALSE;
    return ApplyHooks();
}
}
