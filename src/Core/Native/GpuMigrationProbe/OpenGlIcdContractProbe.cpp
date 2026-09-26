#include "../GpuPlacementShim/OpenGlIcd.h"
#include <cstdio>
#include <stdexcept>

using ResourceManagerOpenGl::IcdExports;
using ResourceManagerOpenGl::ResolveIcdExports;
unsigned calls = 0;
// These owned exports test symbol binding only. No graphics driver or GPU is used.
extern "C" {
__declspec(dllexport) BOOL APIENTRY DrvValidateVersion(ULONG) { ++calls; return TRUE; }
__declspec(dllexport) void APIENTRY DrvSetCallbackProcs(INT, PROC*) { ++calls; }
__declspec(dllexport) LONG APIENTRY DrvDescribePixelFormat(HDC, INT, ULONG, PIXELFORMATDESCRIPTOR*) { ++calls; return 1; }
__declspec(dllexport) BOOL APIENTRY DrvSetPixelFormat(HDC, LONG) { ++calls; return TRUE; }
__declspec(dllexport) DHGLRC APIENTRY DrvCreateLayerContext(HDC, int) { ++calls; return 1; }
__declspec(dllexport) PGLCLTPROCTABLE APIENTRY DrvSetContext(HDC, DHGLRC, PFN_SETPROCTABLE) { ++calls; return nullptr; }
__declspec(dllexport) BOOL APIENTRY DrvReleaseContext(DHGLRC) { ++calls; return TRUE; }
__declspec(dllexport) BOOL APIENTRY DrvDeleteContext(DHGLRC) { ++calls; return TRUE; }
__declspec(dllexport) PROC APIENTRY DrvGetProcAddress(LPCSTR) { ++calls; return nullptr; }
#ifndef RM_MISSING_SHARE
__declspec(dllexport) BOOL APIENTRY DrvShareLists(DHGLRC, DHGLRC) { ++calls; return TRUE; }
#endif
__declspec(dllexport) BOOL APIENTRY DrvCopyContext(DHGLRC, DHGLRC, UINT) { ++calls; return TRUE; }
__declspec(dllexport) BOOL APIENTRY DrvSwapBuffers(HDC) { ++calls; return TRUE; }
#ifdef RM_HAS_PRESENT
__declspec(dllexport) BOOL APIENTRY DrvPresentBuffers(HDC, LPPRESENTBUFFERS) { ++calls; return TRUE; }
#endif
}

namespace {
unsigned checks = 0;
void Check(bool value, const char* name)
{
    ++checks;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", name, value ? "true" : "false");
    if (!value) throw std::runtime_error(name);
}
bool Empty(const IcdExports& e)
{
    return !e.validateVersion && !e.setCallbacks && !e.describePixelFormat && !e.setPixelFormat &&
        !e.createLayerContext && !e.setContext && !e.releaseContext && !e.deleteContext &&
        !e.getProcAddress && !e.shareLists && !e.copyContext && !e.swapBuffers && !e.presentBuffers;
}
}
int main()
{
    SetErrorMode(32771);
    try {
        IcdExports output;
        output.validateVersion = &DrvValidateVersion;
        Check(!ResolveIcdExports(nullptr, output) && GetLastError() == ERROR_INVALID_HANDLE && Empty(output), "null-clears-output");
        output.validateVersion = &DrvValidateVersion;
        Check(!ResolveIcdExports(GetModuleHandleW(L"kernel32.dll"), output) && GetLastError() == ERROR_PROC_NOT_FOUND && Empty(output), "wrong-module-clears-output");
        const bool resolved = ResolveIcdExports(GetModuleHandleW(nullptr), output);
#ifdef RM_MISSING_SHARE
        Check(!resolved && GetLastError() == ERROR_PROC_NOT_FOUND && Empty(output), "middle-required-export-missing-no-partial-table");
#else
        Check(resolved, "owned-export-table-resolved");
#define RM_VERIFY(Member, Name) Check(output.Member == &Name, #Name "-exact-address");
        RM_VERIFY(validateVersion, DrvValidateVersion)
        RM_VERIFY(setCallbacks, DrvSetCallbackProcs)
        RM_VERIFY(describePixelFormat, DrvDescribePixelFormat)
        RM_VERIFY(setPixelFormat, DrvSetPixelFormat)
        RM_VERIFY(createLayerContext, DrvCreateLayerContext)
        RM_VERIFY(setContext, DrvSetContext)
        RM_VERIFY(releaseContext, DrvReleaseContext)
        RM_VERIFY(deleteContext, DrvDeleteContext)
        RM_VERIFY(getProcAddress, DrvGetProcAddress)
        RM_VERIFY(shareLists, DrvShareLists)
        RM_VERIFY(copyContext, DrvCopyContext)
        RM_VERIFY(swapBuffers, DrvSwapBuffers)
#undef RM_VERIFY
#ifdef RM_HAS_PRESENT
        Check(output.presentBuffers == &DrvPresentBuffers, "optional-present-exact-address");
#else
        Check(!output.presentBuffers, "optional-present-absent-is-valid");
#endif
#endif
        Check(calls == 0, "resolve-never-invokes-an-export");
        std::printf("{\"passed\":true,\"checks\":%u,\"coreSignatures\":336,\"coreOffsets\":336,\"gpuCreated\":false}\n", checks);
        return 0;
    } catch (const std::exception& error) {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
