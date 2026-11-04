#pragma once
#include <windows.h>
#include <utility>

namespace ResourceManagerOpenGl
{
// Access is serialized by the context's existing WGL operation lock.
class ThreadOwner
{
    HANDLE thread_{};
    DWORD id_{};

    bool AllowsAccess(bool allowCurrent) const noexcept
    {
        if (!thread_) return true;
        const DWORD error = GetLastError();
        const DWORD state = WaitForSingleObject(thread_, 0);
        if (state == WAIT_OBJECT_0 ||
            (state == WAIT_TIMEOUT && allowCurrent && id_ == GetCurrentThreadId())) {
            SetLastError(error);
            return true;
        }
        if (state != WAIT_FAILED) SetLastError(ERROR_BUSY);
        return false;
    }
public:
    ThreadOwner() = default;
    ThreadOwner(const ThreadOwner&) = delete;
    ThreadOwner& operator=(const ThreadOwner&) = delete;
    ThreadOwner(ThreadOwner&& other) noexcept
        : thread_(std::exchange(other.thread_, nullptr)), id_(std::exchange(other.id_, 0)) {}
    ThreadOwner& operator=(ThreadOwner&& other) noexcept
    {
        if (this != &other) {
            Reset();
            thread_ = std::exchange(other.thread_, nullptr);
            id_ = std::exchange(other.id_, 0);
        }
        return *this;
    }
    ~ThreadOwner() { Reset(); }

    // Prepare before the native bind; move into the record only if binding succeeds.
    bool CaptureCurrent() noexcept
    {
        HANDLE prepared{};
        const DWORD error = GetLastError();
        if (!DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(),
            &prepared, SYNCHRONIZE, FALSE, 0)) return false;
        Reset();
        thread_ = prepared;
        id_ = GetCurrentThreadId();
        SetLastError(error);
        return true;
    }
    void Reset() noexcept
    {
        const DWORD error = GetLastError();
        if (thread_) CloseHandle(thread_);
        thread_ = nullptr;
        id_ = 0;
        SetLastError(error);
    }
    DWORD Id() const noexcept { return id_; }
    bool CanBind() const noexcept { return AllowsAccess(true); }
    bool CanDeleteUnbound() const noexcept { return AllowsAccess(false); }
};
}
