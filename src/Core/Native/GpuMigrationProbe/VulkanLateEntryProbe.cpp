#define wmain RetainedVulkanStartupProbeMain
#include "VulkanPlacementProbe.cpp"
#undef wmain

namespace
{
void Entry(HMODULE loader, const char* name, const char* source, PFN_vkVoidFunction function)
{
    MEMORY_BASIC_INFORMATION region{};
    const auto address = reinterpret_cast<void*>(function);
    if (function) Check(VirtualQuery(address, &region, sizeof(region)) == sizeof(region), "function-memory-query");
    std::printf("{\"entry\":\"%s\",\"source\":\"%s\",\"address\":\"%p\",\"module\":\"%p\","
        "\"loaderModule\":%s,\"rva\":%llu}\n", name, source, address, region.AllocationBase,
        region.AllocationBase == loader ? "true" : "false",
        function ? static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(address) -
            reinterpret_cast<uintptr_t>(region.AllocationBase)) : 0);
}
}

int wmain(int argc, wchar_t**)
{
    SetErrorMode(32771);
    try
    {
        Check(argc == 1, "no-application-or-policy-arguments");
        FILETIME birth{}, exit{}, kernel{}, user{};
        BOOL inJob = FALSE;
        Check(GetProcessTimes(GetCurrentProcess(), &birth, &exit, &kernel, &user) &&
            IsProcessInJob(GetCurrentProcess(), nullptr, &inJob) && inJob, "owned-native-identity");
        std::printf("{\"pid\":%lu,\"creationFileTimeUtc\":%llu,\"errorMode\":%u,\"inJob\":true}\n",
            GetCurrentProcessId(), (static_cast<unsigned long long>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime,
            GetErrorMode());
        const auto loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(loader != nullptr, "system-loader");
        const auto raw = GetProcAddress(loader, "vkGetInstanceProcAddr");
        std::memcpy(&gipa, &raw, sizeof(gipa));
        Check(gipa != nullptr, "loader-gipa");
        {
            OwnedInstance first, second;
            Check(Create(false, VK_API_VERSION_1_1, &first.value, true) == VK_SUCCESS, "first-no-requested-layer-instance");
            Check(Create(false, VK_API_VERSION_1_1, &second.value, true) == VK_SUCCESS, "second-no-requested-layer-instance");
            Check(GetModuleHandleW(L"ResourceManager.VulkanPlacementLayer.dll") == nullptr &&
                GetModuleHandleW(L"ResourceManager.GpuPlacementShim.dll") == nullptr, "no-resource-manager-provider-loaded");
            for (const auto name : {"vkGetInstanceProcAddr", "vkEnumeratePhysicalDevices", "vkEnumeratePhysicalDeviceGroups",
                "vkEnumeratePhysicalDeviceGroupsKHR", "vkCreateDevice", "vkGetPhysicalDeviceProperties2", "vkDestroyInstance"})
            {
                const auto exported = GetProcAddress(loader, name);
                PFN_vkVoidFunction function = nullptr;
                std::memcpy(&function, &exported, sizeof(function));
                Entry(loader, name, "export", function);
                Entry(loader, name, "global-gipa", gipa(nullptr, name));
                Entry(loader, name, "cached-instance-1", gipa(first.value, name));
                Entry(loader, name, "cached-instance-2", gipa(second.value, name));
            }
            const auto original = Enumerate(first.value);
            const auto other = Enumerate(second.value);
            Check(original.size() == other.size() && original.size() >= 2, "two-real-adapters-both-instances");
            for (size_t i = 0; i < original.size(); ++i)
            {
                const auto luid = Identity(first.value, original[i]);
                Check(luid != 0 && luid == Identity(second.value, other[i]), "same-real-luid-from-each-instance");
                std::printf("{\"adapter\":true,\"luid\":\"%016llx\"}\n", static_cast<unsigned long long>(luid));
            }
        }
        Check(FreeLibrary(loader), "loader-reference-released-after-instances");
        std::printf("{\"passed\":true,\"checks\":%u,\"addressProbeOnly\":true,\"injected\":false,\"gpuDeviceCreated\":false}\n", checks);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::printf("{\"passed\":false,\"error\":\"%s\"}\n", error.what());
        return 1;
    }
}
