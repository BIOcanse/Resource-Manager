#pragma once
#include "OpenGlModuleLifetime.h"
#include "OpenGlRuntimeState.h"
#include <functional>
#include <limits>

namespace ResourceManagerOpenGl::Runtime
{
enum class WindowCallState { NotStarted, Completed, ThreadExited, Unconfirmed };
struct WindowCallResult
{
    WindowCallState state{WindowCallState::NotStarted};
    BOOL returned{};
    DWORD operationError{}, dispatchError{}, cleanupError{};
};
// The function owns its captured inputs; it must not borrow a caller's stack.
using WindowWork = std::function<BOOL(HWND)>;

namespace WindowCallDetail
{
enum class Phase { Waiting, Running, Completed, Cancelled };
struct Request
{
    HWND window{};
    DWORD threadId{};
    UINT message{};
    WPARAM token{};
    HANDLE thread{}, completed{};
    HHOOK hook{};
    WindowWork work;
    std::atomic<Phase> phase{Phase::Waiting};
    BOOL returned{};
    DWORD operationError{};
    ~Request() {
        if (completed) CloseHandle(completed);
        if (thread) CloseHandle(thread);
    }
};
inline std::mutex slotLock;
inline std::shared_ptr<Request> active;
inline WPARAM nextToken{1};

inline DWORD ErrorOrFailure()
{
    const DWORD value = GetLastError();
    return value ? value : ERROR_GEN_FAILURE;
}
inline void Run(Request* request)
{
    SetLastError(0);
    try {
        request->returned = request->work(request->window);
        request->operationError = GetLastError();
    } catch (const std::bad_alloc&) {
        request->returned = FALSE;
        request->operationError = ERROR_NOT_ENOUGH_MEMORY;
    } catch (...) {
        request->returned = FALSE;
        request->operationError = ERROR_UNHANDLED_EXCEPTION;
    }
    // Work can leave through ExitThread without C++ unwinding. The occupied
    // slot owns it while running; acquire a temporary owner only after work.
    std::shared_ptr<Request> completing;
    { std::lock_guard<std::mutex> lock(slotLock); completing = active; }
    request->phase.store(Phase::Completed, std::memory_order_release);
    SetEvent(request->completed);
}
inline LRESULT CALLBACK Deliver(int code, WPARAM w, LPARAM l)
{
    const DWORD error = GetLastError();
    if (code >= 0) {
        std::shared_ptr<Request> request;
        { std::lock_guard<std::mutex> lock(slotLock); request = active; }
        if (request) {
            const auto& message = *reinterpret_cast<const CWPSTRUCT*>(l);
            if (message.message == request->message && message.wParam == request->token &&
                message.lParam == 0 && message.hwnd == request->window &&
                GetCurrentThreadId() == request->threadId &&
                WaitForSingleObject(request->thread, 0) == WAIT_TIMEOUT) {
                auto waiting = Phase::Waiting;
                if (request->phase.compare_exchange_strong(waiting, Phase::Running)) {
                    auto* executing = request.get();
                    request.reset();
                    Run(executing);
                }
            }
        }
    }
    SetLastError(error);
    return CallNextHookEx(nullptr, code, w, l);
}
struct RestoreError
{
    DWORD value{GetLastError()};
    ~RestoreError() { SetLastError(value); }
};
}

// Called by the original Configure action. Message timeout is not a work deadline.
inline WindowCallResult ExecuteOnWindowThread(HWND window, WindowWork work, DWORD messageTimeout)
{
    using namespace WindowCallDetail;
    RestoreError restore;
    WindowCallResult result;
    DWORD process{};
    const DWORD threadId = GetWindowThreadProcessId(window, &process);
    if (!threadId) { result.dispatchError = ERROR_INVALID_WINDOW_HANDLE; return result; }
    if (process != GetCurrentProcessId()) { result.dispatchError = ERROR_ACCESS_DENIED; return result; }
    if (!work || !messageTimeout || messageTimeout == INFINITE) {
        result.dispatchError = ERROR_INVALID_PARAMETER; return result;
    }
    std::shared_ptr<Request> request;
    try { request = std::make_shared<Request>(); }
    catch (const std::bad_alloc&) { result.dispatchError = ERROR_NOT_ENOUGH_MEMORY; return result; }
    request->window = window; request->threadId = threadId; request->work = std::move(work);
    request->thread = OpenThread(SYNCHRONIZE | THREAD_QUERY_LIMITED_INFORMATION, FALSE, threadId);
    if (!request->thread) { result.dispatchError = ErrorOrFailure(); return result; }
    if (GetProcessIdOfThread(request->thread) != process ||
        WaitForSingleObject(request->thread, 0) != WAIT_TIMEOUT ||
        GetWindowThreadProcessId(window, nullptr) != threadId) {
        result.dispatchError = ERROR_INVALID_THREAD_ID; return result;
    }
    request->completed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!request->completed) { result.dispatchError = ErrorOrFailure(); return result; }
    request->message = RegisterWindowMessageW(L"ResourceManager.OpenGl.WindowCall.9AA339C3-DF60-4C19-8DC9-C85F2B826F20");
    if (!request->message) { result.dispatchError = ErrorOrFailure(); return result; }
    if (!PinEntryModule(Callback(&Deliver))) { result.dispatchError = ErrorOrFailure(); return result; }
    {
        std::lock_guard<std::mutex> lock(slotLock);
        if (active) { result.dispatchError = ERROR_BUSY; return result; }
        if (nextToken == std::numeric_limits<WPARAM>::max()) {
            result.dispatchError = ERROR_ARITHMETIC_OVERFLOW; return result;
        }
        request->token = nextToken++;
        active = request;
    }
    if (threadId == GetCurrentThreadId()) {
        request->phase.store(Phase::Running);
        auto* executing = request.get();
        request.reset();
        Run(executing);
        { std::lock_guard<std::mutex> lock(slotLock); request = active; }
    } else {
        request->hook = SetWindowsHookExW(WH_CALLWNDPROC, Deliver, nullptr, threadId);
        if (!request->hook) result.dispatchError = ErrorOrFailure();
        else {
            DWORD_PTR ignored{};
            SetLastError(0);
            if (!SendMessageTimeoutW(window, request->message, request->token, 0,
                SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, messageTimeout, &ignored))
                result.dispatchError = ErrorOrFailure();
        }
    }
    auto waiting = Phase::Waiting;
    request->phase.compare_exchange_strong(waiting, Phase::Cancelled);
    if (request->phase.load(std::memory_order_acquire) == Phase::Running) {
        const HANDLE handles[]{request->completed, request->thread};
        const auto waited = WaitForMultipleObjects(2, handles, FALSE, INFINITE);
        if (waited == WAIT_OBJECT_0 + 1) result.state = WindowCallState::ThreadExited;
        else if (waited != WAIT_OBJECT_0) {
            result.state = WindowCallState::Unconfirmed;
            if (!result.dispatchError) result.dispatchError = ErrorOrFailure();
        }
    }
    const auto phase = request->phase.load(std::memory_order_acquire);
    if (phase == Phase::Completed) {
        result.state = WindowCallState::Completed;
        result.returned = request->returned;
        result.operationError = request->operationError;
    } else if (phase == Phase::Cancelled && !result.dispatchError) {
        result.dispatchError = ERROR_NOT_SUPPORTED;
    }
    if (request->hook) {
        if (UnhookWindowsHookEx(request->hook)) request->hook = nullptr;
        else result.cleanupError = ErrorOrFailure();
    }
    if (!request->hook && result.state != WindowCallState::Unconfirmed) {
        std::lock_guard<std::mutex> lock(slotLock);
        if (active == request) active.reset();
    }
    return result;
}
}
