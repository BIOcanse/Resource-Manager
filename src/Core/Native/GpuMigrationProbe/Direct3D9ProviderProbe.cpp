// Reuse the retained native renderer and factory controls, with a separate executable entry.
#define wmain PreferenceDiagnosticEntry
#include "Direct3D9PreferenceProbe.cpp"
#undef wmain
#include "DeviceObservationProbe.h"
#include <atomic>
#include <array>
#include <thread>

namespace
{
using Configure = DWORD(WINAPI*)(LPVOID);

void WritePolicy(const std::wstring& path, const std::string& value, bool first)
{
    Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
        first ? CREATE_NEW : TRUNCATE_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    Check(file.Get() != INVALID_HANDLE_VALUE, "owned-policy-open");
    DWORD written = 0;
    Check(WriteFile(file.Get(), value.data(), static_cast<DWORD>(value.size()), &written, nullptr)
        && written == value.size(), "owned-policy-write");
}

std::string PolicyToken(LUID luid)
{
    char token[22]{};
    std::snprintf(token, sizeof(token), "0x%08x_0x%08x", static_cast<UINT>(luid.HighPart), static_cast<UINT>(luid.LowPart));
    return token;
}

LUID Target(ResourceManagerGpuPolicy::GpuShimPolicyMode mode)
{
    ResourceManagerGpuPolicy::GpuShimPolicy policy; policy.mode = mode;
    ComPtr<IDXGIAdapter1> adapter; adapter.Attach(ResourceManagerGpuPolicy::SelectAdapter(policy));
    Check(adapter != nullptr, "real-dxgi-target");
    DXGI_ADAPTER_DESC1 description{}; Hr(adapter->GetDesc1(&description), "real-target-description");
    Emit("\"event\":\"target\",\"luid\":" + J(Hex(Luid(description.AdapterLuid)))
        + ",\"vendor\":" + std::to_string(description.VendorId));
    return description.AdapterLuid;
}

void CheckFactoryIdentity(Factory& factory)
{
    ComPtr<IUnknown> first, second;
    Hr(factory.api.As(&first), "factory-IUnknown");
    ComPtr<IDirect3D9> classic;
    Hr(first.As(&classic), "factory-classic-QI");
    Hr(classic.As(&second), "factory-classic-IUnknown");
    Check(first == second, "same-COM-identity");
    ComPtr<IDirect3D9Ex> extended;
    const HRESULT result = first.As(&extended);
    Check(factory.extended ? SUCCEEDED(result) : result == E_NOINTERFACE, "same-Ex-QI-support");
    if (extended) { Hr(extended.As(&second), "factory-Ex-IUnknown"); Check(first == second, "same-Ex-COM-identity"); }
}

void CheckObservation(HMODULE provider, uint64_t before, LUID expected, const std::string& phase)
{
    const auto all = GpuObservationProbe::Read(provider);
    const auto& observation = all.api[static_cast<UINT>(ResourceManagerGpuObservation::Api::D3D9)];
    Check(observation.returnedDeviceCount == before + 1
        && observation.identity == ResourceManagerGpuObservation::Identity::Adapter
        && observation.adapterLuid == Luid(expected), "actual-D3D9-observation");
    for (UINT index = 0; index < 3; ++index)
        Check(all.api[index].returnedDeviceCount == 0, "other-API-slots-unchanged");
    Emit("\"event\":\"provider-device\",\"phase\":" + J(phase)
        + ",\"count\":" + std::to_string(observation.returnedDeviceCount) + ",\"actualLuid\":" + J(Hex(observation.adapterLuid)));
}

std::array<HRESULT, 3> InvalidDeviceInputs(Factory& factory, HWND window, const char* phase)
{
    std::array<HRESULT, 3> results{};
    for (UINT index = 0; index < results.size(); ++index) {
        auto parameters = Parameters(window);
        const D3DDEVTYPE type = index == 1 ? D3DDEVTYPE_REF : index == 2 ? D3DDEVTYPE_SW : D3DDEVTYPE_HAL;
        if (factory.extended) {
            ComPtr<IDirect3DDevice9Ex> returned;
            results[index] = factory.ex->CreateDeviceEx(UINT_MAX, type, window, Flags, &parameters, nullptr,
                returned.GetAddressOf());
            Check(!returned, "invalid-Ex-input-did-not-return-device");
        } else {
            ComPtr<IDirect3DDevice9> returned;
            results[index] = factory.api->CreateDevice(UINT_MAX, type, window, Flags, &parameters,
                returned.GetAddressOf());
            Check(!returned, "invalid-classic-input-did-not-return-device");
        }
        Check(FAILED(results[index]), "invalid-input-native-failure");
        Emit("\"event\":\"invalid-device\",\"phase\":" + J(phase) + ",\"api\":" + J(factory.Name())
            + ",\"case\":" + std::to_string(index) + ",\"hresult\":" + std::to_string(results[index]));
    }
    return results;
}

void RunNullArgument(const std::wstring& providerPath, const std::wstring& policyPath, bool extended,
    const std::wstring& kind, const std::wstring& stage)
{
    const bool nullOutput = kind == L"output", nullRef = kind == L"nullref";
    const UINT ordinal = nullRef ? UINT_MAX : 0;
    const D3DDEVTYPE type = nullRef ? D3DDEVTYPE_NULLREF : D3DDEVTYPE_HAL;
    const HWND window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, L"STATIC", L"RM D3D9 invalid argument",
        WS_POPUP, 0, 0, Side, Side, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    Check(window != nullptr, "invalid-input-hidden-window");
    try {
        Factory old(extended);
        if (stage != L"native") {
            const auto low = Target(ResourceManagerGpuPolicy::GpuShimPolicyMode::LowPower);
            const auto high = Target(ResourceManagerGpuPolicy::GpuShimPolicyMode::HighPerformance);
            LUID baseline{};
            Factory identity(true); Hr(identity.ex->GetAdapterLUID(0, &baseline), "invalid-input-baseline");
            WritePolicy(policyPath, "default", true);
            const HMODULE provider = LoadLibraryW(providerPath.c_str()); Check(provider != nullptr, "invalid-input-load-provider");
            Configure configure = nullptr; const auto address = GetProcAddress(provider, "ResourceManagerGpuPlacementConfigure");
            std::memcpy(&configure, &address, sizeof(configure)); Check(configure != nullptr, "invalid-input-configure-export");
            Check((configure(const_cast<wchar_t*>(policyPath.c_str())) & 15) == 15, "invalid-input-ready");
            Factory attached(extended); CheckFactoryIdentity(attached);
            WritePolicy(policyPath, stage == L"missing" ? "0xffffffff_0xffffffff"
                : PolicyToken(ResourceManagerGpuPolicy::SameLuid(baseline, low) ? high : low), false);
        }
        auto parameters = Parameters(window);
        Emit("\"event\":\"invalid-call-start\",\"api\":" + J(old.Name())
            + ",\"kind\":" + J(Utf8(kind)) + ",\"stage\":" + J(Utf8(stage)));
        HRESULT result = E_UNEXPECTED;
        if (extended) {
            ComPtr<IDirect3DDevice9Ex> output;
            result = old.ex->CreateDeviceEx(ordinal, type, window, Flags,
                nullOutput || nullRef ? &parameters : nullptr, nullptr, nullOutput ? nullptr : output.GetAddressOf());
            Check(!output, "invalid-Ex-no-device");
        } else {
            ComPtr<IDirect3DDevice9> output;
            result = old.api->CreateDevice(ordinal, type, window, Flags,
                nullOutput || nullRef ? &parameters : nullptr, nullOutput ? nullptr : output.GetAddressOf());
            Check(!output, "invalid-classic-no-device");
        }
        Check(FAILED(result), "invalid-call-must-fail");
        Emit("\"event\":\"invalid-call-result\",\"hresult\":" + std::to_string(result));
    } catch (...) { DestroyWindow(window); throw; }
    Check(DestroyWindow(window), "invalid-input-window-cleanup");
}

void RunProvider(const std::wstring& providerPath, const std::wstring& policyPath)
{
    ResourceManagerD3D9::HybridEnumeration mode;
    Check(mode.Open(GetModuleHandleW(L"d3d9.dll")), "qualified-system-image");
    UINT initialMode = 99; Check(mode.Read(initialMode), "initial-hybrid-value");
    const LUID low = Target(ResourceManagerGpuPolicy::GpuShimPolicyMode::LowPower);
    const LUID high = Target(ResourceManagerGpuPolicy::GpuShimPolicyMode::HighPerformance);
    Check(!ResourceManagerGpuPolicy::SameLuid(low, high), "two-real-hardware-targets");
    HWND window = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, L"STATIC", L"RM D3D9 Provider Probe",
        WS_POPUP, 0, 0, Side, Side, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    Check(window != nullptr, "provider-hidden-window");
    try
    {
        Factory oldClassic(false), oldEx(true);
        oldClassic.Create(window); oldEx.Create(window);
        oldClassic.Render("before-provider", true, false); oldEx.Render("before-provider", true, false);
        LUID baseline{}; Hr(oldEx.ex->GetAdapterLUID(0, &baseline), "baseline-ex-LUID");
        const UINT baselineVendor = DeviceVendor(oldEx);
        Check(DeviceVendor(oldClassic) == baselineVendor, "baseline-two-APIs");
        Check(Direct3DCreate9(0) == nullptr, "native-invalid-SDK-classic");
        ComPtr<IDirect3D9Ex> invalid;
        const HRESULT invalidSdk = Direct3DCreate9Ex(0, &invalid);
        Check(FAILED(invalidSdk) && !invalid, "native-invalid-SDK-ex");
        auto parameters = Parameters(window);
        ComPtr<IDirect3DDevice9> invalidDevice;
        const HRESULT invalidOrdinal = oldClassic.api->CreateDevice(UINT_MAX, D3DDEVTYPE_HAL, window, Flags, &parameters, &invalidDevice);
        Check(FAILED(invalidOrdinal) && !invalidDevice, "native-invalid-ordinal");
        const auto nativeClassicErrors = InvalidDeviceInputs(oldClassic, window, "native");
        const auto nativeExErrors = InvalidDeviceInputs(oldEx, window, "native");

        WritePolicy(policyPath, "default", true);
        const HMODULE provider = LoadLibraryW(providerPath.c_str()); Check(provider != nullptr, "load-actual-provider");
        Configure configure = nullptr; const auto address = GetProcAddress(provider, "ResourceManagerGpuPlacementConfigure");
        std::memcpy(&configure, &address, sizeof(configure)); Check(configure != nullptr, "actual-configure-export");
        Check((configure(const_cast<wchar_t*>(policyPath.c_str())) & 15) == 15, "D3D9-specific-ready-bit");
        Check(GpuObservationProbe::Read(provider, ResourceManagerGpuObservation::Api::D3D9).returnedDeviceCount == 0,
            "pre-injection-devices-not-invented");
        const auto assertRestored = [&] {
            UINT actual = 99; Check(mode.Read(actual) && actual == initialMode, "provider-restored-original-hybrid-value");
        };
        const auto createPair = [&](const std::string& phase, LUID target) {
            for (bool extended : {false, true}) {
                const auto before = GpuObservationProbe::Read(provider, ResourceManagerGpuObservation::Api::D3D9).returnedDeviceCount;
                Factory factory(extended); assertRestored(); CheckFactoryIdentity(factory);
                factory.Create(window); assertRestored();
                factory.Render(phase, true, false);
                CheckObservation(provider, before, target, phase + "-" + factory.Name());
            }
        };
        createPair("configured-default", baseline);
        Check(Direct3DCreate9(0) == nullptr, "hooked-invalid-SDK-classic");
        Check(Direct3DCreate9Ex(0, &invalid) == invalidSdk && !invalid, "hooked-invalid-SDK-ex");
        parameters = Parameters(window);
        Check(oldClassic.api->CreateDevice(UINT_MAX, D3DDEVTYPE_HAL, window, Flags, &parameters, &invalidDevice)
            == invalidOrdinal && !invalidDevice, "hooked-invalid-ordinal");

        const LUID opposite = ResourceManagerGpuPolicy::SameLuid(baseline, low) ? high : low;
        WritePolicy(policyPath, PolicyToken(opposite), false);
        Check((configure(const_cast<wchar_t*>(policyPath.c_str())) & 15) == 15, "reconfigure-same-provider");
        createPair("target-opposite", opposite);
        Check(InvalidDeviceInputs(oldClassic, window, "active-old-factory") == nativeClassicErrors,
            "active-classic-native-invalid-input-parity");
        Check(InvalidDeviceInputs(oldEx, window, "active-old-factory") == nativeExErrors,
            "active-Ex-native-invalid-input-parity");
        const auto beforeReset = GpuObservationProbe::Read(provider);
        for (auto* factory : {&oldClassic, &oldEx}) {
            factory->Reset(window, "old-device-reset"); factory->Render("old-device-reset", true, false);
            Check(DeviceVendor(*factory) == baselineVendor, "Reset-does-not-move-old-device");
            parameters = Parameters(window);
            if (factory->extended) {
                ComPtr<IDirect3DDevice9Ex> rejected;
                Check(factory->ex->CreateDeviceEx(0, D3DDEVTYPE_HAL, window, Flags, &parameters, nullptr, &rejected)
                    == D3DERR_NOTAVAILABLE && !rejected, "old-Ex-factory-cannot-expose-target");
            } else {
                Check(factory->api->CreateDevice(0, D3DDEVTYPE_HAL, window, Flags, &parameters, &invalidDevice)
                    == D3DERR_NOTAVAILABLE && !invalidDevice, "old-classic-factory-cannot-expose-target");
            }
        }
        const auto afterReset = GpuObservationProbe::Read(provider);
        Check(std::memcmp(&beforeReset, &afterReset, sizeof(beforeReset)) == 0, "reset-and-rejection-do-not-create-observations");
        assertRestored();

        std::atomic<UINT> completed{0};
        std::vector<std::thread> threads;
        for (UINT worker = 0; worker < 2; ++worker) threads.emplace_back([&] {
            try {
                for (UINT item = 0; item < 8; ++item) {
                    Factory factory(true); LUID actual{};
                    Hr(factory.ex->GetAdapterLUID(0, &actual), "concurrent-factory-identity");
                    Check(ResourceManagerGpuPolicy::SameLuid(actual, opposite), "concurrent-target");
                    ++completed;
                }
            } catch (...) { }
        });
        for (auto& thread : threads) thread.join();
        Check(completed == 16, "all-concurrent-factory-requests"); assertRestored();
        createPair("target-after-concurrency", opposite);
        WritePolicy(policyPath, PolicyToken(baseline), false); createPair("target-reverse", baseline);
        WritePolicy(policyPath, "0xffffffff_0xffffffff", false);
        const auto beforeInvalid = GpuObservationProbe::Read(provider);
        Check(InvalidDeviceInputs(oldClassic, window, "missing-target") == nativeClassicErrors,
            "missing-target-classic-native-invalid-input-parity");
        Check(InvalidDeviceInputs(oldEx, window, "missing-target") == nativeExErrors,
            "missing-target-Ex-native-invalid-input-parity");
        const auto afterInvalid = GpuObservationProbe::Read(provider);
        Check(std::memcmp(&beforeInvalid, &afterInvalid, sizeof(beforeInvalid)) == 0, "invalid-inputs-not-observed");
        Check(Direct3DCreate9(D3D_SDK_VERSION) == nullptr, "nonexistent-target-classic-rejected");
        Check(Direct3DCreate9Ex(D3D_SDK_VERSION, &invalid) == D3DERR_NOTAVAILABLE && !invalid,
            "nonexistent-target-Ex-rejected");
        assertRestored();
        WritePolicy(policyPath, "default", false); createPair("restored-default", baseline);
        Check((configure(const_cast<wchar_t*>(policyPath.c_str())) & 15) == 15, "provider-remains-ready");
        Emit("\"event\":\"provider-complete\",\"passed\":true,\"productionProvider\":true,\"remoteInjection\":false,\"originalMode\":"
            + std::to_string(initialMode) + ",\"concurrentFactories\":16,\"drawCount\":" + std::to_string(drawCount)
            + ",\"presentCalls\":0,\"baselineLuid\":" + J(Hex(Luid(baseline))) + ",\"oppositeLuid\":" + J(Hex(Luid(opposite))));
    }
    catch (...) { DestroyWindow(window); throw; }
    Check(!IsWindowVisible(window) && GetForegroundWindow() != window, "provider-window-remained-hidden");
    Check(DestroyWindow(window) && !IsWindow(window), "provider-window-destroyed");
    Emit("\"event\":\"provider-cleanup\",\"hwndDestroyed\":true");
}
}

int wmain(int argc, wchar_t** argv)
{
    try {
        Check(argc == 3 || argc == 7, "provider-and-owned-policy-paths");
        BOOL inJob = FALSE; Check(IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) && inJob, "owned-job");
        Check((GetErrorMode() & 0x8003) == 0x8003, "non-dialog-parent-policy");
        Emit("\"event\":\"process\",\"creationFileTime\":" + std::to_string(Birth(GetCurrentProcess()))
            + ",\"errorMode\":" + std::to_string(GetErrorMode()) + ",\"path\":" + J(Utf8(ModulePath())));
        if (argc == 7) {
            Check(std::wstring(argv[3]) == L"--invalid", "invalid-input-command");
            RunNullArgument(argv[1], argv[2], std::wstring(argv[4]) == L"ex", argv[5], argv[6]);
        } else RunProvider(argv[1], argv[2]);
        return 0;
    } catch (const std::exception& error) { std::fprintf(stderr, "provider-probe: %s (win32=%lu)\n", error.what(), GetLastError()); return 1; }
}
