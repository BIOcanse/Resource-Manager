#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

namespace {
struct Handle {
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE handle) : value(handle) {}
    ~Handle() { if (value != nullptr && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

std::uint64_t creation_time(HANDLE process) {
    FILETIME creation{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(process, &creation, &exit, &kernel, &user)) return 0;
    return (static_cast<std::uint64_t>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;
}

bool transfer(HANDLE pipe, void* buffer, DWORD size, bool writing) {
    auto* cursor = static_cast<unsigned char*>(buffer);
    while (size != 0) {
        DWORD actual = 0;
        const BOOL ok = writing ? WriteFile(pipe, cursor, size, &actual, nullptr)
            : ReadFile(pipe, cursor, size, &actual, nullptr);
        if (!ok || actual == 0) return false;
        cursor += actual;
        size -= actual;
    }
    return true;
}

bool send(HANDLE pipe, const std::string& value) {
    auto length = static_cast<std::uint32_t>(value.size());
    return transfer(pipe, &length, sizeof(length), true)
        && transfer(pipe, const_cast<char*>(value.data()), length, true);
}

[[noreturn]] void suspend_probe() { Sleep(INFINITE); ExitProcess(199); }

int run(int argc, wchar_t** argv) {
    if (argc != 5) return 101;
    const std::wstring mode = argv[4];
    const auto parent_pid = static_cast<DWORD>(std::wcstoul(argv[2], nullptr, 10));
    const auto parent_creation = std::wcstoull(argv[3], nullptr, 10);
    if ((GetErrorMode() & 0x8003) != 0x8003) return 102;
    if (mode == L"no-connect") suspend_probe();
    const std::wstring path = L"\\\\.\\pipe\\" + std::wstring(argv[1]);
    Handle pipe(CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
        OPEN_EXISTING, SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
    if (pipe.value == INVALID_HANDLE_VALUE) return 103;
    ULONG server = 0;
    if (!GetNamedPipeServerProcessId(pipe.value, &server) || server != parent_pid) return 104;
    Handle parent(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, server));
    if (!parent.value || creation_time(parent.value) != parent_creation
        || WaitForSingleObject(parent.value, 0) != WAIT_TIMEOUT) return 105;

    if (mode == L"no-output") suspend_probe();
    if (mode == L"partial-prefix") {
        std::uint16_t partial = 20;
        if (!transfer(pipe.value, &partial, sizeof(partial), true)) return 106;
        suspend_probe();
    }
    if (mode == L"partial-body") {
        std::uint32_t length = 20;
        char partial[] = "partial";
        if (!transfer(pipe.value, &length, sizeof(length), true)
            || !transfer(pipe.value, partial, sizeof(partial) - 1, true)) return 106;
        suspend_probe();
    }
    if (mode == L"oversize" || mode == L"empty-frame") {
        std::uint32_t length = mode == L"oversize" ? UINT32_MAX : 0;
        if (!transfer(pipe.value, &length, sizeof(length), true)) return 106;
        suspend_probe();
    }
    if (mode == L"crash") return 23;

    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
    struct JobProcesses { DWORD assigned, listed; ULONG_PTR pids[8]; } members{};
    if (!QueryInformationJobObject(nullptr, JobObjectExtendedLimitInformation, &limits, sizeof(limits), nullptr)
        || !QueryInformationJobObject(nullptr, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr)
        || !QueryInformationJobObject(nullptr, JobObjectBasicProcessIdList, &members, sizeof(members), nullptr)
        || members.listed > 8) return 107;
    std::string member_json = "[";
    for (DWORD index = 0; index < members.listed; ++index) {
        if (index != 0) member_json += ",";
        const auto pid = static_cast<DWORD>(members.pids[index]);
        Handle member(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid));
        std::vector<wchar_t> image(32768);
        DWORD image_size = static_cast<DWORD>(image.size());
        const bool queried = member.value && QueryFullProcessImageNameW(member.value, 0, image.data(), &image_size);
        std::string filename;
        if (queried) {
            const std::wstring full(image.data(), image_size);
            const auto base = full.substr(full.find_last_of(L"\\/") + 1);
            for (const auto character : base) filename += character < 127 && character != '"' ? static_cast<char>(character) : '?';
        }
        member_json += "{\"pid\":" + std::to_string(pid) + ",\"creation\":"
            + std::to_string(member.value ? creation_time(member.value) : 0) + ",\"image\":\"" + filename + "\"}";
    }
    member_json += "]";
    const auto hello = std::string("{\"pid\":") + std::to_string(GetCurrentProcessId())
        + ",\"creationFileTimeUtc\":" + std::to_string(creation_time(GetCurrentProcess()))
        + ",\"parentPid\":" + std::to_string(server)
        + ",\"errorMode\":" + std::to_string(GetErrorMode())
        + ",\"activeProcessLimit\":" + std::to_string(limits.BasicLimitInformation.ActiveProcessLimit)
        + ",\"activeProcessCount\":" + std::to_string(accounting.ActiveProcesses)
        + ",\"totalProcesses\":" + std::to_string(accounting.TotalProcesses)
        + ",\"terminatedProcesses\":" + std::to_string(accounting.TotalTerminatedProcesses)
        + ",\"members\":" + member_json + "}";
    if (!send(pipe.value, hello)) return 108;
    if (mode == L"no-read") suspend_probe();

    std::uint32_t length = 0;
    if (!transfer(pipe.value, &length, sizeof(length), false) || length == 0 || length > 1024 * 1024) return 109;
    std::vector<unsigned char> payload(length);
    if (!transfer(pipe.value, payload.data(), length, false)) return 110;
    if (mode == L"descendant") {
        std::vector<wchar_t> executable(32768);
        const DWORD characters = GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size()));
        if (characters == 0 || characters >= executable.size()) return 111;
        std::wstring command = L"\"" + std::wstring(executable.data()) + L"\"";
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION child{};
        const BOOL created = CreateProcessW(executable.data(), command.data(), nullptr, nullptr, FALSE,
            CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child);
        const DWORD error = created ? ERROR_SUCCESS : GetLastError();
        if (created) {
            Handle process(child.hProcess), thread(child.hThread);
            TerminateProcess(process.value, 112);
            WaitForSingleObject(process.value, 5000);
        }
        return send(pipe.value, std::string("{\"created\":") + (created ? "true" : "false")
            + ",\"error\":" + std::to_string(error) + "}") && !created ? 0 : 112;
    }
    if (!send(pipe.value, std::string(payload.begin(), payload.end()))) return 113;
    if (mode == L"hang-after-result") suspend_probe();
    return mode == L"normal" ? 0 : 114;
}
}

int wmain(int argc, wchar_t** argv) {
    const auto result = run(argc, argv);
    if (result != 0) std::fprintf(stderr, "WindowActionWorkerProbe failed: %d\n", result);
    return result;
}
