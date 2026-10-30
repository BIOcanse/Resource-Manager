#pragma once

#include "OverlayProtocol.h"
#include <sddl.h>
#include <psapi.h>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

namespace IdleReleaseTest
{
constexpr uint32_t Width = 1920, Height = 1080;

inline void Check(bool good, const char* message)
{
    if (!good) throw std::runtime_error(message);
}

template <typename T> void Drop(T*& value)
{
    if (value) { value->Release(); value = nullptr; }
}

struct Window
{
    HWND handle = CreateWindowExW(WS_EX_NOACTIVATE, L"STATIC", L"Overlay idle release test",
        WS_POPUP, 0, 0, Width, Height, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    Window() { Check(handle != nullptr, "CreateWindowExW failed"); }
    ~Window() { DestroyWindow(handle); }
};

inline std::wstring ExeDirectory()
{
    wchar_t path[32768]{};
    Check(GetModuleFileNameW(nullptr, path, 32768) != 0, "GetModuleFileNameW failed");
    const std::wstring full(path);
    return full.substr(0, full.find_last_of(L"\\/"));
}

inline void LoadOverlay()
{
    const auto path = ExeDirectory() + L"\\ResourceManager.PerformanceOverlay.dll";
    HMODULE overlay = LoadLibraryW(path.c_str());
    Check(overlay != nullptr, "LoadLibraryW overlay failed");
    using Initialize = BOOL(WINAPI*)();
    const auto address = GetProcAddress(overlay, "ResourceManagerPerformanceOverlayInitialize");
    Initialize initialize = nullptr;
    static_assert(sizeof(initialize) == sizeof(address));
    std::memcpy(&initialize, &address, sizeof(initialize));
    Check(initialize && initialize(), "Overlay initialization failed");
    // Detours remain installed until this test process exits, just as in an injected target.
}

struct PublisherWitness
{
    HANDLE process = nullptr;
    HANDLE input = nullptr;
    DWORD processId = 0;

    void Open()
    {
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
        HANDLE read = nullptr;
        Check(CreatePipe(&read, &input, &security, 0), "Create publisher witness pipe failed");
        Check(SetHandleInformation(input, HANDLE_FLAG_INHERIT, 0), "Set publisher witness pipe inheritance failed");
        HANDLE output = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, &security, OPEN_EXISTING, 0, nullptr);
        Check(output != INVALID_HANDLE_VALUE, "Open publisher witness NUL failed");
        wchar_t system[MAX_PATH]{};
        Check(GetSystemDirectoryW(system, MAX_PATH) != 0, "GetSystemDirectoryW failed");
        const std::wstring executable = std::wstring(system) + L"\\sort.exe";
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdInput = read;
        startup.hStdOutput = startup.hStdError = output;
        PROCESS_INFORMATION child{};
        const BOOL created = CreateProcessW(executable.c_str(), nullptr, nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child);
        CloseHandle(read);
        CloseHandle(output);
        Check(created != FALSE, "Start publisher witness process failed");
        CloseHandle(child.hThread);
        process = child.hProcess;
        processId = child.dwProcessId;
        Check(WaitForSingleObject(process, 0) == WAIT_TIMEOUT, "Publisher witness exited before publication");
    }

    void Close()
    {
        if (input) { CloseHandle(input); input = nullptr; }
        if (process)
        {
            // sort.exe waits only for its inherited stdin EOF; closing the pipe ends this publisher identity.
            const DWORD result = WaitForSingleObject(process, 10000);
            CloseHandle(process);
            process = nullptr;
            processId = 0;
            Check(result == WAIT_OBJECT_0, "Publisher witness did not exit after EOF");
        }
    }
};

struct Publisher
{
    PublisherWitness witness;
    HANDLE mapping = nullptr;
    OverlayProtocol::Header* header = nullptr;
    wchar_t name[96]{};

    Publisher() { Check(OverlayProtocol::SectionName(name), "SectionName failed"); }
    ~Publisher() { Close(); }

    void Open(HWND window, uint32_t color, bool publish = true)
    {
        Check(!mapping, "Publisher is already open");
        witness.Open();
        HANDLE token = nullptr;
        Check(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token), "OpenProcessToken failed");
        DWORD bytes = 0;
        GetTokenInformation(token, TokenUser, nullptr, 0, &bytes);
        std::vector<uint8_t> user(bytes);
        const bool tokenRead = GetTokenInformation(token, TokenUser, user.data(), bytes, &bytes) != FALSE;
        CloseHandle(token);
        Check(tokenRead, "GetTokenInformation failed");
        LPWSTR sid = nullptr;
        Check(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid, &sid),
            "ConvertSidToStringSidW failed");
        const std::wstring sddl = L"D:P(A;;GA;;;SY)(A;;GA;;;" + std::wstring(sid) + L")";
        LocalFree(sid);
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        Check(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), 1, &descriptor, nullptr),
            "ConvertStringSecurityDescriptorToSecurityDescriptorW failed");
        SECURITY_ATTRIBUTES security{sizeof(security), descriptor, FALSE};
        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &security, PAGE_READWRITE, 0,
            OverlayProtocol::HeaderBytes + OverlayProtocol::MaximumPixelBytes, name);
        const DWORD createError = GetLastError();
        LocalFree(descriptor);
        Check(mapping != nullptr, "CreateFileMappingW failed");
        Check(createError != ERROR_ALREADY_EXISTS, "Old section survived the disabled presents");
        header = static_cast<OverlayProtocol::Header*>(MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0));
        Check(header != nullptr, "MapViewOfFile publisher failed");
        std::memset(header, 0, sizeof(*header));
        header->magic = OverlayProtocol::Magic;
        header->version = OverlayProtocol::Version;
        header->headerBytes = OverlayProtocol::HeaderBytes;
        header->pixelCapacity = OverlayProtocol::MaximumPixelBytes;
        header->targetWindow = reinterpret_cast<uint64_t>(window);
        header->publisherProcessId = witness.processId;
        InterlockedExchange(&header->enabled, OverlayProtocol::Handshake);
        if (!publish) return;
        InterlockedExchange64(&header->generation, 1);
        header->width = Width;
        header->height = Height;
        header->stride = Width * 4;
        auto* pixels = reinterpret_cast<uint32_t*>(reinterpret_cast<uint8_t*>(header) + OverlayProtocol::HeaderBytes);
        for (size_t i = 0; i < size_t(Width) * Height; ++i) pixels[i] = color;
        // Reopening deliberately restarts at generation 2, matching a fresh publisher.
        InterlockedExchange64(&header->generation, 2);
        InterlockedExchange(&header->enabled, OverlayProtocol::Enabled);
    }

    void Disable() { if (header) InterlockedExchange(&header->enabled, OverlayProtocol::Disabled); }
    void CloseView(bool disable = true)
    {
        if (disable) Disable();
        if (header) { UnmapViewOfFile(header); header = nullptr; }
        if (mapping) { CloseHandle(mapping); mapping = nullptr; }
    }
    void Close(bool disable = true)
    {
        CloseView(disable);
        witness.Close();
    }

    void CheckGone() const
    {
        HANDLE old = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
        if (old) CloseHandle(old);
        Check(old == nullptr && GetLastError() == ERROR_FILE_NOT_FOUND,
            "Disabled reader still holds the named section object");
    }
};

inline SIZE_T PrivateCommit()
{
    PROCESS_MEMORY_COUNTERS_EX counters{};
    Check(GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters),
        sizeof(counters)), "GetProcessMemoryInfo failed");
    return counters.PrivateUsage;
}

inline SIZE_T MappedCommit()
{
    SYSTEM_INFO system{};
    GetSystemInfo(&system);
    uintptr_t address = 0;
    const uintptr_t end = reinterpret_cast<uintptr_t>(system.lpMaximumApplicationAddress);
    SIZE_T bytes = 0;
    uint32_t regions = 0;
    while (address < end && regions++ < 65536)
    {
        MEMORY_BASIC_INFORMATION memory{};
        Check(VirtualQuery(reinterpret_cast<void*>(address), &memory, sizeof(memory)) != 0,
            "VirtualQuery mapped-commit sample failed");
        if (memory.State == MEM_COMMIT && memory.Type == MEM_MAPPED) bytes += memory.RegionSize;
        const uintptr_t next = reinterpret_cast<uintptr_t>(memory.BaseAddress) + memory.RegionSize;
        Check(next > address, "VirtualQuery did not advance");
        address = next;
    }
    Check(address >= end, "Mapped-commit sample exceeded region bound");
    return bytes;
}

template <typename Renderer> void Run(Renderer& renderer, const char* backend,
    void (*checkDisabled)(Renderer&) = nullptr)
{
    Publisher publisher;
    for (int i = 0; i < 8; ++i) { renderer.Frame(); Sleep(10); }
    publisher.Open(renderer.window.handle, 0xffff0000u, false);
    for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
    Check(OverlayProtocol::Read32(&publisher.header->swapChainWidth) == static_cast<LONG>(Width)
        && OverlayProtocol::Read32(&publisher.header->swapChainHeight) == static_cast<LONG>(Height)
        && OverlayProtocol::Read64(&publisher.header->frameCounter) > 0,
        "Initially disabled section did not receive swapchain dimensions");
    Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) == 0,
        "Initially disabled overlay drew a frame");
    if (checkDisabled) checkDisabled(renderer);
    publisher.Close();
    for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
    publisher.CheckGone();
    std::printf("%s initially_disabled_dimensions=%ux%u no_draw=yes section_destroyed=yes\n", backend, Width, Height);
    const SIZE_T baseline = PrivateCommit();
    for (int cycle = 0; cycle < 2; ++cycle)
    {
        const uint32_t color = cycle ? 0xff00ff00u : 0xffff0000u;
        publisher.Open(renderer.window.handle, color);
        for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
        Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) > 0,
            "Overlay did not draw after enabling");
        Check(OverlayProtocol::Read64(&publisher.header->consumedGeneration) == 2,
            "Overlay did not consume the new generation");
        renderer.CheckPixel(color);
        const SIZE_T enabled = PrivateCommit();
        const SIZE_T enabledMapped = MappedCommit();
        const LONG64 draws = OverlayProtocol::Read64(&publisher.header->drawSuccessCounter);
        publisher.Disable();
        for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
        Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) == draws,
            "Overlay drew while disabled");
        if (checkDisabled) checkDisabled(renderer);
        const SIZE_T disabled = PrivateCommit();
        const SIZE_T disabledMapped = MappedCommit();
        publisher.Close();
        publisher.CheckGone();
        const long long drop = static_cast<long long>(enabled) - static_cast<long long>(disabled);
        std::printf("%s cycle=%d baseline_commit=%llu enabled_commit=%llu disabled_commit=%llu drop=%lld bitmap_bytes=%u section_destroyed=yes\n",
            backend, cycle + 1, static_cast<unsigned long long>(baseline),
            static_cast<unsigned long long>(enabled), static_cast<unsigned long long>(disabled), drop, Width * Height * 4);
        std::fflush(stdout);
        std::printf("%s cycle=%d enabled_mapped_commit=%llu disabled_mapped_commit=%llu mapped_drop=%lld\n",
            backend, cycle + 1, static_cast<unsigned long long>(enabledMapped),
            static_cast<unsigned long long>(disabledMapped),
            static_cast<long long>(enabledMapped) - static_cast<long long>(disabledMapped));
        // Driver allocation caches and device-local memory need not track texture COM lifetime in private commit.
        Check(drop >= 6ll * 1024 * 1024,
            "Private commit did not drop by the expected overlay allocation size");
    }
    publisher.Open(renderer.window.handle, 0xff0000ffu);
    for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
    Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) > 0,
        "Overlay did not draw before abrupt publisher close");
    renderer.CheckPixel(0xff0000ffu);
    const SIZE_T beforeClose = PrivateCommit();
    publisher.Close(false);
    for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
    publisher.CheckGone();
    if (checkDisabled) checkDisabled(renderer);
    const SIZE_T afterClose = PrivateCommit();
    std::printf("%s abrupt_publisher_close enabled_commit=%llu disabled_commit=%llu drop=%lld section_destroyed=yes\n",
        backend, static_cast<unsigned long long>(beforeClose), static_cast<unsigned long long>(afterClose),
        static_cast<long long>(beforeClose) - static_cast<long long>(afterClose));
    publisher.Open(renderer.window.handle, 0xffffff00u);
    for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
    Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) > 0,
        "Overlay did not recover after abrupt publisher close");
    renderer.CheckPixel(0xffffff00u);
    publisher.Disable();
    for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
    publisher.Close();
    for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
    publisher.CheckGone();
    // A fresh owner can restart generation 2 after the reader observes the disabled/dead publisher.
    publisher.Open(renderer.window.handle, 0xff12ab34u);
    for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
    Check(OverlayProtocol::Read64(&publisher.header->drawSuccessCounter) > 0
        && OverlayProtocol::Read64(&publisher.header->consumedGeneration) == 2,
        "Overlay did not consume a same-generation replacement after publisher retirement");
    renderer.CheckPixel(0xff12ab34u);
    std::printf("%s same_generation_replacement_after_publisher_retirement=yes\n", backend);
    publisher.Disable();
    for (int i = 0; i < 40; ++i) { renderer.Frame(); Sleep(10); }
    publisher.Close();
    publisher.CheckGone();
    if (checkDisabled) checkDisabled(renderer);
    std::printf("%s PASS enabled -> disabled -> enabled, with fresh generation and section each time\n", backend);
}
}
