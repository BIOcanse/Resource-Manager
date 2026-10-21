#define wWinMain ResourceManagerGpuLaunchBrokerEntryForTest
#include "../../src/Core/Native/GpuLaunchBroker/ResourceManagerGpuLaunchBroker.cpp"
#undef wWinMain

#include <cstdio>

// Exercise the production suspended launch/detach/resume path with this probe
// image only. No IFEO registration, provider DLL, backend or hardware is used.
int wmain(int argc, wchar_t** argv)
{
    std::array<wchar_t, 32768> image{};
    const DWORD length = GetModuleFileNameW(nullptr, image.data(), static_cast<DWORD>(image.size()));
    if (length == 0 || length >= image.size()) return 1;
    const std::wstring path(image.data(), length);
    const auto directory = path.substr(0, path.find_last_of(L"\\/"));

    if (argc == 3)
    {
        ResourceManagerGpuLaunch::Environment inherited;
        if (!ResourceManagerGpuLaunch::ReadEnvironmentBlock(inherited)
            || inherited.count(L"RM_GPU_IFEO_DEPTH") != 0
            || inherited.count(L"RM_GPU_SHIM_POLICY_FILE") != 0
            || inherited.count(L"RM_GPU_SHIM_READY_EVENT") != 0
            || inherited[L"RM_LAUNCH_PROBE_VALUE"] != L"preserved") return 2;
        if (std::wstring(argv[1]) == L"--grandchild")
        {
            ScopedHandle event(OpenEventW(EVENT_MODIFY_STATE, FALSE, argv[2]));
            return event.value != nullptr && SetEvent(event.value) ? 37 : 3;
        }
        if (std::wstring(argv[1]) != L"--child") return 4;
        // A valid same-image descendant must still launch if old caller state
        // is present again; each launch cleans only the broker's own markers.
        SetEnvironmentVariableW(L"RM_GPU_IFEO_DEPTH", L"1");
        return LaunchTarget(path, L"\"" + path + L"\" --grandchild \"" + argv[2] + L"\"",
            directory, StartupDecision{}, true);
    }
    if (argc != 1) return 5;
    SetEnvironmentVariableW(L"RM_GPU_IFEO_DEPTH", L"1");
    SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", L"stale-policy");
    SetEnvironmentVariableW(L"RM_GPU_SHIM_READY_EVENT", L"stale-ready");
    SetEnvironmentVariableW(L"RM_LAUNCH_PROBE_VALUE", L"preserved");
    // Verify ordinary pass-through and the real unavailable-provider fallback.
    for (int mode = 0; mode < 2; ++mode)
    {
        const auto eventName = L"Local\\ResourceManager.LaunchProbe." + std::to_wstring(GetCurrentProcessId())
            + L"." + std::to_wstring(mode);
        ScopedHandle completed(CreateEventW(nullptr, TRUE, FALSE, eventName.c_str()));
        if (completed.value == nullptr) return 6;
        StartupDecision decision;
        decision.injectDirect3D = mode != 0;
        // Fallback cannot inject a real DLL into the probe, even if test output
        // shares a parent with a developer payload.
        const auto emptyProviderDirectory = Combine(directory, L"nonexistent-probe-providers");
        if (FileExists(Combine(emptyProviderDirectory, L"GpuPlacementShim\\ResourceManager.GpuPlacementShim.dll")))
            return 7;
        const int result = LaunchTarget(path, L"\"" + path + L"\" --child \"" + eventName + L"\"",
            emptyProviderDirectory, decision, true);
        if (result != 37 || WaitForSingleObject(completed.value, 0) != WAIT_OBJECT_0) return 8;
    }
    std::puts("GPU broker child lifecycle: pass-through, unavailable-provider fallback, same-image descendants and exit propagation passed.");
    return 0;
}
