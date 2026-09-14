#pragma once
#include "OpenGlAdapterIcd.h"
#include "OpenGlSystemBootstrap.h"
#include "OpenGlRuntimeIcd.h"
#include "OpenGlPixelFormatHooks.h"
#include "OpenGlPixelFormatPreparation.h"
#include "OpenGlWindowPreparation.h"
#include "OpenGlWindowThread.h"

namespace ResourceManagerOpenGl::Runtime
{
struct InstalledTarget
{
    HMODULE module{};
    IcdExports driver{};
    LUID adapter{};
    std::shared_ptr<const PixelFormatDirectory> directory;
    WindowDispatchReference windows;
};

// Original Configure serializes installation. Retained code and callback storage
// belong to the original installers, including a partially installed set.
inline SystemBootstrapResult InstallPreparedTarget(ResolvedIcdCallbacks&& callbacks,
    LUID adapter, const PIXELFORMATDESCRIPTOR& format, SIZE size, int maximumFormats,
    InstalledTarget& output, CreationPolicyReader policyReader)
{
    InternalContextWork internalWork;
    if (!policyReader || maximumFormats <= 0 || (!adapter.LowPart && !adapter.HighPart)
        || !callbacks.Callbacks().pfnGetDhglrc || !callbacks.Callbacks().pfnGetAdapterLuid)
        return {ERROR_INVALID_PARAMETER, 0};
    if (sourceIcdCallbacks.Callbacks().pfnGetAdapterLuid) return {ERROR_ALREADY_INITIALIZED, 0};
    const DWORD priorError = GetLastError();
    SystemEntries system;
    if (!ResolveSystemEntries(GetModuleHandleW(L"opengl32.dll"), system)) {
        const DWORD failure = GetLastError(); SetLastError(priorError); return {failure, 0};
    }
    const auto error = [] { const DWORD value = GetLastError(); return value ? value : static_cast<DWORD>(ERROR_GEN_FAILURE); };
    const auto require = [&](bool okay) { if (!okay) throw error(); };
    HMODULE sourceModule{}, targetModule{};
    DWORD callbackCleanup{};
    bool observersTouched{};
    InstalledTarget candidate;
    auto result = ExecuteSystemBootstrap(format, size, [&](HDC dc, HGLRC, int sourceFormat) -> DWORD {
        DWORD operationError{};
        HGLRC temporary{};
        try {
            const auto openGl = GetModuleHandleW(L"opengl32.dll");
            const auto gdi = GetModuleHandleW(L"gdi32.dll");
            const auto bytesAddress = system.lookup("glGetUnsignedBytevEXT");
            require(Callable(bytesAddress));
            GetBytesEntry bytes{}; std::memcpy(&bytes, &bytesAddress, sizeof(bytes));
            LUID sourceAdapter{}; bytes(0x9599, reinterpret_cast<GLubyte*>(&sourceAdapter));
            if (system.error() != GL_NO_ERROR || (!sourceAdapter.LowPart && !sourceAdapter.HighPart))
                throw static_cast<DWORD>(ERROR_NOT_SUPPORTED);
            GlInfo sourceInfo{}, targetInfo{};
            const LUID selections[]{sourceAdapter, adapter};
            GlInfo* infos[]{&sourceInfo, &targetInfo};
            for (size_t i = 0; i < std::size(selections); ++i) {
                const auto query = QueryAdapterIcd(selections[i], *infos[i]);
                if (query.cleanupError && !callbackCleanup) callbackCleanup = query.cleanupError;
                if (!query.Succeeded()) throw query.error ? query.error : query.cleanupError;
            }
            require(GetModuleHandleExW(0, sourceInfo.filename, &sourceModule));
            IcdExports sourceDriver{}, targetDriver{};
            require(ResolveIcdExports(sourceModule, sourceDriver));
            ModernEntries modern{};
            if (!ResolveModern(modern, system.lookup)) throw static_cast<DWORD>(ERROR_NOT_SUPPORTED);
            const auto sourceWindows = CaptureWindowEntries(system.lookup);
            const auto sourceAttributes = system.lookup("wglCreateContextAttribsARB");
            if (Callable(sourceAttributes))
                std::memcpy(&systemCreateAttributes, &sourceAttributes, sizeof(systemCreateAttributes));
            require(system.makeCurrent(nullptr, nullptr));
            targetModule = LoadLibraryW(targetInfo.filename); require(targetModule != nullptr);
            require(ResolveIcdExports(targetModule, targetDriver));
            if (sourceDriver.setCallbacks == targetDriver.setCallbacks) throw static_cast<DWORD>(ERROR_NOT_SUPPORTED);
            require(InstallIcdCallbacks(std::move(callbacks), targetModule, targetDriver, targetInfo.version));
            require(InstallPixelFormats(gdi, openGl, sourceModule));
            observersTouched = true;
            const auto record = PrepareObservedDrawable(WindowFromDC(dc)); require(record != nullptr);
            const auto target = PrepareTarget(targetModule, targetDriver, adapter); require(target != nullptr);
            const auto directory = PrepareFormatDirectory(dc, sourceDescribePixelFormat,
                targetDriver.describePixelFormat, systemDescribePixelFormat, maximumFormats);
            require(directory != nullptr);
            require(drawables.PublishTarget(record, target, sourceFormat));
            require(drawables.PublishDirectory(record, directory));
            const int selected = ChoosePixelFormat(dc, &format); require(selected > 0);
            require(SetPixelFormat(dc, selected, &format));
            creationPolicyReader.store(policyReader);
            require(InstallCore(openGl, sourceIcdCallbacks.Callbacks().pfnGetDhglrc, bytes));
            require(InstallModern(modern));
            temporary = system.create(dc); require(temporary != nullptr);
            require(system.makeCurrent(dc, temporary));
            const auto targetWindows = CaptureWindowEntries(targetDriver.getProcAddress);
            const auto dispatch = PrepareWindowDispatch(targetWindows); require(dispatch != nullptr);
            require(drawables.PublishWindowDispatch(record, dispatch));
            require(InstallWindows(sourceWindows, targetWindows));
            require(InstallPresent(sourceDriver.presentBuffers, targetDriver.presentBuffers));
            require(InstallSwap(gdi));
            candidate = {targetModule, targetDriver, adapter, directory, dispatch};
        } catch (DWORD failure) { operationError = failure; }
          catch (const std::bad_alloc&) { operationError = ERROR_NOT_ENOUGH_MEMORY; }
          catch (...) { operationError = ERROR_UNHANDLED_EXCEPTION; }
        if (temporary) {
            if (!system.makeCurrent(nullptr, nullptr)) callbackCleanup = error();
            else if (!system.destroy(temporary)) callbackCleanup = error();
        }
        return operationError;
    });
    if (!result.cleanupError) result.cleanupError = callbackCleanup;
    const auto cleanup = [&](BOOL okay) { if (!okay && !result.cleanupError) result.cleanupError = error(); };
    if (observersTouched) cleanup(RemoveWindowObserversForCurrentThread());
    if (targetModule) cleanup(FreeLibrary(targetModule));
    if (sourceModule) cleanup(FreeLibrary(sourceModule));
    if (result.Succeeded()) {
        try {
            auto target = PrepareTarget(candidate.module, candidate.driver, candidate.adapter);
            if (!target) result.error = error();
            else {
                std::shared_ptr<const DrawableSnapshot> formats = std::make_shared<DrawableSnapshot>(
                    DrawableSnapshot{nullptr, std::move(target), candidate.directory, candidate.windows, 0, false});
                std::atomic_store(&unformattedWindowFormats, std::move(formats));
                output = std::move(candidate);
            }
        } catch (const std::bad_alloc&) { result.error = ERROR_NOT_ENOUGH_MEMORY; }
    }
    SetLastError(priorError);
    return result;
}
}
