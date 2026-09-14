#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <cerrno>
#include <cwchar>
#include <cstring>
#include <limits>
#include <string>
#include <vector>
#include "WindowActionProtocol.h"
#include "../GpuPlacementCommon/WorkerPipeClient.h"

namespace {
using namespace window_action;
using namespace ResourceManagerGpuWorker;
template<class T> bool read_frame(HANDLE pipe, T& value) { return ReadFrame(pipe, value, maximum_message_bytes); }
template<class T> bool write_frame(HANDLE pipe, const T& value) { return WriteFrame(pipe, value); }

int reject(HANDLE pipe, Reason reason, uint32_t error) {
    return write_frame(pipe, Rejected{{version, Kind::rejected}, reason, error}) ? 0 : ERROR_BROKEN_PIPE;
}

bool same_user(HANDLE process) {
    HANDLE target_raw = nullptr, own_raw = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &target_raw)) return false;
    Handle target(target_raw);
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &own_raw)) return false;
    Handle own(own_raw);
    auto user = [](HANDLE token, std::vector<unsigned char>& buffer) {
        DWORD size = 0;
        if (GetTokenInformation(token, TokenUser, nullptr, 0, &size) || GetLastError() != ERROR_INSUFFICIENT_BUFFER) return false;
        buffer.resize(size);
        return GetTokenInformation(token, TokenUser, buffer.data(), size, &size) != FALSE;
    };
    std::vector<unsigned char> target_user, own_user;
    return user(target.get(), target_user) && user(own.get(), own_user)
        && EqualSid(reinterpret_cast<TOKEN_USER*>(target_user.data())->User.Sid, reinterpret_cast<TOKEN_USER*>(own_user.data())->User.Sid);
}

bool owns(const Request& request, HANDLE process, DWORD thread) {
    DWORD pid = 0;
    const auto actual_thread = GetWindowThreadProcessId(reinterpret_cast<HWND>(request.target.window), &pid);
    return alive(process, request.target.creation) && actual_thread != 0 && (thread == 0 || actual_thread == thread)
        && pid == request.target.pid;
}

Read observe(const Request& request, HANDLE process, DWORD thread) {
    if (!owns(request, process, thread)) return {ReadState::unavailable, ERROR_INVALID_WINDOW_HANDLE, {}};
    const auto window = reinterpret_cast<HWND>(request.target.window);
    RECT rectangle{};
    SetLastError(0);
    if (!GetWindowRect(window, &rectangle)) return {ReadState::unavailable, GetLastError(), {}};
    const auto width = static_cast<int64_t>(rectangle.right) - rectangle.left;
    const auto height = static_cast<int64_t>(rectangle.bottom) - rectangle.top;
    if (width < 0 || width > std::numeric_limits<int32_t>::max() || height < 0 || height > std::numeric_limits<int32_t>::max())
        return {ReadState::unavailable, ERROR_ARITHMETIC_OVERFLOW, {}};
    State value{rectangle.left, rectangle.top, static_cast<int32_t>(width), static_cast<int32_t>(height),
        (IsWindowVisible(window) ? 1U : 0U) | (IsIconic(window) ? 2U : 0U) | (IsZoomed(window) ? 4U : 0U)};
    if (!owns(request, process, thread)) return {ReadState::unavailable, ERROR_INVALID_WINDOW_HANDLE, {}};
    return {ReadState::available, 0, value};
}

bool eligible(const Request& request, const State& state) {
    const auto window = reinterpret_cast<HWND>(request.target.window);
    DWORD foreground_pid = 0;
    GetWindowThreadProcessId(GetForegroundWindow(), &foreground_pid);
    return (state.flags & 1U) && !(state.flags & 2U) && state.width > 0 && state.width < std::numeric_limits<int32_t>::max()
        && state.height > 0 && GetAncestor(window, GA_ROOT) == window && GetWindowTextLengthW(window) > 0
        && foreground_pid != request.target.pid;
}

Call resize(HWND window, int width, int height) {
    SetLastError(0);
    const auto accepted = SetWindowPos(window, nullptr, 0, 0, width, height,
        SWP_NOMOVE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE);
    return {accepted ? CallState::accepted : CallState::rejected, accepted ? 0U : GetLastError()};
}

int run(HANDLE pipe) {
    Request request{};
    if (!read_frame(pipe, request)) return ERROR_INVALID_DATA;
    if (request.header.version != version || request.header.kind != Kind::prepare || request.target.pid <= 4
        || request.target.creation == 0 || request.target.creation > static_cast<uint64_t>(INT64_MAX) || request.target.window == 0
        || (request.target.method != Method::redraw && request.target.method != Method::resize))
        return reject(pipe, Reason::invalid_request, ERROR_INVALID_DATA);
    Handle target(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, request.target.pid));
    if (!target.valid()) return reject(pipe, Reason::target_unavailable, GetLastError());
    if (!alive(target.get(), request.target.creation)) return reject(pipe, Reason::target_unavailable, 0);
    DWORD target_session = 0, own_session = 0;
    if (!ProcessIdToSessionId(request.target.pid, &target_session) || !ProcessIdToSessionId(GetCurrentProcessId(), &own_session)
        || target_session != own_session || !same_user(target.get()))
        return reject(pipe, Reason::context_unavailable, ERROR_ACCESS_DENIED);
    const auto window = reinterpret_cast<HWND>(request.target.window);
    DWORD pid = 0;
    const auto thread = GetWindowThreadProcessId(window, &pid);
    if (!owns(request, target.get(), thread)) return reject(pipe, Reason::window_mismatch, ERROR_INVALID_WINDOW_HANDLE);
    const auto before = observe(request, target.get(), thread);
    if (before.state != ReadState::available) return reject(pipe, Reason::window_mismatch, before.error);
    if (!eligible(request, before.value)) return reject(pipe, Reason::not_eligible, 0);
    if (!write_frame(pipe, Prepared{{version, Kind::prepared}, request.target, thread, before.value})) return ERROR_BROKEN_PIPE;

    Header command{};
    if (!read_frame(pipe, command)) return ERROR_INVALID_DATA;
    if (command.version != version || command.kind != Kind::execute) return reject(pipe, Reason::invalid_request, ERROR_INVALID_DATA);
    const auto current = observe(request, target.get(), thread);
    if (current.state != ReadState::available) return reject(pipe, Reason::window_mismatch, current.error);
    if (!eligible(request, current.value)) return reject(pipe, Reason::not_eligible, 0);
    if (std::memcmp(&current.value, &before.value, sizeof(State)) != 0) return reject(pipe, Reason::state_changed, 0);

    Completed result{{version, Kind::completed}, {}, {}, {}, {}};
    if (request.target.method == Method::redraw) {
        SetLastError(0);
        const auto accepted = RedrawWindow(window, nullptr, nullptr, RDW_INVALIDATE | RDW_ALLCHILDREN);
        result.change = {accepted ? CallState::accepted : CallState::rejected, accepted ? 0U : GetLastError()};
        result.after_change = observe(request, target.get(), thread);
    } else {
        result.change = resize(window, before.value.width + 1, before.value.height);
        result.after_change = observe(request, target.get(), thread);
        // A failed call is not proof that the target had no effect; restore once while ownership still holds.
        result.restore = owns(request, target.get(), thread)
            ? resize(window, before.value.width, before.value.height)
            : Call{CallState::target_unavailable, ERROR_INVALID_WINDOW_HANDLE};
        result.after_restore = observe(request, target.get(), thread);
    }
    return write_frame(pipe, result) ? 0 : ERROR_BROKEN_PIPE;
}
}

int wmain(int argc, wchar_t** argv) {
    Handle pipe, parent;
    const auto connection = ConnectParentPipe(argc, argv, pipe, parent);
    if (connection) return static_cast<int>(connection);
    if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        return reject(pipe.get(), Reason::context_unavailable, GetLastError());
    return run(pipe.get());
}
