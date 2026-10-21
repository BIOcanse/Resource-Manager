#pragma once
#include "../GpuPlacementCommon/WorkerPipeClient.h"
#include <array>
#include <string>
#include <vector>

namespace ResourceManagerGpuLaunchSession
{
constexpr unsigned char ResolveOperation = 2;
constexpr unsigned char ReportOperation = 3;
constexpr DWORD MaximumPayloadBytes = 64 * 1024;
using ResourceManagerGpuWorker::Handle;

struct BackendIdentity { DWORD processId{}; uint64_t creation{}; };

inline bool ProcessIdentity(HANDLE process, const std::wstring& expectedImage, uint64_t& creation)
{
    FILETIME born{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &born, &exited, &kernel, &user) || exited.dwHighDateTime || exited.dwLowDateTime) return false;
    std::array<wchar_t, 32768> image{};
    DWORD size = static_cast<DWORD>(image.size());
    if (!QueryFullProcessImageNameW(process, 0, image.data(), &size) || _wcsicmp(image.data(), expectedImage.c_str()) != 0) return false;
    creation = (static_cast<uint64_t>(born.dwHighDateTime) << 32) | born.dwLowDateTime;
    return true;
}

inline bool Transfer(HANDLE pipe, void* bytes, DWORD length, bool writing, ULONGLONG deadline)
{
    auto at = static_cast<unsigned char*>(bytes);
    while (length)
    {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event.valid()) return false;
        OVERLAPPED pending{};
        pending.hEvent = event.get();
        DWORD done{};
        if (!(writing ? WriteFile(pipe, at, length, &done, &pending) : ReadFile(pipe, at, length, &done, &pending)))
        {
            if (GetLastError() != ERROR_IO_PENDING) return false;
            const auto now = GetTickCount64();
            if (now >= deadline || WaitForSingleObject(event.get(), static_cast<DWORD>(deadline - now)) != WAIT_OBJECT_0)
            {
                CancelIoEx(pipe, &pending);
                GetOverlappedResult(pipe, &pending, &done, TRUE); // Drain before the OVERLAPPED storage is released.
                return false;
            }
            if (!GetOverlappedResult(pipe, &pending, &done, FALSE)) return false;
        }
        if (!done || done > length) return false;
        at += done;
        length -= done;
    }
    return true;
}

inline bool Call(unsigned char operation, const std::string& body, const std::wstring& expectedBackend,
    const std::wstring& testPipeName, BackendIdentity& backend, bool requireExistingBackend, std::string& reply)
{
    reply.clear();
    if (body.empty() || body.size() > MaximumPayloadBytes) return false;
    DWORD session{};
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) return false;
    const auto name = L"\\\\.\\pipe\\" + (testPipeName.empty()
        ? L"ResourceManager.Service.Session." + std::to_wstring(session) + L".v1" : testPipeName);
    const auto deadline = GetTickCount64() + 2000;
    Handle pipe(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IMPERSONATION, nullptr));
    if (!pipe.valid() && GetLastError() == ERROR_PIPE_BUSY)
    {
        if (!WaitNamedPipeW(name.c_str(), 1000)) return false;
        pipe = Handle(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IMPERSONATION, nullptr));
    }
    if (!pipe.valid()) return false;
    ULONG pid{};
    if (!GetNamedPipeServerProcessId(pipe.get(), &pid)) return false;
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    uint64_t creation{};
    if (!process.valid() || !ProcessIdentity(process.get(), expectedBackend, creation)
        || (requireExistingBackend && (backend.processId != pid || backend.creation != creation))) return false;
    DWORD size = static_cast<DWORD>(body.size());
    if (!Transfer(pipe.get(), &operation, 1, true, deadline)
        || !Transfer(pipe.get(), &size, sizeof(size), true, deadline)
        || !Transfer(pipe.get(), const_cast<char*>(body.data()), size, true, deadline)
        || !Transfer(pipe.get(), &size, sizeof(size), false, deadline) || size == 0 || size > MaximumPayloadBytes) return false;
    std::string result(size, '\0');
    if (!Transfer(pipe.get(), result.data(), size, false, deadline)) return false;
    ULONG currentPid{};
    uint64_t currentCreation{};
    if (!GetNamedPipeServerProcessId(pipe.get(), &currentPid) || currentPid != pid
        || !ProcessIdentity(process.get(), expectedBackend, currentCreation) || currentCreation != creation) return false;
    backend = {pid, creation};
    reply = std::move(result);
    return true;
}
}
