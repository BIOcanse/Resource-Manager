#pragma once
#include "OpenGlDrawableTarget.h"
#include "OpenGlPixelFormatDirectory.h"
#include "OpenGlWindowDispatch.h"
#include <windows.h>
#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <unordered_map>

namespace ResourceManagerOpenGl
{
struct DrawableRecord
{
private:
    friend class DrawableStore;
    std::shared_ptr<const DrawableTarget> target_;
    std::shared_ptr<const PixelFormatDirectory> directory_;
    WindowDispatchReference windowDispatch_;
    int installedPixelFormat_{};
    int sourcePixelFormat_{};
    bool installingPixelFormat_{};
    bool targetPresentation_{};
    uint64_t presentationCreationOrder_{};
public:
    const HWND window;
    const DWORD threadId;
    std::atomic<bool> active;
    DrawableRecord(HWND value, DWORD thread, bool live)
        : window(value), threadId(thread), active(live) {}
};
using DrawableReference = std::shared_ptr<DrawableRecord>;

struct DrawableSnapshot
{
    DrawableReference record;
    std::shared_ptr<const DrawableTarget> target;
    std::shared_ptr<const PixelFormatDirectory> directory;
    WindowDispatchReference windowDispatch;
    int installedPixelFormat{};
    bool targetPresentation{};
    int sourcePixelFormat{};
    explicit operator bool() const noexcept { return record != nullptr; }
};

class DrawableStore
{
    static constexpr wchar_t Property[] = L"ResourceManager.OpenGl.Drawable.37A00D99-80B4-494A-B218-CE82330590CA";
    std::mutex recordsLock_;
    std::unordered_map<HWND, DrawableReference> records_;

    static bool OnWindowThread(HWND window, DWORD& thread) noexcept
    {
        DWORD process{};
        thread = GetWindowThreadProcessId(window, &process);
        if (!thread) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return false; }
        if (process != GetCurrentProcessId() || thread != GetCurrentThreadId()) {
            SetLastError(ERROR_INVALID_THREAD_ID);
            return false;
        }
        return true;
    }

    // recordsLock_ is held by the explicit publication operation.
    bool CanPublish(const DrawableReference& record)
    {
        if (!record) { SetLastError(ERROR_INVALID_HANDLE); return false; }
        DWORD thread{};
        if (!OnWindowThread(record->window, thread)) return false;
        const auto found = records_.find(record->window);
        if (found == records_.end() || found->second != record || !record->active.load() ||
            GetPropW(record->window, Property) != record.get()) {
            SetLastError(ERROR_INVALID_WINDOW_HANDLE);
            return false;
        }
        return true;
    }
public:
    DrawableStore() = default;
    DrawableStore(const DrawableStore&) = delete;
    DrawableStore& operator=(const DrawableStore&) = delete;

    // Explicit preparation; its caller has already installed both terminal observers.
    DrawableReference PrepareOnWindowThread(HWND window)
    {
        const DWORD error = GetLastError();
        DWORD thread{};
        if (!OnWindowThread(window, thread)) return nullptr;
        std::lock_guard<std::mutex> lock(recordsLock_);
        const auto found = records_.find(window);
        if (found != records_.end()) {
            const auto& record = found->second;
            if (!record->active.load()) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return nullptr; }
            if (GetPropW(window, Property) != record.get()) { SetLastError(ERROR_INVALID_DATA); return nullptr; }
            SetLastError(error);
            return record;
        }
        if (GetPropW(window, Property)) { SetLastError(ERROR_ALREADY_EXISTS); return nullptr; }
        DrawableReference record;
        try {
            record = std::make_shared<DrawableRecord>(window, thread, true);
            records_.emplace(window, record);
        } catch (const std::bad_alloc&) {
            SetLastError(ERROR_NOT_ENOUGH_MEMORY);
            return nullptr;
        }
        if (!SetPropW(window, Property, record.get())) {
            const DWORD failure = GetLastError();
            records_.erase(window);
            SetLastError(failure);
            return nullptr;
        }
        SetLastError(error);
        return record;
    }

    DrawableSnapshot Read(HDC dc)
    {
        const DWORD error = GetLastError();
        const HWND window = dc ? WindowFromDC(dc) : nullptr;
        DrawableSnapshot result;
        if (window) {
            std::lock_guard<std::mutex> lock(recordsLock_);
            const auto found = records_.find(window);
            if (found != records_.end() && found->second->active.load() &&
                GetPropW(window, Property) == found->second.get()) {
                const auto& record = found->second;
                result = {record, record->target_, record->directory_, record->windowDispatch_, record->installedPixelFormat_, record->targetPresentation_, record->sourcePixelFormat_};
            }
        }
        SetLastError(error);
        return result;
    }

    DrawableReference Find(HDC dc) { return Read(dc).record; }

    // Creation or a successful bind may carry a newer application's target object.
    void CommitTargetPresentation(const DrawableReference& record, HMODULE module, uint64_t creationOrder)
    {
        if (!record || !creationOrder) return;
        std::lock_guard<std::mutex> lock(recordsLock_);
        if (record->active.load() && record->target_ && record->target_->module == module
            && creationOrder > record->presentationCreationOrder_) {
            record->targetPresentation_ = true;
            record->presentationCreationOrder_ = creationOrder;
        }
    }

    void CommitSystemContextCreation(const DrawableReference& record, uint64_t creationOrder)
    {
        if (!record || !creationOrder) return;
        std::lock_guard<std::mutex> lock(recordsLock_);
        if (record->active.load() && creationOrder > record->presentationCreationOrder_) {
            record->targetPresentation_ = false;
            record->presentationCreationOrder_ = creationOrder;
        }
    }

    BOOL InstallPixelFormat(HDC dc, int format)
    {
        const DWORD incoming = GetLastError();
        const auto drawable = Read(dc);
        if (!drawable) { SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE; }
        const auto& record = drawable.record;
        {
            std::lock_guard<std::mutex> lock(recordsLock_);
            if (!record->active.load() || GetPropW(record->window, Property) != record.get()) {
                SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE;
            }
            if (!drawable.target || !drawable.directory) {
                SetLastError(ERROR_NOT_READY); return FALSE;
            }
            PIXELFORMATDESCRIPTOR description{};
            if (format <= 0 || format > drawable.directory->NativeCount() ||
                !drawable.directory->Describe(format, sizeof(description), &description) ||
                !(description.dwFlags & PFD_DRAW_TO_WINDOW) ||
                (description.dwFlags & PFD_GENERIC_FORMAT)) {
                SetLastError(ERROR_INVALID_PIXEL_FORMAT); return FALSE;
            }
            if (record->installingPixelFormat_) { SetLastError(ERROR_BUSY); return FALSE; }
            if (record->installedPixelFormat_) {
                if (record->installedPixelFormat_ != format) {
                    SetLastError(ERROR_INVALID_PIXEL_FORMAT); return FALSE;
                }
                SetLastError(incoming); return TRUE;
            }
            record->installingPixelFormat_ = true;
        }
        // Native code may read this same Store. Never hold its lock across the call.
        SetLastError(incoming);
        const BOOL installed = drawable.target->driver.setPixelFormat(dc, format);
        const DWORD nativeError = GetLastError();
        {
            std::lock_guard<std::mutex> lock(recordsLock_);
            if (installed) record->installedPixelFormat_ = format;
            record->installingPixelFormat_ = false;
        }
        SetLastError(nativeError);
        return installed;
    }

    BOOL InstallSystemPixelFormat(HDC dc, int format, const PIXELFORMATDESCRIPTOR* description,
        decltype(&SetPixelFormat) write, decltype(&GetPixelFormat) read)
    {
        if (!write || !read) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
        const auto drawable = Read(dc);
        if (!drawable) return write(dc, format, description);
        const auto& record = drawable.record;
        {
            std::lock_guard<std::mutex> lock(recordsLock_);
            if (!record->active.load() || GetPropW(record->window, Property) != record.get()) {
                SetLastError(ERROR_INVALID_WINDOW_HANDLE); return FALSE;
            }
            if (record->installingPixelFormat_) { SetLastError(ERROR_BUSY); return FALSE; }
            record->installingPixelFormat_ = true;
        }
        const BOOL installed = write(dc, format, description);
        const DWORD nativeError = GetLastError();
        const int actual = installed ? read(dc) : 0;
        {
            std::lock_guard<std::mutex> lock(recordsLock_);
            if (installed) record->sourcePixelFormat_ = actual;
            record->installingPixelFormat_ = false;
        }
        SetLastError(nativeError);
        return installed;
    }

    bool PublishTarget(const DrawableReference& record, std::shared_ptr<const DrawableTarget> target, int sourcePixelFormat)
    {
        const DWORD error = GetLastError();
        if (!target || sourcePixelFormat < 0) { SetLastError(ERROR_INVALID_PARAMETER); return false; }
        std::lock_guard<std::mutex> lock(recordsLock_);
        if (!CanPublish(record)) return false;
        if (record->target_ && record->target_ != target) { SetLastError(ERROR_ALREADY_EXISTS); return false; }
        if (record->target_ && record->sourcePixelFormat_ != sourcePixelFormat) { SetLastError(ERROR_ALREADY_EXISTS); return false; }
        record->target_ = std::move(target);
        record->sourcePixelFormat_ = sourcePixelFormat;
        SetLastError(error);
        return true;
    }

    bool PublishDirectory(const DrawableReference& record, std::shared_ptr<const PixelFormatDirectory> directory)
    {
        const DWORD error = GetLastError();
        if (!directory) { SetLastError(ERROR_INVALID_PARAMETER); return false; }
        std::lock_guard<std::mutex> lock(recordsLock_);
        if (!CanPublish(record)) return false;
        if (!record->target_) {
            SetLastError(ERROR_INVALID_PIXEL_FORMAT);
            return false;
        }
        if (record->directory_ && record->directory_ != directory) { SetLastError(ERROR_ALREADY_EXISTS); return false; }
        record->directory_ = std::move(directory);
        SetLastError(error);
        return true;
    }

    bool PublishWindowDispatch(const DrawableReference& record, WindowDispatchReference dispatch)
    {
        const DWORD error = GetLastError();
        if (!dispatch) {
            SetLastError(ERROR_INVALID_PARAMETER);
            return false;
        }
        std::lock_guard<std::mutex> lock(recordsLock_);
        if (!CanPublish(record)) return false;
        if (!record->directory_) { SetLastError(ERROR_NOT_READY); return false; }
        if (dispatch->module != record->target_->module) { SetLastError(ERROR_INVALID_PARAMETER); return false; }
        if (record->windowDispatch_ && record->windowDispatch_ != dispatch) { SetLastError(ERROR_ALREADY_EXISTS); return false; }
        record->windowDispatch_ = std::move(dispatch);
        SetLastError(error);
        return true;
    }

    bool BeforeNcDestroy(HWND window)
    {
        const DWORD error = GetLastError();
        DWORD thread{};
        if (!OnWindowThread(window, thread)) return false;
        std::lock_guard<std::mutex> lock(recordsLock_);
        auto found = records_.find(window);
        if (found == records_.end()) {
            try {
                found = records_.emplace(window, std::make_shared<DrawableRecord>(window, thread, false)).first;
            } catch (const std::bad_alloc&) {
                SetLastError(ERROR_NOT_ENOUGH_MEMORY);
                return false;
            }
        }
        auto& record = found->second;
        record->active.store(false);
        const HANDLE association = GetPropW(window, Property);
        if (association && association != record.get()) { SetLastError(ERROR_INVALID_DATA); return false; }
        if (association && RemovePropW(window, Property) != record.get()) {
            SetLastError(ERROR_INVALID_DATA);
            return false;
        }
        SetLastError(error);
        return true;
    }

    bool AfterNcDestroy(HWND window)
    {
        const DWORD error = GetLastError();
        std::lock_guard<std::mutex> lock(recordsLock_);
        const auto found = records_.find(window);
        if (found == records_.end() || found->second->active.load()) {
            SetLastError(ERROR_INVALID_WINDOW_HANDLE);
            return false;
        }
        if (found->second->threadId != GetCurrentThreadId()) {
            SetLastError(ERROR_INVALID_THREAD_ID);
            return false;
        }
        records_.erase(found);
        SetLastError(error);
        return true;
    }

    size_t Count()
    {
        std::lock_guard<std::mutex> lock(recordsLock_);
        return records_.size();
    }

    size_t CountForThread(DWORD threadId)
    {
        std::lock_guard<std::mutex> lock(recordsLock_);
        size_t count{};
        for (const auto& item : records_) if (item.second->threadId == threadId) ++count;
        return count;
    }
};
}
