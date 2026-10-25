#define wWinMain ResourceManagerGpuLaunchBrokerEntryForSessionTest
#include "../../src/Core/Native/GpuLaunchBroker/ResourceManagerGpuLaunchBroker.cpp"
#undef wWinMain
#include <cstdio>

// Calls the actual broker resolver/report functions; never launches or injects a target.
int wmain(int argc, wchar_t** argv)
{
    if (argc != 6) return 1;
    if (!SetEnvironmentVariableW(L"RESOURCE_MANAGER_PACKAGE_ROOT", argv[4])) return 8;
    const std::wstring mode(argv[5]);
    if (mode == L"frontend" || mode == L"operation")
    {
        ResourceManagerGpuLaunchSession::BackendIdentity backend;
        std::string reply;
        if (ResourceManagerGpuLaunchSession::Call(mode == L"frontend" ? 1 : 255,
            WideToUtf8(argv[3]), argv[2], ResolvePackageRoot(), argv[1], backend, false, reply)) return 5;
        std::puts("rejected");
        return 0;
    }
    if (mode == L"frame")
    {
        ResourceManagerGpuLaunchSession::BackendIdentity backend;
        std::string reply;
        if (ResourceManagerGpuLaunchSession::Call(ResourceManagerGpuLaunchSession::ResolveOperation,
            std::string(1, static_cast<char>(255)), argv[2], ResolvePackageRoot(), argv[1], backend, false, reply)) return 7;
    }
    auto decision = ResolveStartupDecision(argv[3], argv[4], argv[2], argv[1]);
    if (mode == L"corrupt")
    {
        if (decision.backend.processId) return 6;
        decision = ResolveStartupDecision(argv[3], argv[4], argv[2], argv[1]);
    }
    if (mode == L"backend" || mode == L"timeout" || mode == L"reply")
    {
        if (decision.backend.processId) return 2;
        std::puts("rejected");
        return 0;
    }
    if (decision.status != L"probe-resolved") return 3;
    if (mode == L"generation") decision.backend.creation++;
    const bool reported = PostStartupReport(decision, argv[3], GetCurrentProcessId(), L"pass-through-started", L"owned session probe");
    const bool expectReport = mode == L"valid" || mode == L"corrupt" || mode == L"frame"
        || mode == L"broker" || mode == L"sid" || mode == L"session";
    if (reported != expectReport) return 4;
    std::puts(reported ? "resolved-and-reported" : "resolved-report-rejected");
    return 0;
}

#if defined(RM_GPU_SESSION_DESKTOP_TEST)
// Reuse the same probe as a GUI-subsystem EXE for WTS: no console or window.
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int count{};
    auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!arguments) return 1;
    const auto result = wmain(count, arguments);
    LocalFree(arguments);
    return result;
}
#endif
