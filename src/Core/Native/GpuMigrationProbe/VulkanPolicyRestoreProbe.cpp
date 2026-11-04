#define wmain RetainedVulkanStartupProbeMain
#include "VulkanPlacementProbe.cpp"
#undef wmain
#include "VulkanProbeReadback.h"
#include "../GpuPlacementShim/GpuPlacementApiObservation.h"

namespace
{
using Control = DWORD(WINAPI*)(LPVOID);
Control Export(HMODULE module, const char* name)
{
    const auto address = GetProcAddress(module, name);
    Control result = nullptr;
    static_assert(sizeof(address) == sizeof(result));
    std::memcpy(&result, &address, sizeof(result));
    Check(result != nullptr, name);
    return result;
}
std::vector<VkPhysicalDevice> CachedEnumerate(PFN_vkEnumeratePhysicalDevices fn, VkInstance instance)
{
    uint32_t count = 0;
    Check(fn(instance, &count, nullptr) == VK_SUCCESS, "cached-count");
    std::vector<VkPhysicalDevice> items(count);
    if (count) Check(fn(instance, &count, items.data()) == VK_SUCCESS, "cached-data");
    items.resize(count);
    return items;
}
void SameInventory(VkInstance instance, const std::vector<VkPhysicalDevice>& current,
    VkInstance baseline, const std::vector<VkPhysicalDevice>& original)
{
    Check(current.size() == original.size(), "default-original-count");
    for (size_t i = 0; i < current.size(); ++i)
        Check(Identity(instance, current[i]) == Identity(baseline, original[i]), "default-original-order");
}
}

int wmain(int argc, wchar_t** argv)
{
    SetErrorMode(32771);
    try
    {
        Check(argc == 4, "layer-directory-policy-prefix-and-mode");
        const bool samePath = std::wcscmp(argv[3], L"same") == 0;
        Check(samePath || std::wcscmp(argv[3], L"different") == 0, "exact-restore-mode");
        const std::wstring startup = std::wstring(argv[2]) + L".startup";
        const std::wstring runtime = samePath ? startup : std::wstring(argv[2]) + L".runtime";
        Check(GetFileAttributesW(startup.c_str()) == INVALID_FILE_ATTRIBUTES &&
            GetFileAttributesW(runtime.c_str()) == INVALID_FILE_ATTRIBUTES, "fresh-owned-policy-files");
        FILETIME birth{}, exit{}, kernel{}, user{};
        BOOL inJob = FALSE;
        Check(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user) &&
            IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) && inJob, "owned-native-identity");
        std::printf("{\"pid\":%lu,\"creationFileTimeUtc\":%llu,\"errorMode\":%u,\"inJob\":true}\n",
            GetCurrentProcessId(), (static_cast<unsigned long long>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime,
            GetErrorMode());
        Check(SetEnvironmentVariableW(L"VK_ADD_LAYER_PATH", argv[1]) &&
            SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", startup.c_str()), "owned-startup-binding");
        const auto loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(loader != nullptr, "system-loader");
        const auto address = GetProcAddress(loader, "vkGetInstanceProcAddr");
        std::memcpy(&gipa, &address, sizeof(gipa));
        Check(gipa != nullptr, "loader-entry");
        const auto layerPath = std::wstring(argv[1]) + L"/ResourceManager.VulkanPlacementLayer.dll";
        const auto provider = LoadLibraryW(layerPath.c_str());
        Check(provider != nullptr, "actual-layer-loaded");
        const auto configure = Export(provider, "ResourceManagerGpuPlacementConfigure");
        const auto status = Export(provider, "ResourceManagerGpuPlacementGetStatus");
        const auto startObservation = Export(provider, "ResourceManagerGpuPlacementStartApiObservation");
        const auto readObservation = Export(provider, "ResourceManagerGpuPlacementReadApiObservation");
        const auto stopObservation = Export(provider, "ResourceManagerGpuPlacementStopApiObservation");
        {
            OwnedInstance baseline;
            Check(Create(false, VK_API_VERSION_1_1, &baseline.value) == VK_SUCCESS, "baseline-instance");
            const auto original = Enumerate(baseline.value);
            Check(original.size() >= 2, "two-real-adapters");
            const auto a = Identity(baseline.value, original[0]);
            const auto b = Identity(baseline.value, original[1]);
            Check(a && b && a != b, "distinct-real-luids");
            const auto capabilityA = CapabilitiesOf(baseline.value, original[0]);
            const auto capabilityB = CapabilitiesOf(baseline.value, original[1]);
            WritePolicy(startup.c_str(), a);
            OwnedInstance active;
            Check(Create(true, VK_API_VERSION_1_1, &active.value, true) == VK_SUCCESS, "startup-a-instance");
            const auto instance = active.value;
            const auto enumerate = IP(vkEnumeratePhysicalDevices);
            auto visible = CachedEnumerate(enumerate, instance);
            Check(visible.size() == 1 && Identity(instance, visible[0]) == a, "startup-a-selected");
            auto oldA = GpuReadback(instance, visible[0], capabilityA);
            ResourceManagerGpuObservation::CallObservationRequest observationRequest{16, 1, 4, 60000};
            ResourceManagerGpuObservation::CallObservationSnapshot observation;
            Check(startObservation(&observationRequest) == 1, "existing-layer-observation-start");
            Check(readObservation(&observation) == 1 && observation.observedApis == 0 && observation.recording == 1,
                "prior-real-devices-not-observation-evidence");
            gipa(instance, "vkEnumeratePhysicalDevices");
            status(nullptr);
            Check(readObservation(&observation) == 1 && observation.observedApis == 0, "real-metadata-does-not-record");
            visible = CachedEnumerate(enumerate, instance);
            Check(visible.size() == 1 && Identity(instance, visible[0]) == a, "observation-keeps-startup-selection");
            Check(stopObservation(&observation) == 1 && observation.observedApis == 4 && observation.recording == 0,
                "cached-real-dispatch-observed-and-stopped");
            std::printf("{\"apiObservation\":\"explicit-stop\",\"byteSize\":%u,\"version\":%u,\"apis\":%u,\"recording\":%u}\n",
                observation.byteSize, observation.version, observation.observedApis, observation.recording);
            Check(startObservation(&observationRequest) == 1 && readObservation(&observation) == 1 && observation.observedApis == 0,
                "real-observation-window-resets-only-call-evidence");
            visible = CachedEnumerate(enumerate, instance);
            WritePolicy(runtime.c_str(), b);
            Check(configure(const_cast<wchar_t*>(runtime.c_str())) == 23, "runtime-b-bound");
            Check(readObservation(&observation) == 1 && observation.observedApis == 4 && observation.recording == 0,
                "real-configuration-stops-observation");
            Check(startObservation(&observationRequest) == 0 && GetLastError() == ERROR_INVALID_STATE,
                "real-configured-layer-rejects-observation-restart");
            visible = CachedEnumerate(enumerate, instance);
            Check(visible.size() == 1 && Identity(instance, visible[0]) == b, "runtime-b-selected");
            auto oldB = GpuReadback(instance, visible[0], capabilityB);
            OwnedInstance future;
            Check(Create(true, VK_API_VERSION_1_1, &future.value, true) == VK_SUCCESS, "instance-created-during-b");
            auto futureVisible = Enumerate(future.value);
            Check(futureVisible.size() == 1 && Identity(future.value, futureVisible[0]) == b, "future-b-selected");
            if (samePath) WritePolicy(runtime.c_str(), a);
            else Check(DeleteFileW(runtime.c_str()), "restore-original-file-absence");
            visible = CachedEnumerate(enumerate, instance);
            Check(visible.size() == 1 && Identity(instance, visible[0]) == a,
                "file-rollback-restores-a-without-another-configure");
            futureVisible = Enumerate(future.value);
            Check(futureVisible.size() == 1 && Identity(future.value, futureVisible[0]) == a,
                "instance-created-during-b-also-restores-a");
            Groups(instance, a, "vkEnumeratePhysicalDeviceGroups");
            Groups(instance, a, "vkEnumeratePhysicalDeviceGroupsKHR");
            GpuReadback(instance, visible[0], capabilityA);
            RepeatReadback(*oldA, a);
            RepeatReadback(*oldB, b);
            WritePolicy(runtime.c_str(), 0);
            SameInventory(instance, CachedEnumerate(enumerate, instance), baseline.value, original);
            Check(status(nullptr) == 23, "binding-ready-is-not-active-target");
            WritePolicy(runtime.c_str(), b);
            const auto locked = CreateFileW(runtime.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
            Check(locked != INVALID_HANDLE_VALUE, "owned-file-exclusive-read-denial");
            // A read error retains the existing reader's Default semantics, not startup A fallback.
            uint32_t count = 0;
            const auto result = enumerate(instance, &count, nullptr);
            const auto closed = CloseHandle(locked);
            Check(closed && result == VK_SUCCESS && count == original.size(), "read-error-is-not-file-absence");
            Check(DeleteFileW(runtime.c_str()), "delete-owned-runtime-policy");
            if (samePath) SameInventory(instance, CachedEnumerate(enumerate, instance), baseline.value, original);
            else
            {
                visible = CachedEnumerate(enumerate, instance);
                Check(visible.size() == 1 && Identity(instance, visible[0]) == a, "deleted-runtime-restores-startup-again");
                Check(DeleteFileW(startup.c_str()), "delete-owned-startup-policy");
                SameInventory(instance, CachedEnumerate(enumerate, instance), baseline.value, original);
            }
        }
        Check(status(nullptr) == 0, "all-layer-instances-destroyed");
        Check(FreeLibrary(provider) && FreeLibrary(loader), "explicit-library-references-released");
        std::printf("{\"passed\":true,\"checks\":%u,\"samePath\":%s,\"selfOwned\":true}\n", checks, samePath ? "true" : "false");
        return 0;
    }
    catch (const std::exception& error)
    {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
