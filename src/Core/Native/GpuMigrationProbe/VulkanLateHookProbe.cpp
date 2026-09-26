#define wmain RetainedVulkanStartupProbeMain
#include "VulkanPlacementProbe.cpp"
#undef wmain
#include "VulkanProbeReadback.h"
#include <MinHook.h>
#include <atomic>

namespace
{
PFN_vkEnumeratePhysicalDevices realEnumerate = nullptr;
PFN_vkEnumeratePhysicalDeviceGroups realGroups = nullptr;
std::atomic<uint64_t> selectedLuid{0};
std::atomic<uint32_t> enumerationCalls{0}, groupCalls{0};

// This fixture changes only enumeration, never the application's device creation.
VkResult VKAPI_CALL FilterDevices(VkInstance instance, uint32_t* count, VkPhysicalDevice* output)
{
    ++enumerationCalls;
    const auto target = selectedLuid.load();
    if (!target) return realEnumerate(instance, count, output);
    try
    {
        uint32_t size = 0;
        auto result = realEnumerate(instance, &size, nullptr);
        if (result != VK_SUCCESS) return result;
        std::vector<VkPhysicalDevice> available(size);
        if (size)
        {
            result = realEnumerate(instance, &size, available.data());
            if (result != VK_SUCCESS && result != VK_INCOMPLETE) return result;
        }
        uint32_t matched = 0, written = 0;
        for (uint32_t i = 0; i < size; ++i)
        {
            if (Identity(instance, available[i]) != target) continue;
            ++matched;
            if (output && written < *count) output[written++] = available[i];
        }
        *count = output ? written : matched;
        return output && written < matched ? VK_INCOMPLETE : result;
    }
    catch (const std::bad_alloc&) { return VK_ERROR_OUT_OF_HOST_MEMORY; }
}
VkResult VKAPI_CALL FilterGroups(VkInstance instance, uint32_t* count, VkPhysicalDeviceGroupProperties* output)
{
    ++groupCalls;
    const auto target = selectedLuid.load();
    if (!target) return realGroups(instance, count, output);
    try
    {
        uint32_t size = 0;
        auto result = realGroups(instance, &size, nullptr);
        if (result != VK_SUCCESS) return result;
        std::vector<VkPhysicalDeviceGroupProperties> available(size);
        for (auto& item : available) item.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_GROUP_PROPERTIES;
        if (size)
        {
            result = realGroups(instance, &size, available.data());
            if (result != VK_SUCCESS && result != VK_INCOMPLETE) return result;
        }
        uint32_t matched = 0, written = 0;
        for (auto& item : available)
        {
            uint32_t visible = 0;
            for (uint32_t i = 0; i < item.physicalDeviceCount; ++i)
                if (Identity(instance, item.physicalDevices[i]) == target)
                    item.physicalDevices[visible++] = item.physicalDevices[i];
            if (!visible) continue;
            ++matched;
            if (output && written < *count)
            {
                output[written].physicalDeviceCount = visible;
                std::copy_n(item.physicalDevices, visible, output[written].physicalDevices);
                output[written].subsetAllocation = visible == 1 ? VK_FALSE : item.subsetAllocation;
                ++written;
            }
        }
        *count = output ? written : matched;
        return output && written < matched ? VK_INCOMPLETE : result;
    }
    catch (const std::bad_alloc&) { return VK_ERROR_OUT_OF_HOST_MEMORY; }
}
struct Hooks
{
    void* physical{};
    void* groups{};
    bool initialized = false;
    void Remove()
    {
        if (!initialized) return;
        Check(MH_DisableHook(physical) == MH_OK && MH_DisableHook(groups) == MH_OK, "owned-hooks-disabled");
        Check(MH_RemoveHook(physical) == MH_OK && MH_RemoveHook(groups) == MH_OK && MH_Uninitialize() == MH_OK,
            "owned-hooks-removed");
        initialized = false;
    }
    ~Hooks()
    {
        if (!initialized) return;
        // Fixture failure exits its own process; never leave hooks active during stack cleanup.
        if (physical) { MH_DisableHook(physical); MH_RemoveHook(physical); }
        if (groups) { MH_DisableHook(groups); MH_RemoveHook(groups); }
        MH_Uninitialize();
    }
};
std::vector<VkPhysicalDevice> Cached(PFN_vkEnumeratePhysicalDevices entry, VkInstance instance)
{
    uint32_t count = 0;
    Check(entry(instance, &count, nullptr) == VK_SUCCESS, "cached-count");
    std::vector<VkPhysicalDevice> result(count);
    if (count) Check(entry(instance, &count, result.data()) == VK_SUCCESS, "cached-data");
    result.resize(count);
    return result;
}
void CheckGroup(PFN_vkEnumeratePhysicalDeviceGroups entry, VkInstance instance, uint64_t target)
{
    uint32_t count = 0;
    Check(entry(instance, &count, nullptr) == VK_SUCCESS && count == 1, "cached-group-count-one");
    VkPhysicalDeviceGroupProperties group{};
    group.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_GROUP_PROPERTIES;
    count = 0;
    Check(entry(instance, &count, &group) == VK_INCOMPLETE && count == 0, "zero-group-capacity");
    count = 1;
    Check(entry(instance, &count, &group) == VK_SUCCESS && count == 1 && group.physicalDeviceCount == 1 &&
        Identity(instance, group.physicalDevices[0]) == target &&
        group.sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_GROUP_PROPERTIES && group.pNext == nullptr,
        "cached-group-real-target");
}
}

int wmain()
{
    SetErrorMode(32771);
    try
    {
        FILETIME birth{}, exit{}, kernel{}, user{};
        BOOL inJob = FALSE;
        Check(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user) &&
            IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) && inJob, "owned-native-identity");
        std::printf("{\"pid\":%lu,\"creationFileTimeUtc\":%llu,\"errorMode\":%u,\"inJob\":true}\n",
            GetCurrentProcessId(), (static_cast<unsigned long long>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime,
            GetErrorMode());
        const auto loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(loader != nullptr, "system-loader");
        const auto address = GetProcAddress(loader, "vkGetInstanceProcAddr");
        std::memcpy(&gipa, &address, sizeof(gipa));
        Check(gipa != nullptr, "loader-gipa");
        {
            OwnedInstance first, second;
            Check(Create(false, VK_API_VERSION_1_1, &first.value, true) == VK_SUCCESS &&
                Create(false, VK_API_VERSION_1_1, &second.value, true) == VK_SUCCESS, "two-existing-instances-before-hooks");
            Check(!GetModuleHandleW(L"ResourceManager.VulkanPlacementLayer.dll") &&
                !GetModuleHandleW(L"ResourceManager.GpuPlacementShim.dll"), "no-product-layer-or-provider");
            const auto instance = first.value;
            const auto physicalEntry = IP(vkEnumeratePhysicalDevices);
            const auto secondEntry = I<PFN_vkEnumeratePhysicalDevices>(second.value, "vkEnumeratePhysicalDevices");
            const auto groupEntry = IP(vkEnumeratePhysicalDeviceGroups);
            const auto aliasEntry = IP(vkEnumeratePhysicalDeviceGroupsKHR);
            const auto createEntry = IP(vkCreateDevice);
            const auto original = Cached(physicalEntry, instance);
            Check(original.size() == 2, "two-actual-adapters");
            const uint64_t luids[]{Identity(instance, original[0]), Identity(instance, original[1])};
            Check(luids[0] && luids[1] && luids[0] != luids[1], "two-distinct-real-luids");
            const Capabilities capabilities[]{CapabilitiesOf(instance, original[0]), CapabilitiesOf(instance, original[1])};
            auto old = GpuReadbackCore(instance, original[0], capabilities[0], nullptr);
            Hooks hooks;
            hooks.physical = reinterpret_cast<void*>(GetProcAddress(loader, "vkEnumeratePhysicalDevices"));
            hooks.groups = reinterpret_cast<void*>(GetProcAddress(loader, "vkEnumeratePhysicalDeviceGroups"));
            Check(reinterpret_cast<void*>(physicalEntry) == hooks.physical && secondEntry == physicalEntry &&
                reinterpret_cast<void*>(groupEntry) == hooks.groups && groupEntry == aliasEntry,
                "actual-cached-entries-match-hook-targets");
            Check(MH_Initialize() == MH_OK, "minhook-initialized");
            hooks.initialized = true;
            Check(MH_CreateHook(hooks.physical, reinterpret_cast<void*>(&FilterDevices), reinterpret_cast<void**>(&realEnumerate)) == MH_OK &&
                MH_CreateHook(hooks.groups, reinterpret_cast<void*>(&FilterGroups), reinterpret_cast<void**>(&realGroups)) == MH_OK,
                "two-original-trampolines-created");
            Check(MH_QueueEnableHook(hooks.physical) == MH_OK && MH_QueueEnableHook(hooks.groups) == MH_OK &&
                MH_ApplyQueued() == MH_OK, "late-hooks-enabled");
            Check(Cached(physicalEntry, instance) == original, "default-before-selection");
            for (unsigned index : {1u, 0u, 1u})
            {
                selectedLuid = luids[index];
                const auto beforeCalls = enumerationCalls.load();
                const auto visible = Cached(physicalEntry, instance);
                Check(enumerationCalls > beforeCalls && visible.size() == 1 && visible[0] == original[index],
                    "cached-entry-intercepted-real-original-handle");
                uint32_t zero = 0;
                VkPhysicalDevice untouched = original[1 - index];
                Check(physicalEntry(instance, &zero, &untouched) == VK_INCOMPLETE && zero == 0 && untouched == original[1 - index],
                    "zero-physical-capacity-no-write");
                Compare(capabilities[index], CapabilitiesOf(instance, visible[0]));
                CheckGroup(groupEntry, instance, luids[index]);
                CheckGroup(aliasEntry, instance, luids[index]);
                const auto other = Cached(secondEntry, second.value);
                Check(other.size() == 1 && Identity(second.value, other[0]) == luids[index], "second-existing-instance-selected");
                Check(IP(vkCreateDevice) == createEntry, "creation-entry-not-replaced");
                auto current = GpuReadbackCore(instance, visible[0], capabilities[index], nullptr);
                RepeatReadback(*old, luids[0]);
                {
                    OwnedInstance future;
                    Check(Create(false, VK_API_VERSION_1_1, &future.value, true) == VK_SUCCESS, "future-instance-created");
                    const auto futureVisible = Enumerate(future.value);
                    Check(futureVisible.size() == 1 && Identity(future.value, futureVisible[0]) == luids[index], "future-instance-selected");
                }
                if (index == 1)
                {
                    auto cachedOld = GpuReadbackCore(instance, original[0], capabilities[0], nullptr);
                    Check(Identity(instance, original[0]) == luids[0], "cached-old-physical-not-substituted");
                }
            }
            selectedLuid = UINT64_MAX;
            Check(Cached(physicalEntry, instance).empty(), "missing-target-empty-not-fallback");
            uint32_t count = 8;
            Check(aliasEntry(instance, &count, nullptr) == VK_SUCCESS && count == 0, "missing-target-no-groups");
            selectedLuid = 0;
            Check(Cached(physicalEntry, instance) == original, "default-restores-original-order-and-handles");
            RepeatReadback(*old, luids[0]);
            Check(enumerationCalls > 0 && groupCalls > 0, "both-hooks-really-entered");
            hooks.Remove();
            const auto finalCalls = enumerationCalls.load();
            Check(Cached(physicalEntry, instance) == original && enumerationCalls == finalCalls, "cached-entry-restored-no-hook");
            std::printf("{\"lateHooks\":true,\"physicalCalls\":%u,\"groupCalls\":%u,\"firstLuid\":\"%016llx\",\"secondLuid\":\"%016llx\"}\n",
                enumerationCalls.load(), groupCalls.load(), static_cast<unsigned long long>(luids[0]), static_cast<unsigned long long>(luids[1]));
        }
        Check(FreeLibrary(loader), "loader-reference-released");
        std::printf("{\"passed\":true,\"checks\":%u,\"selfOwned\":true,\"productIntegration\":false,\"remoteInjection\":false}\n", checks);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
