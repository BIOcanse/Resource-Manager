#define NOMINMAX
#define _WIN32_WINNT 0x0a00
#include <windows.h>
#include <cstdio>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
bool tracedIdentityValid = true;
unsigned checks = 0;
void Check(bool value, const char* name)
{
    ++checks;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", name, value ? "true" : "false");
    std::fflush(stdout);
    if (!value) throw std::runtime_error(name);
}
unsigned long long Creation(HANDLE process)
{
    FILETIME creation{}, exit{}, kernel{}, user{};
    return GetProcessTimes(process, &creation, &exit, &kernel, &user)
        ? (static_cast<unsigned long long>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime : 0;
}
std::wstring Quote(const std::wstring& value)
{
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t ch : value)
    {
        if (ch == L'\\') { ++slashes; continue; }
        result.append(ch == L'\"' ? slashes * 2 + 1 : slashes, L'\\');
        result += ch;
        slashes = 0;
    }
    result.append(slashes * 2, L'\\');
    return result + L'\"';
}
std::wstring Command(const std::vector<std::wstring>& arguments)
{
    std::wstring command;
    for (const auto& argument : arguments) command += (command.empty() ? L"" : L" ") + Quote(argument);
    return command;
}
HANDLE expectedJob = nullptr;
BOOL WINAPI ObservedCreateProcessW(LPCWSTR application, LPWSTR command, LPSECURITY_ATTRIBUTES processAttributes,
    LPSECURITY_ATTRIBUTES threadAttributes, BOOL inheritHandles, DWORD flags, LPVOID environment,
    LPCWSTR directory, LPSTARTUPINFOW startup, LPPROCESS_INFORMATION process)
{
    const BOOL result = ::CreateProcessW(application, command, processAttributes, threadAttributes,
        inheritHandles, flags, environment, directory, startup, process);
    if (result)
    {
        const auto creation = Creation(process->hProcess);
        BOOL owned = FALSE;
        const bool ownership = expectedJob && IsProcessInJob(process->hProcess, expectedJob, &owned) && owned;
        tracedIdentityValid = tracedIdentityValid && creation != 0 && ownership;
        std::printf("{\"createdPid\":%lu,\"creationFileTimeUtc\":%llu,\"ownedBeforeResume\":%s}\n",
            process->dwProcessId, creation, ownership ? "true" : "false");
        std::fflush(stdout);
    }
    return result;
}
}

// The test traces the real CreateProcess call; it does not replace the launch or provider logic.
#define CreateProcessW ObservedCreateProcessW
#include "../GpuLaunchBroker/ResourceManagerGpuLaunchBroker.cpp"
#undef CreateProcessW

namespace
{
void EnvironmentContract()
{
    using namespace ResourceManagerGpuLaunch;
    Environment parent{{L"Path", L"base"}, {L"=C:", L"C:\\work"}, {L"EMPTY", L""},
        {L"Vk_Instance_Layers", L"OTHER;THIRD"}, {L"VK_ADD_LAYER_PATH", L"old;older"},
        {L"RM_GPU_SHIM_POLICY_FILE", L"old-policy"}, {L"RM_GPU_SHIM_READY_EVENT", L"old-ready"}};
    const auto original = parent;
    const auto child = ConfigureChildEnvironment(parent, L"policy", L"", L"new");
    Check(parent == original, "environment-parent-not-mutated");
    Check(child.at(L"VK_INSTANCE_LAYERS") == L"VK_LAYER_RESOURCE_MANAGER_gpu_placement;OTHER;THIRD", "layer-list-preserves-case-and-order");
    Check(child.at(L"VK_ADD_LAYER_PATH") == L"new;old;older" && child.find(L"VK_LAYER_PATH") == child.end(), "additive-search-retains-system-discovery");
    Check(child.at(L"EMPTY").empty() && child.at(L"=C:") == L"C:\\work" && child.at(L"PATH") == L"base", "empty-and-drive-and-unrelated-values");
    Check(child.find(L"RM_GPU_SHIM_READY_EVENT") == child.end(), "vulkan-needs-no-direct3d-ready");
    parent[L"Vk_Layer_Path"] = L"explicit;second";
    const auto overridePath = ConfigureChildEnvironment(parent, L"policy", L"ready", L"new");
    Check(overridePath.at(L"VK_LAYER_PATH") == L"new;explicit;second" && overridePath.at(L"VK_ADD_LAYER_PATH") == L"old;older", "existing-override-search-preserved");
    parent[L"VK_LAYER_PATH"] = L"";
    const auto emptyPath = ConfigureChildEnvironment(parent, L"policy", L"", L"new");
    Check(emptyPath.at(L"VK_LAYER_PATH") == L"new", "empty-override-is-still-present");
    const auto pass = ConfigureChildEnvironment(parent, L"", L"", L"");
    Check(pass.at(L"VK_INSTANCE_LAYERS") == parent.at(L"VK_INSTANCE_LAYERS") && pass.at(L"VK_LAYER_PATH").empty() &&
        pass.find(L"RM_GPU_SHIM_POLICY_FILE") == pass.end() && pass.find(L"RM_GPU_SHIM_READY_EVENT") == pass.end(), "passthrough-does-not-enable-or-leak-provider");
    const auto block = BuildEnvironmentBlock(child);
    Check(block.size() >= 2 && block.back() == 0 && block[block.size() - 2] == 0, "unicode-environment-double-terminator");
    const auto empty = BuildEnvironmentBlock({});
    Check(empty == std::vector<wchar_t>({0, 0}), "empty-environment-double-terminator");
}
std::wstring Protocol(const std::wstring& target, const std::wstring& policy, const std::wstring& providers)
{
    return L"version=1\ndecision=inject\nstatus=prepared\nstartupProviders=" + providers +
        L"\nexecutablePath=" + target + L"\npolicyPath=" + policy + L"\n";
}
void DecisionContract(const std::wstring& target, const std::wstring& policy, const std::wstring& root)
{
    for (const auto& providers : {L"d3d-device-create-shim", L"vulkan-explicit-layer", L"d3d-device-create-shim,vulkan-explicit-layer"})
    {
        StartupDecision decision;
        ParseStartupDecision(Protocol(target, policy, providers), target, root, decision);
        Check(decision.injectDirect3D == (std::wstring(providers).find(L"d3d-") != std::wstring::npos) &&
            decision.enableVulkan == (std::wstring(providers).find(L"vulkan-") != std::wstring::npos), "actual-protocol-selects-only-requested-providers");
    }
    for (const auto& providers : {L"", L"vulkan-implicit-layer", L"unknown", L"vulkan-explicit-layer,unknown",
        L"vulkan-explicit-layer,", L"vulkan-explicit-layer,vulkan-explicit-layer"})
    {
        StartupDecision decision;
        ParseStartupDecision(Protocol(target, policy, providers), target, root, decision);
        Check(!decision.injectDirect3D && !decision.enableVulkan && decision.policyPath.empty(), "unsupported-provider-response-not-applied");
    }
    for (const auto& response : {
        Protocol(target + L".other", policy, L"vulkan-explicit-layer"),
        Protocol(target, target, L"vulkan-explicit-layer"),
        Protocol(target, policy + L".missing", L"vulkan-explicit-layer"),
        std::wstring(L"version=2\n") + Protocol(target, policy, L"vulkan-explicit-layer").substr(10)})
    {
        StartupDecision decision;
        ParseStartupDecision(response, target, root, decision);
        Check(!decision.injectDirect3D && !decision.enableVulkan, "wrong-protocol-path-or-version-rejected");
    }
}

int Worker(int argc, wchar_t** argv)
{
    Check(argc == 9, "worker-arguments");
    const std::wstring caseName = argv[2], directory = argv[3], target = argv[4], policy = argv[5], root = argv[6];
    expectedJob = OpenJobObjectW(JOB_OBJECT_QUERY, FALSE, argv[8]);
    Check(expectedJob != nullptr, "worker-opens-owned-job");
    ScopedHandle jobHandle(expectedJob);
    BOOL owned = FALSE;
    Check(IsProcessInJob(GetCurrentProcess(), expectedJob, &owned) && owned, "worker-owned-before-any-target");
    if (caseName == L"contract")
    {
        EnvironmentContract();
        DecisionContract(target, policy, root);
    }
    else
    {
        const bool fallback = caseName.rfind(L"missing", 0) == 0 || caseName == L"bootstrap-failure";
        const bool direct = caseName == L"both" || caseName == L"direct3d" || caseName == L"bootstrap-failure" || caseName.rfind(L"missing-both", 0) == 0;
        const bool vulkan = caseName != L"pass" && caseName != L"direct3d";
        const bool gpu = vulkan && caseName != L"unused" && !fallback;
        const bool delay = caseName == L"delayed" || caseName == L"unused";
        const std::wstring providers = direct ? (vulkan ? L"d3d-device-create-shim,vulkan-explicit-layer" : L"d3d-device-create-shim")
            : vulkan ? L"vulkan-explicit-layer" : L"";
        StartupDecision decision;
        if (direct || vulkan) ParseStartupDecision(Protocol(target, policy, providers), target, root, decision);
        Check((!direct && !vulkan) || decision.injectDirect3D || decision.enableVulkan, "worker-actual-decision-parser");
        Check(SetEnvironmentVariableW(L"RM_TEST_VALUE", L"kept=\u4e2d\u6587") &&
            SetEnvironmentVariableW(L"RM_TEST_EMPTY", L"") && SetEnvironmentVariableW(L"RM_GPU_SHIM_READY_EVENT", L"old-ready") &&
            SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", L"old-policy"), "worker-owned-test-environment");
        Check(SetEnvironmentVariableW(L"VK_LAYER_PATH", caseName == L"layer-path" ? root.c_str() : nullptr) &&
            SetEnvironmentVariableW(L"VK_ADD_LAYER_PATH", root.c_str()) && SetEnvironmentVariableW(L"VK_INSTANCE_LAYERS", L""), "worker-preserved-loader-search");
        Check(SetEnvironmentVariableW(L"RM_TEST_LAYER_DIRECTORY", Combine(directory, L"GpuPlacementShim").c_str()) &&
            SetEnvironmentVariableW(L"RM_TEST_OVERRIDE_SEARCH", caseName == L"layer-path" ? L"1" : L"0"), "worker-expected-loader-environment");
        ResourceManagerGpuLaunch::Environment before, after;
        Check(ResourceManagerGpuLaunch::ReadEnvironmentBlock(before), "worker-parent-environment-before");
        const auto command = Command({target, L"--broker-child", vulkan && !fallback ? argv[7] : L"0", delay ? L"6000" : L"0",
            direct && !fallback ? L"1" : L"0", gpu ? L"1" : L"0", (direct || vulkan) && !fallback ? policy : L"-",
            root, L"", L"two words", L"tail with space\\", L"quote\"\u4e2d\u6587"});
        const int result = LaunchTarget(target, command, directory, decision, true);
        Check(result == 37, "production-launch-mirrors-native-exit-37");
        Check(tracedIdentityValid, "every-production-child-has-native-identity-and-owned-job");
        Check(ResourceManagerGpuLaunch::ReadEnvironmentBlock(after) && before == after, "production-launch-never-mutates-parent-environment");
    }
    std::printf("{\"passed\":true,\"mode\":\"worker\",\"checks\":%u}\n", checks);
    return 0;
}
}

int wmain(int argc, wchar_t** argv)
{
    SetErrorMode(32771);
    try
    {
        if (argc > 1 && std::wcscmp(argv[1], L"--worker") == 0) return Worker(argc, argv);
        Check(argc == 7, "parent-arguments");
        const std::wstring name = L"Local\\ResourceManager.VulkanBrokerProbe." + std::to_wstring(GetCurrentProcessId());
        ScopedHandle job(CreateJobObjectW(nullptr, name.c_str()));
        Check(job.value && GetLastError() != ERROR_ALREADY_EXISTS, "fresh-owned-job");
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        Check(SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits)), "kill-on-close-for-owned-worker");
        SIZE_T size = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
        std::vector<unsigned char> attributes(size);
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
        startup.StartupInfo.hStdError = GetStdHandle(STD_ERROR_HANDLE);
        startup.StartupInfo.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
        startup.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
        Check(InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &size) &&
            UpdateProcThreadAttribute(startup.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &job.value, sizeof(job.value), nullptr, nullptr), "atomic-worker-job-attribute");
        auto command = Command({argv[0], L"--worker", argv[1], argv[2], argv[3], argv[4], argv[5], argv[6], name});
        PROCESS_INFORMATION worker{};
        const bool created = CreateProcessW(argv[0], command.data(), nullptr, nullptr, TRUE,
            CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, argv[5], &startup.StartupInfo, &worker);
        DeleteProcThreadAttributeList(startup.lpAttributeList);
        Check(created, "owned-worker-created-suspended");
        ScopedHandle process(worker.hProcess), thread(worker.hThread);
        const auto creation = Creation(process.value);
        BOOL owned = FALSE;
        Check(creation && IsProcessInJob(process.value, job.value, &owned) && owned, "owned-worker-native-identity");
        std::printf("{\"workerPid\":%lu,\"creationFileTimeUtc\":%llu}\n", worker.dwProcessId, creation);
        Check(ResumeThread(thread.value) == 1, "owned-worker-resumed");
        const bool exited = WaitForSingleObject(process.value, 30000) == WAIT_OBJECT_0;
        if (!exited) { TerminateJobObject(job.value, ERROR_TIMEOUT); WaitForSingleObject(process.value, 3000); }
        DWORD code = STILL_ACTIVE;
        Check(exited && GetExitCodeProcess(process.value, &code) && code == 0, "owned-worker-normal-zero-exit");
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
        const ULONGLONG deadline = GetTickCount64() + 3000;
        while (true)
        {
            Check(QueryInformationJobObject(job.value, JobObjectBasicAccountingInformation, &accounting, sizeof(accounting), nullptr), "owned-job-accounting");
            if (!accounting.ActiveProcesses || GetTickCount64() >= deadline) break;
            Sleep(10);
        }
        Check(accounting.ActiveProcesses == 0, "all-owned-worker-and-target-processes-exited");
        std::printf("{\"passed\":true,\"mode\":\"parent\",\"checks\":%u,\"activeProcessCount\":0,\"forcedCleanup\":false}\n", checks);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::printf("{\"passed\":false,\"failure\":\"%s\"}\n", error.what());
        return 1;
    }
}
