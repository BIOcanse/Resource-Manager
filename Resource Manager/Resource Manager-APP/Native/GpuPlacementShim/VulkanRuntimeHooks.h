#pragma once

// Included in the original Provider, after its policy path and observation store.
namespace VulkanProvider
{
struct IdentitySource
{
    VkInstance instance{};
    PFN_vkGetPhysicalDeviceProperties2 properties = nullptr;
};
PFN_vkGetInstanceProcAddr getInstanceProc = nullptr;
PFN_vkEnumeratePhysicalDevices realEnumerate = nullptr;
PFN_vkEnumeratePhysicalDeviceGroups realGroups = nullptr;
PFN_vkCreateDevice realCreate = nullptr;
PFN_vkDestroyInstance realDestroyInstance = nullptr;
HMODULE moduleReference = nullptr;
INIT_ONCE initialization = INIT_ONCE_STATIC_INIT;
volatile LONG ready = 0;
volatile LONG hooksReady = 0;
std::mutex identityMutex;
std::unordered_map<VkPhysicalDevice, IdentitySource> identities;

PFN_vkGetPhysicalDeviceProperties2 IdentityProperties(VkInstance instance)
{
    auto address = getInstanceProc(instance, "vkGetPhysicalDeviceProperties2");
    if (!address) address = getInstanceProc(instance, "vkGetPhysicalDeviceProperties2KHR");
    return reinterpret_cast<PFN_vkGetPhysicalDeviceProperties2>(address);
}
ResourceManagerVulkan::Selection Selection()
{
    return ResourceManagerVulkan::ResolveSelection(
        ResourceManagerVulkan::ReadCurrentPolicy(ReadConfiguredPolicyPath()));
}
void Remember(VkInstance instance, PFN_vkGetPhysicalDeviceProperties2 properties,
    uint32_t count, const VkPhysicalDevice* physical)
{
    std::lock_guard<std::mutex> lock(identityMutex);
    for (uint32_t i = 0; i < count; ++i) identities[physical[i]] = {instance, properties};
}
VkResult VKAPI_CALL Enumerate(VkInstance instance, uint32_t* count, VkPhysicalDevice* output)
{
    if (ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::Vulkan))
        return realEnumerate(instance, count, output);
    if (InterlockedCompareExchange(&ready, 0, 0) != 1) return realEnumerate(instance, count, output);
    try
    {
        const auto properties = IdentityProperties(instance);
        const auto result = ResourceManagerVulkan::EnumeratePhysicalDevices(
            realEnumerate, instance, count, output, Selection(), properties);
        if (output && (result == VK_SUCCESS || result == VK_INCOMPLETE)) Remember(instance, properties, *count, output);
        return result;
    }
    catch (const std::bad_alloc&) { return VK_ERROR_OUT_OF_HOST_MEMORY; }
}
VkResult VKAPI_CALL Groups(VkInstance instance, uint32_t* count, VkPhysicalDeviceGroupProperties* output)
{
    if (ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::Vulkan))
        return realGroups(instance, count, output);
    if (InterlockedCompareExchange(&ready, 0, 0) != 1) return realGroups(instance, count, output);
    try
    {
        const auto properties = IdentityProperties(instance);
        const auto result = ResourceManagerVulkan::EnumerateGroups(
            realGroups, instance, count, output, Selection(), properties);
        if (output && (result == VK_SUCCESS || result == VK_INCOMPLETE))
            for (uint32_t i = 0; i < *count; ++i)
                Remember(instance, properties, output[i].physicalDeviceCount, output[i].physicalDevices);
        return result;
    }
    catch (const std::bad_alloc&) { return VK_ERROR_OUT_OF_HOST_MEMORY; }
}
VkResult VKAPI_CALL CreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* info,
    const VkAllocationCallbacks* allocator, VkDevice* output)
{
    if (ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::Vulkan))
        return realCreate(physical, info, allocator, output);
    const auto result = realCreate(physical, info, allocator, output);
    if (result != VK_SUCCESS || InterlockedCompareExchange(&ready, 0, 0) != 1) return result;
    PFN_vkGetPhysicalDeviceProperties2 properties = nullptr;
    {
        std::lock_guard<std::mutex> lock(identityMutex);
        const auto found = identities.find(physical);
        if (found != identities.end()) properties = found->second.properties;
    }
    // A pre-hook cached physical handle has no known instance; never borrow another instance's function.
    ResourceManagerVulkan::ObserveReturnedDevice(g_deviceObservations, properties, physical, info);
    return result;
}
void VKAPI_CALL DestroyInstance(VkInstance instance, const VkAllocationCallbacks* allocator)
{
    if (ObserveWithoutSelection(ResourceManagerGpuObservation::CallApi::Vulkan)
        || InterlockedCompareExchange(&ready, 0, 0) != 1) {
        realDestroyInstance(instance, allocator);
        return;
    }
    {
        std::lock_guard<std::mutex> lock(identityMutex);
        for (auto it = identities.begin(); it != identities.end();)
        {
            if (it->second.instance == instance) it = identities.erase(it);
            else ++it;
        }
    }
    realDestroyInstance(instance, allocator);
}
BOOL CALLBACK Initialize(PINIT_ONCE, PVOID, PVOID*)
{
    InterlockedExchange(&hooksReady, -1);
    // Retain the already-used loader for the lifetime of installed entry hooks; do not load an absent API.
    if (!GetModuleHandleExW(0, L"vulkan-1.dll", &moduleReference)) return TRUE;
    const auto proc = GetProcAddress(moduleReference, "vkGetInstanceProcAddr");
    static_assert(sizeof(proc) == sizeof(getInstanceProc));
    std::memcpy(&getInstanceProc, &proc, sizeof(proc));
    if (!getInstanceProc) return TRUE;
    const auto initialized = MH_Initialize();
    if (initialized != MH_OK && initialized != MH_ERROR_ALREADY_INITIALIZED) return TRUE;
    struct Entry { const char* name; LPVOID detour; LPVOID* original; LPVOID target = nullptr; };
    Entry entries[] = {
        {"vkEnumeratePhysicalDevices", reinterpret_cast<LPVOID>(&Enumerate), reinterpret_cast<LPVOID*>(&realEnumerate)},
        {"vkEnumeratePhysicalDeviceGroups", reinterpret_cast<LPVOID>(&Groups), reinterpret_cast<LPVOID*>(&realGroups)},
        {"vkCreateDevice", reinterpret_cast<LPVOID>(&CreateDevice), reinterpret_cast<LPVOID*>(&realCreate)},
        {"vkDestroyInstance", reinterpret_cast<LPVOID>(&DestroyInstance), reinterpret_cast<LPVOID*>(&realDestroyInstance)}
    };
    for (auto& entry : entries)
    {
        entry.target = reinterpret_cast<LPVOID>(GetProcAddress(moduleReference, entry.name));
        if (!entry.target || MH_CreateHook(entry.target, entry.detour, entry.original) != MH_OK) return TRUE;
    }
    // Enabled detours, including partial installations, must outlive every caller's DLL reference.
    HMODULE provider = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&Initialize), &provider)) return TRUE;
    for (const auto& entry : entries)
        if (MH_EnableHook(entry.target) != MH_OK) return TRUE;
    // A partial installation stays a transparent forwarder until target exit, never a partial selector or retry.
    InterlockedExchange(&hooksReady, 1);
    return TRUE;
}
bool EnsureEntryHooks()
{
    InitOnceExecuteOnce(&initialization, Initialize, nullptr, nullptr);
    return InterlockedCompareExchange(&hooksReady, 0, 0) == 1;
}
void Configure() { InterlockedExchange(&ready, EnsureEntryHooks() ? 1 : -1); }
}
