#define wmain RetainedVulkanStartupProbeMain
#include "VulkanPlacementProbe.cpp"
#undef wmain
#include "VulkanProbeReadback.h"

namespace
{
void PublishPolicy(const wchar_t* path, uint64_t target)
{
    const auto pending = std::wstring(path) + L".pending";
    WritePolicy(pending.c_str(), target);
    if (!MoveFileExW(pending.c_str(), path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        throw std::runtime_error("atomic-policy-publication");
}
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


std::vector<VkPhysicalDevice> CachedEnumerate(PFN_vkEnumeratePhysicalDevices enumerate, VkInstance instance)
{
    uint32_t count = 0;
    Check(enumerate(instance, &count, nullptr) == VK_SUCCESS, "cached-enumeration-count");
    std::vector<VkPhysicalDevice> result(count);
    if (count) Check(enumerate(instance, &count, result.data()) == VK_SUCCESS, "cached-enumeration-data");
    result.resize(count);
    return result;
}
void SameInventory(VkInstance instance, const std::vector<VkPhysicalDevice>& current,
    VkInstance baseline, const std::vector<VkPhysicalDevice>& original)
{
    Check(current.size() == original.size(), "default-inventory-size");
    for (size_t i = 0; i < current.size(); ++i)
        Check(Identity(instance, current[i]) == Identity(baseline, original[i]), "default-inventory-order-luid");
}
}

int wmain(int argc, wchar_t** argv)
{
    SetErrorMode(32771);
    try
    {
        Check(argc == 3, "layer-directory-policy-arguments");
        FILETIME birth{}, exit{}, kernel{}, user{};
        BOOL inJob = FALSE;
        Check(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user) &&
            IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) && inJob, "owned-native-identity");
        std::printf("{\"pid\":%lu,\"creationFileTimeUtc\":%llu,\"errorMode\":%u,\"inJob\":true}\n",
            GetCurrentProcessId(), (static_cast<unsigned long long>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime,
            GetErrorMode());
        Check(SetEnvironmentVariableW(L"VK_ADD_LAYER_PATH", argv[1]) &&
            SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", argv[2]), "child-only-layer-path");
        WritePolicy(argv[2], 0);
        const auto loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(loader != nullptr, "system-loader");
        const auto address = GetProcAddress(loader, "vkGetInstanceProcAddr");
        std::memcpy(&gipa, &address, sizeof(gipa));
        Check(gipa != nullptr, "loader-entry");
        const auto path = std::wstring(argv[1]) + L"/ResourceManager.VulkanPlacementLayer.dll";
        const auto provider = LoadLibraryW(path.c_str());
        Check(provider != nullptr, "owned-layer-loaded");
        const auto configure = Export(provider, "ResourceManagerGpuPlacementConfigure");
        const auto status = Export(provider, "ResourceManagerGpuPlacementGetStatus");
        Check(configure(argv[2]) == 0 && status(nullptr) == 0, "load-alone-does-not-claim-connected-layer");
        auto oldFormat = ResourceManagerGpuObservation::Snapshot{};
        oldFormat.byteSize = 80;
        oldFormat.version = 1;
        Check(Export(provider, "ResourceManagerGpuPlacementReadDeviceObservations")(&oldFormat) == 0,
            "old-observation-format-rejected");
        uint64_t firstLuid = 0, secondLuid = 0;
        {
            OwnedInstance baseline, active;
            Check(Create(false, VK_API_VERSION_1_1, &baseline.value) == VK_SUCCESS, "unfiltered-baseline-instance");
            const auto original = Enumerate(baseline.value);
            Check(original.size() >= 2, "two-real-adapters-required");
            firstLuid = Identity(baseline.value, original[0]);
            secondLuid = Identity(baseline.value, original[1]);
            Check(firstLuid && secondLuid && firstLuid != secondLuid, "two-distinct-real-luids");
            const auto firstCapabilities = CapabilitiesOf(baseline.value, original[0]);
            const auto secondCapabilities = CapabilitiesOf(baseline.value, original[1]);
            Check(Create(true, VK_API_VERSION_1_1, &active.value, true) == VK_SUCCESS, "default-layer-instance");
            const auto instance = active.value;
            const auto enumerate = IP(vkEnumeratePhysicalDevices);
            const auto before = CachedEnumerate(enumerate, instance);
            SameInventory(instance, before, baseline.value, original);
            const auto firstPhysical = before[0];
            auto oldDevice = GpuReadback(instance, firstPhysical, firstCapabilities);
            {
                OwnedInstance legacy;
                Check(Create(true, VK_API_VERSION_1_0, &legacy.value) == VK_SUCCESS, "legacy-default-instance");
                WritePolicy(argv[2], firstLuid);
                const auto legacyResult = configure(argv[2]);
                const auto identityEntry = gipa(legacy.value, "vkGetPhysicalDeviceProperties2");
                std::printf("{\"legacyConfigurationStatus\":%lu,\"applicationProperties2Entry\":%s}\n",
                    legacyResult, identityEntry ? "true" : "false");
                if (legacyResult == 0)
                    Check(status(nullptr) == 0, "legacy-rejection-does-not-bind-runtime-path");
                else
                {
                    Check(legacyResult == 23, "legacy-loader-connected-runtime-status");
                    const auto selected = Enumerate(legacy.value);
                    Check(selected.size() == 1, "legacy-loader-target-only");
                    Compare(firstCapabilities, CapabilitiesOf(legacy.value, selected[0]));
                    if (identityEntry)
                        Check(Identity(legacy.value, selected[0]) == firstLuid, "legacy-loader-exact-target-luid");
                }
            }
            for (const auto target : {firstLuid, secondLuid, firstLuid})
            {
                WritePolicy(argv[2], target);
                Check(configure(argv[2]) == 23 && status(nullptr) == 23, "explicit-runtime-configured");
                const auto visible = CachedEnumerate(enumerate, instance);
                Check(visible.size() == 1 && Identity(instance, visible[0]) == target, "same-instance-cached-entry-new-target");
                Groups(instance, target, "vkEnumeratePhysicalDeviceGroups");
                Groups(instance, target, "vkEnumeratePhysicalDeviceGroupsKHR");
                const auto expected = target == firstLuid ? firstCapabilities : secondCapabilities;
                const auto actual = CapabilitiesOf(instance, visible[0]);
                Compare(expected, actual);
                GpuReadback(instance, visible[0], actual);
                const auto fact = GpuObservationProbe::Read(provider);
                RepeatReadback(*oldDevice, firstLuid);
                const auto after = GpuObservationProbe::Read(provider);
                Check(std::memcmp(&fact, &after, sizeof(fact)) == 0, "old-device-submit-adds-no-creation-fact");
                if (target == secondLuid)
                    GpuReadback(instance, firstPhysical, firstCapabilities);
                WritePolicy(argv[2], UINT64_MAX);
                Check(CachedEnumerate(enumerate, instance).empty(), "file-current-value-updates-existing-instance");
                OwnedInstance future;
                Check(Create(true, VK_API_VERSION_1_1, &future.value) == VK_SUCCESS, "future-instance-after-file-change");
                const auto futureVisible = Enumerate(future.value);
                Check(futureVisible.empty(), "future-instance-reads-current-file-value");
            }
            WritePolicy(argv[2], UINT64_MAX);
            Check(configure(argv[2]) == 23 && CachedEnumerate(enumerate, instance).empty(), "missing-target-no-fallback");
            uint32_t groups = 99;
            Check(IP(vkEnumeratePhysicalDeviceGroups)(instance, &groups, nullptr) == VK_SUCCESS && groups == 0,
                "missing-target-no-groups");
            WritePolicy(argv[2], firstLuid);
            Check(configure(argv[2]) == 23, "configure-before-concurrent-readers");
            std::array<bool, 2> concurrent{};
            std::vector<std::thread> readers;
            for (size_t i = 0; i < concurrent.size(); ++i)
                readers.emplace_back([&, i]
                {
                    try
                    {
                        OwnedInstance own;
                        if (Create(true, VK_API_VERSION_1_1, &own.value) != VK_SUCCESS) return;
                        auto fn = I<PFN_vkEnumeratePhysicalDevices>(own.value, "vkEnumeratePhysicalDevices");
                        for (unsigned j = 0; j < 64; ++j)
                        {
                            uint32_t count = 1;
                            VkPhysicalDevice physical{};
                            if (fn(own.value, &count, &physical) != VK_SUCCESS || count != 1) return;
                            const auto luid = Identity(own.value, physical);
                            if (luid != firstLuid && luid != secondLuid) return;
                        }
                        concurrent[i] = true;
                    }
                    catch (...) { }
                });
            bool configured = true;
            for (unsigned i = 0; i < 16; ++i)
            {
                PublishPolicy(argv[2], i % 2 ? firstLuid : secondLuid);
                configured = configure(argv[2]) == 23 && configured;
            }
            for (auto& reader : readers) reader.join();
            Check(configured && concurrent[0] && concurrent[1], "concurrent-configuration-and-two-instance-readers");
            WritePolicy(argv[2], 0);
            Check(configure(argv[2]) == 23, "default-restored-explicitly");
            SameInventory(instance, CachedEnumerate(enumerate, instance), baseline.value, original);
            RepeatReadback(*oldDevice, firstLuid);
            const auto final = GpuObservationProbe::Read(provider);
            Check(final.api[0].returnedDeviceCount == 0 && final.api[1].returnedDeviceCount == 0 &&
                final.api[3].returnedDeviceCount == 0, "other-api-slots-unchanged");
        }
        Check(status(nullptr) == 0 && configure(argv[2]) == 0, "all-layer-instances-destroyed");
        Check(FreeLibrary(provider) && FreeLibrary(loader), "explicit-library-references-released");
        std::printf("{\"passed\":true,\"mode\":\"preloaded-layer-runtime\",\"checks\":%u,\"firstLuid\":\"%016llx\","
            "\"secondLuid\":\"%016llx\",\"lateInjectionTested\":false,\"selfOwned\":true}\n", checks,
            static_cast<unsigned long long>(firstLuid), static_cast<unsigned long long>(secondLuid));
        return 0;
    }
    catch (const std::exception& error)
    {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
