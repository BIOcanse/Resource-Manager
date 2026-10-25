#define wWinMain ResourceManagerGpuLaunchBrokerEntryForTest
#include "../../src/Core/Native/GpuLaunchBroker/ResourceManagerGpuLaunchBroker.cpp"
#undef wWinMain
#include <cassert>

int RunGuardProbe()
{
    int count = 0;
    auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!args) return -1;
    std::vector<std::wstring> values;
    for (int index = 0; index < count; ++index) values.emplace_back(args[index]);
    LocalFree(args);
    if (count == 4 && values[1] == L"--guard-child")
    {
        const auto sentinel = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(_wcstoui64(values[2].c_str(), nullptr, 10)));
        SetEvent(sentinel); // Parent observes whether its unrelated named event was actually inherited.
        ScopedHandle release(OpenEventW(SYNCHRONIZE, FALSE, values[3].c_str()));
        if (!release.value || WaitForSingleObject(release.value, 10000) != WAIT_OBJECT_0) return 81;
        return 0;
    }
    if (count == 5 && values[1] == L"--guard-broker")
    {
        SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
        ScopedHandle guard(CreateFileW(Combine(values[2], L"guard").c_str(), GENERIC_READ, FILE_SHARE_READ,
            &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
        ScopedHandle sentinel(OpenEventW(EVENT_MODIFY_STATE | SYNCHRONIZE, TRUE, values[3].c_str()));
        if (guard.value == INVALID_HANDLE_VALUE || !sentinel.value) return 83;
        ChildStartup startup;
        if (!startup.Prepare(guard.value)) return 84;
        auto command = L"\"" + ModulePath() + L"\" --guard-child "
            + std::to_wstring(reinterpret_cast<uintptr_t>(sentinel.value)) + L" " + values[4];
        PROCESS_INFORMATION child{};
        if (!CreateProcessW(ModulePath().c_str(), command.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.info.StartupInfo, &child)) return 85;
        ScopedHandle process(child.hProcess), thread(child.hThread);
        ScopedHandle receipt(CreateFileW(Combine(values[2], L"child.pid").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
        DWORD written = 0;
        return receipt.value != INVALID_HANDLE_VALUE && WriteFile(receipt.value, &child.dwProcessId, sizeof(DWORD), &written, nullptr)
            && written == sizeof(DWORD) ? 0 : 86;
    }
    return -1;
}

void GuardSurvivesBrokerExitAndOnlyAdmittedHandlesAreInherited(const std::wstring& root)
{
    const auto guardPath = Combine(root, L"guard"), receiptPath = Combine(root, L"child.pid");
    { ScopedHandle file(CreateFileW(guardPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr)); assert(file.value != INVALID_HANDLE_VALUE); }
    const auto suffix = std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(GetTickCount64());
    const auto sentinelName = L"Local\\RmGuardSentinel." + suffix, releaseName = L"Local\\RmGuardRelease." + suffix;
    ScopedHandle sentinel(CreateEventW(nullptr, TRUE, FALSE, sentinelName.c_str()));
    ScopedHandle release(CreateEventW(nullptr, TRUE, FALSE, releaseName.c_str()));
    assert(sentinel.value && release.value);
    auto command = L"\"" + ModulePath() + L"\" --guard-broker \"" + root + L"\" " + sentinelName + L" " + releaseName;
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION broker{};
    assert(CreateProcessW(ModulePath().c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &broker));
    ScopedHandle brokerProcess(broker.hProcess), brokerThread(broker.hThread);
    assert(WaitForSingleObject(brokerProcess.value, 5000) == WAIT_OBJECT_0);
    DWORD brokerExit = 1; assert(GetExitCodeProcess(brokerProcess.value, &brokerExit) && brokerExit == 0);
    DWORD childId = 0, bytes = 0;
    { ScopedHandle receipt(CreateFileW(receiptPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
      assert(receipt.value != INVALID_HANDLE_VALUE && ReadFile(receipt.value, &childId, sizeof(childId), &bytes, nullptr) && bytes == sizeof(childId)); }
    ScopedHandle child(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, childId));
    assert(child.value);
    { ScopedHandle exclusive(CreateFileW(guardPath.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
      assert(exclusive.value == INVALID_HANDLE_VALUE && GetLastError() == ERROR_SHARING_VIOLATION); }
    assert(SetEvent(release.value));
    assert(WaitForSingleObject(child.value, 5000) == WAIT_OBJECT_0);
    DWORD childExit = 1; assert(GetExitCodeProcess(child.value, &childExit) && childExit == 0);
    assert(WaitForSingleObject(sentinel.value, 0) == WAIT_TIMEOUT);
    { ScopedHandle exclusive(CreateFileW(guardPath.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr)); assert(exclusive.value != INVALID_HANDLE_VALUE); }
    assert(DeleteFileW(receiptPath.c_str())); assert(DeleteFileW(guardPath.c_str()));
}

int main()
{
    const int probe = RunGuardProbe();
    if (probe >= 0) return probe;
    std::array<wchar_t, MAX_PATH> temp{};
    assert(GetTempPathW(static_cast<DWORD>(temp.size()), temp.data()) > 0);
    const std::wstring root = Combine(temp.data(), L"rm-module-broker-test-" + std::to_wstring(GetTickCount64()));
    const auto userData = Combine(root, L"UserData"), policyRoot = Combine(userData, L"GpuPlacement");
    assert(CreateDirectoryW(root.c_str(), nullptr));
    assert(CreateDirectoryW(userData.c_str(), nullptr));
    assert(CreateDirectoryW(policyRoot.c_str(), nullptr));
    GuardSurvivesBrokerExitAndOnlyAdmittedHandlesAreInherited(root);
    const auto policyPath = Combine(policyRoot, L"policy.txt"), imagePath = Combine(root, L"module.dll");
    for (const auto& path : {policyPath, imagePath})
    {
        HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        assert(file != INVALID_HANDLE_VALUE); CloseHandle(file);
    }
    const auto target = Combine(root, L"game.exe");
    const auto common = L"version=2\ndecision=inject\nexecutablePath=" + target + L"\n";
    const std::wstring module = L"moduleCount=1\nmodule0.id=unrelated-module\nmodule0.kind=preload\n"
        L"module0.image.path=module.dll\nmodule0.image.length=0\n"
        L"module0.image.sha256=e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n";
    StartupDecision moduleOnly;
    ParseStartupDecision(common + module, target, root, moduleOnly);
    assert(moduleOnly.modules.size() == 1 && !moduleOnly.injectDirect3D && !moduleOnly.enableVulkan);
    assert(moduleOnly.policyPath.empty());
    StartupDecision gpuOnly;
    ParseStartupDecision(common + L"moduleCount=0\nstartupProviders=d3d-device-create-shim\npolicyPath=" + policyPath + L"\n", target, root, gpuOnly);
    assert(gpuOnly.modules.empty() && gpuOnly.injectDirect3D);
    StartupDecision both;
    ParseStartupDecision(common + module + L"startupProviders=d3d-device-create-shim,vulkan-explicit-layer\npolicyPath=" + policyPath + L"\n", target, root, both);
    assert(both.modules.size() == 1 && both.injectDirect3D && both.enableVulkan);
    for (const auto& bad : {std::wstring(L"moduleCount=0\n"), std::wstring(L"overlayEnabled=true\n"),
            std::wstring(L"moduleCount=17\n"), module + L"module0.id=duplicate\n"})
    {
        StartupDecision invalid; ParseStartupDecision(common + bad, target, root, invalid);
        assert(invalid.modules.empty() && invalid.status == L"untrusted-response");
    }
    for (const auto& replacement : {std::wstring(L"../module.dll"), std::wstring(L"C:/module.dll"), std::wstring(L"missing.dll")})
    {
        auto bad = module;
        const auto offset = bad.find(L"=module.dll");
        bad.replace(offset + 1, std::wcslen(L"module.dll"), replacement);
        StartupDecision invalid; ParseStartupDecision(common + bad, target, root, invalid);
        assert(invalid.modules.empty() && invalid.status == L"untrusted-response");
    }
    auto badDigest = module; badDigest[badDigest.find(L"sha256=") + 7] = L'0';
    StartupDecision invalidDigest; ParseStartupDecision(common + badDigest, target, root, invalidDigest);
    assert(invalidDigest.modules.empty() && invalidDigest.status == L"untrusted-response");
    ResourceManagerGpuLaunch::Environment inherited;
    const auto child = ResourceManagerGpuLaunch::ConfigureChildEnvironment(inherited, L"", L"", L"C:\\gpu", {{L"C:\\module", L"VK_LAYER_arbitrary"}});
    assert(child.at(L"VK_INSTANCE_LAYERS") == L"VK_LAYER_arbitrary;VK_LAYER_RESOURCE_MANAGER_gpu_placement");
    assert(child.at(L"VK_ADD_LAYER_PATH") == L"C:\\module;C:\\gpu");
    moduleOnly.admittedFiles.clear(); both.admittedFiles.clear();
    assert(DeleteFileW(imagePath.c_str())); assert(DeleteFileW(policyPath.c_str()));
    assert(RemoveDirectoryW(policyRoot.c_str())); assert(RemoveDirectoryW(userData.c_str())); assert(RemoveDirectoryW(root.c_str()));
    return 0;
}
