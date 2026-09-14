#pragma once
#include <windows.h>
#include <cerrno>
#include <cwchar>
#include <cstdint>
#include <limits>
#include <string>
#include <utility>
#include "../GpuWindowAction/WindowActionProtocol.h"

namespace ResourceManagerGpuWorker
{
class Handle
{
    HANDLE value_{INVALID_HANDLE_VALUE};
public:
    Handle() = default;
    explicit Handle(HANDLE value) : value_(value) {}
    ~Handle() { if (valid()) CloseHandle(value_); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value_(std::exchange(other.value_, INVALID_HANDLE_VALUE)) {}
    Handle& operator=(Handle&& other) noexcept
    {
        if (this != &other) { if (valid()) CloseHandle(value_); value_ = std::exchange(other.value_, INVALID_HANDLE_VALUE); }
        return *this;
    }
    HANDLE get() const { return value_; }
    bool valid() const { return value_ && value_ != INVALID_HANDLE_VALUE; }
};
inline bool number(const wchar_t* text, uint64_t& value)
{
    if (!text || !*text) return false;
    for (auto at = text; *at; ++at) if (*at < L'0' || *at > L'9') return false;
    errno = 0; wchar_t* end{};
    value = std::wcstoull(text, &end, 10);
    return errno == 0 && end && *end == 0 && value != 0;
}
inline bool alive(HANDLE process, uint64_t creation)
{
    FILETIME born{}, exited{}, kernel{}, user{};
    return WaitForSingleObject(process, 0) == WAIT_TIMEOUT
        && GetProcessTimes(process, &born, &exited, &kernel, &user)
        && ((static_cast<uint64_t>(born.dwHighDateTime) << 32) | born.dwLowDateTime) == creation;
}
inline bool transfer(HANDLE pipe, void* buffer, uint32_t length, bool writing)
{
    auto bytes = static_cast<unsigned char*>(buffer);
    while (length) {
        DWORD done{};
        const auto okay = writing ? WriteFile(pipe, bytes, length, &done, nullptr) : ReadFile(pipe, bytes, length, &done, nullptr);
        if (!okay || !done) return false;
        length -= done; bytes += done;
    }
    return true;
}
template<class T> bool ReadFrame(HANDLE pipe, T& value, uint32_t maximum)
{
    uint32_t length{};
    return transfer(pipe, &length, sizeof(length), false) && length == sizeof(T)
        && length <= maximum && transfer(pipe, &value, length, false);
}
inline bool WriteBytes(HANDLE pipe, const void* value, uint32_t length)
{
    return transfer(pipe, &length, sizeof(length), true) && transfer(pipe, const_cast<void*>(value), length, true);
}
template<class T> bool WriteFrame(HANDLE pipe, const T& value) { return WriteBytes(pipe, &value, sizeof(value)); }

// The existing process owner transfers this read-only parent handle before resuming the worker.
inline DWORD ConnectParentPipe(int argc, wchar_t** argv, Handle& pipe, Handle& parent)
{
    if (argc != 4 || (GetErrorMode() & 0x8003U) != 0x8003U) return ERROR_INVALID_PARAMETER;
    uint64_t parentPid{}, parentCreation{};
    if (!number(argv[2], parentPid) || parentPid > MAXDWORD || !number(argv[3], parentCreation)) return ERROR_INVALID_PARAMETER;
    const std::wstring name(argv[1]);
    if (name.rfind(L"ResourceManager.GpuWindowAction.", 0) != 0 || name.find_first_of(L"\\/") != std::wstring::npos)
        return ERROR_INVALID_PARAMETER;
    pipe = Handle(CreateFileW((L"\\\\.\\pipe\\" + name).c_str(), FILE_READ_DATA | FILE_WRITE_DATA | SYNCHRONIZE,
        0, nullptr, OPEN_EXISTING, 0, nullptr));
    if (!pipe.valid()) return GetLastError();
    ULONG server{};
    if (!GetNamedPipeServerProcessId(pipe.get(), &server) || server != parentPid) return ERROR_ACCESS_DENIED;
    window_action::Parent bootstrap{};
    if (!ReadFrame(pipe.get(), bootstrap, window_action::maximum_message_bytes)
        || bootstrap.header.version != window_action::version || bootstrap.header.kind != window_action::Kind::parent
        || !bootstrap.handle || bootstrap.handle > static_cast<uint64_t>(INT64_MAX)
        || bootstrap.handle > (std::numeric_limits<uintptr_t>::max)()) return ERROR_INVALID_DATA;
    parent = Handle(reinterpret_cast<HANDLE>(static_cast<uintptr_t>(bootstrap.handle)));
    if (!parent.valid() || GetProcessId(parent.get()) != parentPid || !alive(parent.get(), parentCreation)
        || !GetNamedPipeServerProcessId(pipe.get(), &server) || server != parentPid) return ERROR_ACCESS_DENIED;
    return ERROR_SUCCESS;
}
}
