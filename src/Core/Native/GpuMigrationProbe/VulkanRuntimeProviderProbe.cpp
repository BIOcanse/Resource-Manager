#define wmain RetainedVulkanStartupProbeMain
#include "VulkanPlacementProbe.cpp"
#undef wmain
#include "VulkanProbeReadback.h"

namespace
{
using Control = DWORD(WINAPI*)(LPVOID);
Control ControlEntry(HMODULE module, const char* name)
{
    const auto address = GetProcAddress(module, name);
    Control result = nullptr;
    static_assert(sizeof(address) == sizeof(result));
    std::memcpy(&result, &address, sizeof(result));
    Check(result != nullptr, name);
    return result;
}
std::vector<VkPhysicalDevice> EnumerateCached(PFN_vkEnumeratePhysicalDevices fn, VkInstance instance)
{
    uint32_t count = 0;
    Check(fn(instance, &count, nullptr) == VK_SUCCESS, "cached-enumeration-count");
    std::vector<VkPhysicalDevice> items(count);
    if (count) Check(fn(instance, &count, items.data()) == VK_SUCCESS, "cached-enumeration-data");
    items.resize(count);
    return items;
}
}

int wmain(int argc, wchar_t** argv)
{
    SetErrorMode(32771);
    try
    {
        Check(argc == 3, "provider-and-owned-policy-prefix");
        const std::wstring startup = std::wstring(argv[2]) + L".startup";
        const std::wstring runtime = std::wstring(argv[2]) + L".runtime";
        Check(GetFileAttributesW(startup.c_str()) == INVALID_FILE_ATTRIBUTES &&
            GetFileAttributesW(runtime.c_str()) == INVALID_FILE_ATTRIBUTES, "fresh-owned-policy-files");
        Check(SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", startup.c_str()), "owned-startup-policy-binding");
        FILETIME birth{}, exit{}, kernel{}, user{};
        BOOL inJob = FALSE;
        Check(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user) &&
            IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) && inJob, "owned-native-identity");
        std::printf("{\"pid\":%lu,\"creationFileTimeUtc\":%llu,\"errorMode\":%u,\"inJob\":true}\n",
            GetCurrentProcessId(), (static_cast<unsigned long long>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime,
            GetErrorMode());
        Check(!GetModuleHandleW(L"ResourceManager.GpuPlacementShim.dll") &&
            !GetModuleHandleW(L"ResourceManager.VulkanPlacementLayer.dll"), "no-product-module-at-start");
        const auto loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(loader != nullptr, "system-loader");
        const auto entry = GetProcAddress(loader, "vkGetInstanceProcAddr");
        std::memcpy(&gipa, &entry, sizeof(gipa));
        Check(gipa != nullptr, "loader-entry");
        {
            OwnedInstance active, other;
            Check(Create(false, VK_API_VERSION_1_1, &active.value, true) == VK_SUCCESS &&
                Create(false, VK_API_VERSION_1_1, &other.value, true) == VK_SUCCESS, "two-instances-before-provider");
            const auto instance = active.value;
            const auto enumerate = IP(vkEnumeratePhysicalDevices);
            const auto coreGroups = IP(vkEnumeratePhysicalDeviceGroups);
            const auto aliasGroups = I<PFN_vkEnumeratePhysicalDeviceGroups>(instance, "vkEnumeratePhysicalDeviceGroupsKHR");
            const auto cachedCreate = IP(vkCreateDevice);
            const auto original = EnumerateCached(enumerate, instance);
            Check(original.size() >= 2, "two-real-adapters");
            const auto a = Identity(instance, original[0]);
            const auto b = Identity(instance, original[1]);
            Check(a && b && a != b, "distinct-real-luids");
            const auto capabilityA = CapabilitiesOf(instance, original[0]);
            const auto capabilityB = CapabilitiesOf(instance, original[1]);
            auto old = GpuReadbackCore(instance, original[0], capabilityA, nullptr);
            WritePolicy(startup.c_str(), a);
            const auto provider = LoadLibraryW(argv[1]);
            Check(provider != nullptr, "original-provider-loaded-after-devices");
            const auto configure = ControlEntry(provider, "ResourceManagerGpuPlacementConfigureVulkan");
            const auto status = ControlEntry(provider, "ResourceManagerGpuPlacementGetStatus");
            Check((status(nullptr) & 16) == 0, "load-alone-does-not-install-vulkan-hooks");
            WritePolicy(runtime.c_str(), b);
            Check((configure(const_cast<wchar_t*>(runtime.c_str())) & 19) == 19, "explicit-vulkan-ready");
            Check(FreeLibrary(provider), "caller-provider-reference-released");
            if (!GetModuleHandleW(argv[1]))
            {
                // Do not unwind through installed jumps after proving that their code was unloaded.
                std::printf("{\"check\":\"provider-retained-after-caller-release\",\"passed\":false}\n");
                std::printf("{\"passed\":false,\"checks\":%u}\n", ++checks);
                std::fflush(stdout);
                ExitProcess(1);
            }
            Check(true, "provider-retained-after-caller-release");
            Check((status(nullptr) & 19) == 19, "retained-provider-still-ready");
            Check(cachedCreate == IP(vkCreateDevice) && coreGroups == IP(vkEnumeratePhysicalDeviceGroups) &&
                aliasGroups == I<PFN_vkEnumeratePhysicalDeviceGroups>(instance, "vkEnumeratePhysicalDeviceGroupsKHR"),
                "saved-create-and-group-addresses-unchanged");
            // Before any post-hook enumeration, this old physical handle has no recorded instance.
            const auto beforeUnknown = GpuObservationProbe::Read(provider);
            GpuReadbackCore(instance, original[0], capabilityA, nullptr);
            const auto unknown = GpuObservationProbe::Read(provider);
            Check(unknown.api[2].returnedDeviceCount == beforeUnknown.api[2].returnedDeviceCount + 1 &&
                unknown.api[2].identity == ResourceManagerGpuObservation::Identity::Unavailable,
                "pre-hook-cached-physical-is-real-but-identity-unknown");
            for (const auto target : {b, a, b})
            {
                WritePolicy(runtime.c_str(), target);
                const auto visible = EnumerateCached(enumerate, instance);
                Check(visible.size() == 1 && Identity(instance, visible[0]) == target, "cached-entry-current-target");
                const auto otherVisible = Enumerate(other.value);
                Check(otherVisible.size() == 1 && Identity(other.value, otherVisible[0]) == target, "other-existing-instance-target");
                uint32_t zero = 0;
                VkPhysicalDevice sentinel = VK_NULL_HANDLE;
                Check(enumerate(instance, &zero, &sentinel) == VK_INCOMPLETE && zero == 0 && !sentinel,
                    "zero-capacity-preserves-output");
                for (const auto groups : {coreGroups, aliasGroups})
                {
                    uint32_t count = 1;
                    VkPhysicalDeviceGroupProperties group{};
                    group.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_GROUP_PROPERTIES;
                    Check(groups(instance, &count, &group) == VK_SUCCESS && count == 1 &&
                        group.physicalDeviceCount == 1 && Identity(instance, group.physicalDevices[0]) == target,
                        "cached-core-khr-group-real-target");
                }
                const auto expected = target == a ? capabilityA : capabilityB;
                Compare(expected, CapabilitiesOf(instance, visible[0]));
                GpuReadbackCore(instance, visible[0], expected, provider);
                const auto beforeOld = GpuObservationProbe::Read(provider);
                RepeatReadback(*old, a);
                const auto afterOld = GpuObservationProbe::Read(provider);
                Check(std::memcmp(&beforeOld, &afterOld, sizeof(beforeOld)) == 0, "old-device-stays-on-original-gpu");
                OwnedInstance future;
                Check(Create(false, VK_API_VERSION_1_1, &future.value, true) == VK_SUCCESS, "future-instance-without-layer");
                const auto next = Enumerate(future.value);
                Check(next.size() == 1 && Identity(future.value, next[0]) == target, "future-instance-current-target");
            }
            WritePolicy(runtime.c_str(), UINT64_MAX);
            Check(EnumerateCached(enumerate, instance).empty(), "missing-target-not-substituted");
            WritePolicy(runtime.c_str(), 0);
            const auto defaults = EnumerateCached(enumerate, instance);
            Check(defaults.size() == original.size(), "explicit-default-not-startup-fallback");
            for (size_t i = 0; i < defaults.size(); ++i)
                Check(defaults[i] == original[i], "default-original-physical-handles-and-order");
            WritePolicy(runtime.c_str(), b);
            Check(DeleteFileW(runtime.c_str()), "original-owner-deletes-runtime-policy");
            const auto restored = EnumerateCached(enumerate, instance);
            Check(restored.size() == 1 && Identity(instance, restored[0]) == a, "file-only-rollback-restores-startup-a");
            GpuReadbackCore(instance, restored[0], capabilityA, provider);
            Check(DeleteFileW(startup.c_str()), "remove-owned-startup-policy");
            Check(EnumerateCached(enumerate, instance).size() == original.size(), "no-policy-original-inventory");
            const auto final = GpuObservationProbe::Read(provider);
            Check(final.api[0].returnedDeviceCount == 0 && final.api[1].returnedDeviceCount == 0 &&
                final.api[3].returnedDeviceCount == 0 && final.api[4].returnedDeviceCount == 0,
                "other-api-observations-unchanged");
            // Installed entry hooks retain their module references until this self-owned process exits.
        }
        Check(FreeLibrary(loader), "explicit-loader-reference-released");
        std::printf("{\"passed\":true,\"checks\":%u,\"selfOwned\":true,\"actualProvider\":true,\"remoteInjection\":false}\n", checks);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
