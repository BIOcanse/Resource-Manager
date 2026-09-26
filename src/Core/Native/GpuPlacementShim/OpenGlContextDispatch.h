#pragma once
#include "OpenGlIcd.h"
#include "OpenGlThreadOwner.h"
#include "OpenGlWindowDispatch.h"
#include <GL/glext.h>
#include <atomic>
#include <cstdint>
#include <limits>
#include <memory>
#include <mutex>
#include <unordered_map>

namespace ResourceManagerOpenGl
{
struct ModernEntries
{
#define RM_GL_MODERN(Name, Type) Type Name{};
#include "OpenGlModernEntries.inc"
#undef RM_GL_MODERN
};

struct ContextRecord
{
    const HMODULE module;
    const IcdExports driver;
    const WindowDispatchReference windowDispatch;
    std::atomic<DHGLRC> token{0};
    ThreadOwner owner;
    // Protected by the existing WGL operation lock, like owner.
    size_t nativeUseCount{};
    uint64_t applicationCreationOrder{};
    ModernEntries modern{};
    bool modernResolved{};

    ContextRecord(HMODULE owner, const IcdExports& entries, WindowDispatchReference windows)
        : module(owner), driver(entries), windowDispatch(std::move(windows)) {}
};
using ContextReference = std::shared_ptr<ContextRecord>;

class ContextUse
{
    ContextReference record_;
    std::mutex& operationLock_;
public:
    ContextUse(const ContextReference& record, std::mutex& operationLock)
        : operationLock_(operationLock)
    {
        if (!record) return;
        const DWORD error = GetLastError();
        {
            std::lock_guard<std::mutex> lock(operationLock_);
            if (!record->token.load()) { SetLastError(ERROR_INVALID_HANDLE); return; }
            ++record->nativeUseCount;
            record_ = record;
        }
        SetLastError(error);
    }
    ContextUse(const ContextUse&) = delete;
    ContextUse& operator=(const ContextUse&) = delete;
    ~ContextUse()
    {
        if (!record_) return;
        const DWORD error = GetLastError();
        {
            std::lock_guard<std::mutex> lock(operationLock_);
            --record_->nativeUseCount;
        }
        SetLastError(error);
    }
    explicit operator bool() const noexcept { return record_ != nullptr; }
};

inline HGLRC PublicHandle(const ContextReference& record) noexcept
{
    return reinterpret_cast<HGLRC>(record.get());
}

class ContextStore
{
    // This protects lookup storage, not native operations. Never hold it across an ICD call.
    std::mutex recordsLock_;
    std::unordered_map<HGLRC, ContextReference> records_;
    uint64_t applicationCreationCount_{};

    void Remove(const ContextReference& record)
    {
        std::lock_guard<std::mutex> lock(recordsLock_);
        records_.erase(PublicHandle(record));
    }
public:
    // Called only at successful outer application return, under the WGL operation lock.
    uint64_t PublishApplicationCreation(const ContextReference& record)
    {
        if (record && record->applicationCreationOrder) return record->applicationCreationOrder;
        if (applicationCreationCount_ == (std::numeric_limits<uint64_t>::max)()) return 0;
        const uint64_t order = ++applicationCreationCount_;
        if (record) record->applicationCreationOrder = order;
        return order;
    }

    size_t Count()
    {
        std::lock_guard<std::mutex> lock(recordsLock_);
        return records_.size();
    }

    ContextReference Find(HGLRC handle)
    {
        std::lock_guard<std::mutex> lock(recordsLock_);
        const auto found = records_.find(handle);
        return found == records_.end() ? nullptr : found->second;
    }

    // The existing WGL operation lock serializes these calls with bind/release/share.
    ContextReference Create(HMODULE module, const IcdExports& driver, HDC dc, int layer,
        WindowDispatchReference windows = nullptr)
    {
        if (!module || !driver.createLayerContext || !driver.deleteContext || (windows && windows->module != module)) {
            SetLastError(ERROR_INVALID_PARAMETER);
            return nullptr;
        }
        ContextReference record;
        try {
            record = std::make_shared<ContextRecord>(module, driver, std::move(windows));
            std::lock_guard<std::mutex> lock(recordsLock_);
            records_.emplace(PublicHandle(record), record);
        } catch (const std::bad_alloc&) {
            SetLastError(ERROR_NOT_ENOUGH_MEMORY);
            return nullptr;
        }
        const DHGLRC token = driver.createLayerContext(dc, layer);
        const DWORD error = GetLastError();
        if (!token) Remove(record);
        else record->token.store(token);
        SetLastError(error);
        return token ? record : nullptr;
    }

    BOOL DestroyUnbound(const ContextReference& record)
    {
        if (!record) { SetLastError(ERROR_INVALID_HANDLE); return FALSE; }
        const auto registered = Find(PublicHandle(record));
        if (!registered || registered != record) {
            SetLastError(ERROR_INVALID_HANDLE);
            return FALSE;
        }
        const DHGLRC token = registered->token.load();
        if (!token) { SetLastError(ERROR_INVALID_HANDLE); return FALSE; }
        if (registered->nativeUseCount) { SetLastError(ERROR_BUSY); return FALSE; }
        if (!registered->owner.CanDeleteUnbound()) return FALSE;
        if (!registered->driver.deleteContext(token)) return FALSE;
        const DWORD error = GetLastError();
        registered->token.store(0);
        registered->owner.Reset();
        Remove(registered);
        SetLastError(error);
        return TRUE;
    }
};
}
