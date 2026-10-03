#define wWinMain ResourceManagerGpuLaunchBrokerEntryForTest
#include "../../src/Core/Native/GpuLaunchBroker/ResourceManagerGpuLaunchBroker.cpp"
#undef wWinMain

#include <cassert>

int main()
{
    std::array<wchar_t, MAX_PATH> temp{};
    assert(GetTempPathW(static_cast<DWORD>(temp.size()), temp.data()) > 0);
    const std::wstring root = Combine(temp.data(), L"rm-overlay-broker-test-" + std::to_wstring(GetTickCount64()));
    const std::wstring userData = Combine(root, L"UserData");
    const std::wstring policyRoot = Combine(userData, L"GpuPlacement");
    assert(CreateDirectoryW(root.c_str(), nullptr));
    assert(CreateDirectoryW(userData.c_str(), nullptr));
    assert(CreateDirectoryW(policyRoot.c_str(), nullptr));
    const std::wstring policyPath = Combine(policyRoot, L"policy.txt");
    HANDLE policy = CreateFileW(policyPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    assert(policy != INVALID_HANDLE_VALUE);
    CloseHandle(policy);

    const std::wstring target = Combine(root, L"game.exe");
    const std::wstring common = L"version=1\ndecision=inject\nexecutablePath=" + target + L"\n";
    StartupDecision overlayOnly;
    ParseStartupDecision(common + L"overlayEnabled=true\n", target, root, overlayOnly);
    assert(overlayOnly.injectOverlay && !overlayOnly.injectDirect3D && !overlayOnly.enableVulkan);
    assert(overlayOnly.policyPath.empty());

    StartupDecision gpuOnly;
    ParseStartupDecision(common + L"startupProviders=d3d-device-create-shim\npolicyPath=" + policyPath + L"\n",
        target, root, gpuOnly);
    assert(!gpuOnly.injectOverlay && gpuOnly.injectDirect3D && !gpuOnly.enableVulkan);

    StartupDecision both;
    ParseStartupDecision(common + L"startupProviders=d3d-device-create-shim,vulkan-explicit-layer\n"
        L"overlayEnabled=true\npolicyPath=" + policyPath + L"\n", target, root, both);
    assert(both.injectOverlay && both.injectDirect3D && both.enableVulkan);

    StartupDecision invalid;
    ParseStartupDecision(common + L"overlayEnabled=maybe\n", target, root, invalid);
    assert(!invalid.injectOverlay && invalid.status == L"untrusted-response");

    ResourceManagerGpuLaunch::Environment inherited;
    const auto child = ResourceManagerGpuLaunch::ConfigureChildEnvironment(
        inherited, L"", L"", L"C:\\gpu", L"C:\\overlay");
    assert(child.at(L"VK_INSTANCE_LAYERS") ==
        L"VK_LAYER_RESOURCE_MANAGER_performance_overlay;VK_LAYER_RESOURCE_MANAGER_gpu_placement");
    assert(child.at(L"VK_ADD_LAYER_PATH") == L"C:\\overlay;C:\\gpu");

    assert(DeleteFileW(policyPath.c_str()));
    assert(RemoveDirectoryW(policyRoot.c_str()));
    assert(RemoveDirectoryW(userData.c_str()));
    assert(RemoveDirectoryW(root.c_str()));
    return 0;
}
