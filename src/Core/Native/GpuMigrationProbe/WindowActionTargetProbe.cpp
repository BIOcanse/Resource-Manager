#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <atomic>
#include <cstdint>
#include <cwchar>
#include <sstream>
#include <string>
#include <vector>

namespace {
HANDLE ready, release_resize, command_done, blocked_event;
HDESK desktop;
HWND window;
DWORD window_thread, window_error;
std::atomic<UINT> pause_message{0};
std::atomic<int> refused_width{0};
std::atomic<bool> tracking{false}, blocked{false}, timed_release{false};
CRITICAL_SECTION history_lock;
struct Size { int width, height; UINT flags; };
std::vector<Size> requests, changes;

uint64_t birth(HANDLE handle, bool thread = false) {
    FILETIME created{}, ended{}, kernel{}, user{};
    const auto ok = thread ? GetThreadTimes(handle, &created, &ended, &kernel, &user) : GetProcessTimes(handle, &created, &ended, &kernel, &user);
    return ok ? (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime : 0;
}

bool transfer(HANDLE pipe, void* value, uint32_t size, bool write) {
    auto bytes = static_cast<unsigned char*>(value);
    while (size) {
        DWORD done = 0;
        const auto ok = write ? WriteFile(pipe, bytes, size, &done, nullptr) : ReadFile(pipe, bytes, size, &done, nullptr);
        if (!ok || !done) return false;
        bytes += done;
        size -= done;
    }
    return true;
}

bool send(HANDLE pipe, std::string text) {
    auto length = static_cast<uint32_t>(text.size());
    return transfer(pipe, &length, 4, true) && transfer(pipe, text.data(), length, true);
}

LRESULT CALLBACK procedure(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if ((message == WM_WINDOWPOSCHANGING || message == WM_WINDOWPOSCHANGED) && tracking.load()) {
        auto value = reinterpret_cast<WINDOWPOS*>(lparam);
        EnterCriticalSection(&history_lock);
        (message == WM_WINDOWPOSCHANGING ? requests : changes).push_back({value->cx, value->cy, value->flags});
        LeaveCriticalSection(&history_lock);
        if (message == WM_WINDOWPOSCHANGING && refused_width.load() == value->cx) value->flags |= SWP_NOSIZE;
    }
    auto expected = message;
    if ((message == WM_WINDOWPOSCHANGING || message == WM_WINDOWPOSCHANGED) && pause_message.compare_exchange_strong(expected, 0)) {
        blocked.store(true);
        SetEvent(blocked_event);
        if (WaitForSingleObject(release_resize, 10000) != WAIT_OBJECT_0) timed_release.store(true);
    }
    if (message == WM_APP + 1) {
        SetLastError(0);
        window_error = SetWindowPos(hwnd, nullptr, 0, 0, static_cast<int>(wparam), 60,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE) ? 0 : GetLastError();
        SetEvent(command_done);
        return 0;
    }
    if (message == WM_CLOSE) { DestroyWindow(hwnd); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

DWORD WINAPI window_main(void*) {
    window_thread = GetCurrentThreadId();
    if (!SetThreadDesktop(desktop)) { window_error = GetLastError(); SetEvent(ready); return window_error; }
    WNDCLASSEXW type{};
    type.cbSize = sizeof(type); type.hInstance = GetModuleHandleW(nullptr); type.lpfnWndProc = procedure; type.lpszClassName = L"ResourceManager.NativeWindowTarget";
    if (!RegisterClassExW(&type)) { window_error = GetLastError(); SetEvent(ready); return window_error; }
    window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, type.lpszClassName, L"GPU isolated native target",
        WS_POPUP | WS_VISIBLE, 0, 0, 80, 60, nullptr, nullptr, type.hInstance, nullptr);
    if (!window) window_error = GetLastError();
    tracking.store(true);
    SetEvent(ready);
    if (window) {
        MSG message{};
        while (GetMessageW(&message, nullptr, 0, 0) > 0) DispatchMessageW(&message);
        if (IsWindow(window)) DestroyWindow(window);
    }
    if (!UnregisterClassW(type.lpszClassName, type.hInstance)) return GetLastError();
    return window_error;
}

std::string snapshot(HANDLE thread, bool thread_exited, bool desktop_closed) {
    RECT rect{};
    const auto valid = GetWindowRect(window, &rect) != FALSE;
    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION job{};
    if (!QueryInformationJobObject(nullptr, JobObjectBasicAccountingInformation, &job, sizeof(job), nullptr)) return "{}";
    std::ostringstream out;
    out << "{\"processId\":" << GetCurrentProcessId() << ",\"creationFileTimeUtc\":" << birth(GetCurrentProcess())
        << ",\"window\":" << reinterpret_cast<uintptr_t>(window) << ",\"threadId\":" << window_thread
        << ",\"threadCreationFileTimeUtc\":" << birth(thread, true) << ",\"width\":" << (valid ? rect.right - rect.left : 0)
        << ",\"height\":" << (valid ? rect.bottom - rect.top : 0) << ",\"left\":" << rect.left << ",\"top\":" << rect.top
        << ",\"visible\":" << (IsWindowVisible(window) ? "true" : "false") << ",\"iconic\":" << (IsIconic(window) ? "true" : "false")
        << ",\"maximized\":" << (IsZoomed(window) ? "true" : "false") << ",\"blocked\":" << (blocked.load() ? "true" : "false")
        << ",\"timedRelease\":" << (timed_release.load() ? "true" : "false") << ",\"threadExited\":" << (thread_exited ? "true" : "false")
        << ",\"desktopClosed\":" << (desktop_closed ? "true" : "false") << ",\"windowDestroyed\":" << (!IsWindow(window) ? "true" : "false")
        << ",\"activeProcessCount\":" << job.ActiveProcesses << ",\"errorMode\":" << GetErrorMode() << ",\"windowError\":" << window_error;
    EnterCriticalSection(&history_lock);
    auto list = [&](const char* name, const std::vector<Size>& values) {
        out << ",\"" << name << "\":[";
        bool first = true;
        for (auto value : values) { if (!first) out << ','; first = false; out << "{\"width\":" << value.width << ",\"height\":" << value.height << ",\"flags\":" << value.flags << '}'; }
        out << ']';
    };
    list("requests", requests); list("changes", changes);
    LeaveCriticalSection(&history_lock);
    out << '}';
    return out.str();
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 5 || GetErrorMode() != 32771 || !SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return 10;
    const auto parent_pid = std::wcstoul(argv[2], nullptr, 10);
    const auto parent_creation = std::wcstoull(argv[3], nullptr, 10);
    HANDLE parent = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, parent_pid);
    if (!parent || birth(parent) != parent_creation || WaitForSingleObject(parent, 0) != WAIT_TIMEOUT) return 11;
    const auto path = std::wstring(L"\\\\.\\pipe\\") + argv[1];
    HANDLE pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    ULONG server = 0;
    if (pipe == INVALID_HANDLE_VALUE || !GetNamedPipeServerProcessId(pipe, &server) || server != parent_pid) return 12;
    const auto old_desktop = GetThreadDesktop(GetCurrentThreadId());
    desktop = CreateDesktopW(argv[4], nullptr, nullptr, 0, DESKTOP_CREATEWINDOW | DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_ENUMERATE, nullptr);
    if (!desktop || !SetThreadDesktop(desktop)) return 13;
    InitializeCriticalSection(&history_lock);
    ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    release_resize = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    command_done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    blocked_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE thread = CreateThread(nullptr, 0, window_main, nullptr, 0, nullptr);
    if (!thread || WaitForSingleObject(ready, 5000) != WAIT_OBJECT_0 || window_error) return 14;
    bool exited = false, closed = false;
    int exit_code = 0;
    if (!send(pipe, snapshot(thread, exited, closed))) exit_code = 15;
    while (exit_code == 0 && !closed) {
        uint32_t length = 0, command[2]{};
        if (!transfer(pipe, &length, 4, false) || length != sizeof(command) || !transfer(pipe, command, length, false)) { exit_code = 16; break; }
        switch (command[0]) {
        case 1: break; // Snapshot only.
        case 2:
            if (!PostMessageW(window, WM_APP + 1, command[1], 0) || WaitForSingleObject(command_done, 5000) != WAIT_OBJECT_0) exit_code = 17;
            break;
        case 3: pause_message.store(command[1]); break;
        case 4: SetEvent(release_resize); break;
        case 5: refused_width.store(static_cast<int>(command[1])); break;
        case 8:
            if (command[1] > 5000) { exit_code = 24; break; }
            WaitForSingleObject(blocked_event, command[1]);
            break;
        case 6: case 7:
            SetEvent(release_resize);
            if (!exited) {
                if (!PostMessageW(window, WM_CLOSE, 0, 0) || WaitForSingleObject(thread, 5000) != WAIT_OBJECT_0) { exit_code = 18; break; }
                exited = true;
            }
            if (command[0] == 7) {
                if (!SetThreadDesktop(old_desktop) || !CloseDesktop(desktop)) { exit_code = 19; break; }
                closed = true;
            }
            break;
        default: exit_code = 20;
        }
        if (exit_code == 0 && !send(pipe, snapshot(thread, exited, closed))) exit_code = 21;
    }
    if (!closed) {
        SetEvent(release_resize);
        if (!exited) { PostMessageW(window, WM_CLOSE, 0, 0); WaitForSingleObject(thread, 5000); }
        SetThreadDesktop(old_desktop);
        CloseDesktop(desktop);
    }
    DWORD thread_exit = STILL_ACTIVE;
    GetExitCodeThread(thread, &thread_exit);
    if (WaitForSingleObject(thread, 0) != WAIT_OBJECT_0) return 23;
    CloseHandle(thread); CloseHandle(ready); CloseHandle(release_resize); CloseHandle(command_done); CloseHandle(blocked_event);
    DeleteCriticalSection(&history_lock);
    CloseHandle(pipe); CloseHandle(parent);
    return exit_code ? exit_code : (thread_exit != 0 ? 22 : 0);
}
