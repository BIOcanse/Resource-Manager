#define WIN32_LEAN_AND_MEAN
#define VK_NO_PROTOTYPES
#include <windows.h>
#include <vulkan/vulkan_core.h>
#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include "DeviceObservationProbe.h"

namespace
{
constexpr char LayerName[] = "VK_LAYER_RESOURCE_MANAGER_gpu_placement";
PFN_vkGetInstanceProcAddr gipa;
uint32_t checks = 0;
void Check(bool condition, const char* name)
{
    ++checks;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", name, condition ? "true" : "false");
    std::fflush(stdout);
    if (!condition) throw std::runtime_error(name);
}
template <typename T> T I(VkInstance instance, const char* name)
{
    auto address = gipa(instance, name);
    if (!address) throw std::runtime_error(name);
    return reinterpret_cast<T>(address);
}
template <typename T> T D(PFN_vkGetDeviceProcAddr gdpa, VkDevice device, const char* name)
{
    auto address = gdpa(device, name);
    if (!address) throw std::runtime_error(name);
    return reinterpret_cast<T>(address);
}
#define IP(name) I<PFN_##name>(instance, #name)
#define DP(name) D<PFN_##name>(gdpa, device, #name)

VkResult Create(bool enabled, uint32_t api, VkInstance* instance, bool groupAlias = false)
{
    VkApplicationInfo application{};
    application.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    application.apiVersion = api;
    VkInstanceCreateInfo info{};
    info.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    info.pApplicationInfo = &application;
    const char* layer = LayerName;
    info.enabledLayerCount = enabled ? 1 : 0;
    info.ppEnabledLayerNames = enabled ? &layer : nullptr;
    const char* extension = VK_KHR_DEVICE_GROUP_CREATION_EXTENSION_NAME;
    info.enabledExtensionCount = groupAlias ? 1 : 0;
    info.ppEnabledExtensionNames = groupAlias ? &extension : nullptr;
    return I<PFN_vkCreateInstance>(nullptr, "vkCreateInstance")(&info, nullptr, instance);
}
struct OwnedInstance
{
    VkInstance value{};
    ~OwnedInstance() { if (value) I<PFN_vkDestroyInstance>(value, "vkDestroyInstance")(value, nullptr); }
};
struct OwnedDevice
{
    VkDevice device{};
    PFN_vkGetDeviceProcAddr gdpa{};
    VkBuffer buffer{};
    VkDeviceMemory memory{};
    VkCommandPool pool{};
    VkFence fence{};
    VkQueue queue{};
    VkCommandBuffer command{};
    void* mapped{};
    ~OwnedDevice()
    {
        if (!device) return;
        if (mapped) DP(vkUnmapMemory)(device, memory);
        if (fence) DP(vkDestroyFence)(device, fence, nullptr);
        if (pool) DP(vkDestroyCommandPool)(device, pool, nullptr);
        if (buffer) DP(vkDestroyBuffer)(device, buffer, nullptr);
        if (memory) DP(vkFreeMemory)(device, memory, nullptr);
        DP(vkDestroyDevice)(device, nullptr);
    }
};
std::vector<VkPhysicalDevice> Enumerate(VkInstance instance)
{
    uint32_t count = 0;
    Check(IP(vkEnumeratePhysicalDevices)(instance, &count, nullptr) == VK_SUCCESS, "physical-count");
    std::vector<VkPhysicalDevice> result(count);
    if (count > 0) Check(IP(vkEnumeratePhysicalDevices)(instance, &count, result.data()) == VK_SUCCESS, "physical-data");
    result.resize(count);
    return result;
}
uint64_t Identity(VkInstance instance, VkPhysicalDevice physical)
{
    VkPhysicalDeviceIDProperties identity{};
    identity.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES;
    VkPhysicalDeviceProperties2 properties{};
    properties.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2;
    properties.pNext = &identity;
    IP(vkGetPhysicalDeviceProperties2)(physical, &properties);
    if (!identity.deviceLUIDValid) return 0;
    uint64_t luid = 0;
    std::memcpy(&luid, identity.deviceLUID, sizeof(luid));
    return luid;
}
void WritePolicy(const wchar_t* path, uint64_t target)
{
    FILE* file = _wfopen(path, L"wb");
    if (!file) throw std::runtime_error("write-policy");
    if (target)
        std::fprintf(file, "version=1\nprovider=d3d-device-create-shim\nmode=targetLuid\ntargetLuid=0x%08x_0x%08x\n",
            static_cast<unsigned>(target >> 32), static_cast<unsigned>(target));
    else std::fprintf(file, "version=1\nmode=default\n");
    if (std::fclose(file) != 0) throw std::runtime_error("close-policy");
}
struct Capabilities
{
    VkPhysicalDeviceProperties properties{};
    VkPhysicalDeviceFeatures features{};
    VkPhysicalDeviceMemoryProperties memory{};
    std::vector<VkQueueFamilyProperties> queues;
    std::vector<VkExtensionProperties> extensions;
};
Capabilities CapabilitiesOf(VkInstance instance, VkPhysicalDevice physical)
{
    Capabilities result;
    IP(vkGetPhysicalDeviceProperties)(physical, &result.properties);
    IP(vkGetPhysicalDeviceFeatures)(physical, &result.features);
    IP(vkGetPhysicalDeviceMemoryProperties)(physical, &result.memory);
    uint32_t count = 0;
    IP(vkGetPhysicalDeviceQueueFamilyProperties)(physical, &count, nullptr);
    result.queues.resize(count);
    IP(vkGetPhysicalDeviceQueueFamilyProperties)(physical, &count, result.queues.data());
    count = 0;
    Check(IP(vkEnumerateDeviceExtensionProperties)(physical, nullptr, &count, nullptr) == VK_SUCCESS, "extension-count");
    result.extensions.resize(count);
    Check(IP(vkEnumerateDeviceExtensionProperties)(physical, nullptr, &count, result.extensions.data()) == VK_SUCCESS, "extension-data");
    return result;
}
void Compare(const Capabilities& expected, const Capabilities& actual)
{
    Check(expected.properties.apiVersion == actual.properties.apiVersion &&
        expected.properties.vendorID == actual.properties.vendorID && expected.properties.deviceID == actual.properties.deviceID &&
        std::strcmp(expected.properties.deviceName, actual.properties.deviceName) == 0, "properties-unchanged");
    Check(std::memcmp(&expected.features, &actual.features, sizeof(expected.features)) == 0, "features-unchanged");
    bool memory = expected.memory.memoryTypeCount == actual.memory.memoryTypeCount && expected.memory.memoryHeapCount == actual.memory.memoryHeapCount;
    for (uint32_t i = 0; memory && i < expected.memory.memoryTypeCount; ++i)
        memory = expected.memory.memoryTypes[i].heapIndex == actual.memory.memoryTypes[i].heapIndex &&
            expected.memory.memoryTypes[i].propertyFlags == actual.memory.memoryTypes[i].propertyFlags;
    for (uint32_t i = 0; memory && i < expected.memory.memoryHeapCount; ++i)
        memory = expected.memory.memoryHeaps[i].size == actual.memory.memoryHeaps[i].size && expected.memory.memoryHeaps[i].flags == actual.memory.memoryHeaps[i].flags;
    Check(memory, "memory-capabilities-unchanged");
    bool queues = expected.queues.size() == actual.queues.size();
    for (size_t i = 0; queues && i < expected.queues.size(); ++i)
        queues = std::memcmp(&expected.queues[i], &actual.queues[i], sizeof(VkQueueFamilyProperties)) == 0;
    Check(queues, "all-queue-families-unchanged");
    bool extensions = expected.extensions.size() == actual.extensions.size();
    for (size_t i = 0; extensions && i < expected.extensions.size(); ++i)
        extensions = std::strcmp(expected.extensions[i].extensionName, actual.extensions[i].extensionName) == 0 &&
            expected.extensions[i].specVersion == actual.extensions[i].specVersion;
    Check(extensions, "all-device-extensions-unchanged");
}

std::unique_ptr<OwnedDevice> GpuReadbackCore(VkInstance instance, VkPhysicalDevice physical,
    const Capabilities& capabilities, HMODULE observationProvider)
{
    auto retained = std::make_unique<OwnedDevice>();
    uint32_t family = UINT32_MAX;
    for (uint32_t i = 0; i < capabilities.queues.size(); ++i)
        if (capabilities.queues[i].queueCount && (capabilities.queues[i].queueFlags & VK_QUEUE_GRAPHICS_BIT)) { family = i; break; }
    Check(family != UINT32_MAX, "real-graphics-queue-available");
    float priority = 1.0f;
    VkDeviceQueueCreateInfo queueInfo{};
    queueInfo.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    queueInfo.queueFamilyIndex = family;
    queueInfo.queueCount = 1;
    queueInfo.pQueuePriorities = &priority;
    VkDeviceGroupDeviceCreateInfo groupInfo{};
    groupInfo.sType = VK_STRUCTURE_TYPE_DEVICE_GROUP_DEVICE_CREATE_INFO;
    groupInfo.physicalDeviceCount = 1;
    groupInfo.pPhysicalDevices = &physical;
    VkDeviceCreateInfo info{};
    info.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    info.pNext = &groupInfo;
    info.queueCreateInfoCount = 1;
    info.pQueueCreateInfos = &queueInfo;
    VkDevice device{};
    const char* missing = "VK_RESOURCE_MANAGER_nonexistent_probe_extension";
    info.enabledExtensionCount = 1;
    info.ppEnabledExtensionNames = &missing;
    const auto before = observationProvider ? GpuObservationProbe::Read(observationProvider)
        : ResourceManagerGpuObservation::Snapshot{};
    Check(IP(vkCreateDevice)(physical, &info, nullptr, &device) == VK_ERROR_EXTENSION_NOT_PRESENT, "missing-extension-not-hidden");
    if (observationProvider)
    {
        const auto failed = GpuObservationProbe::Read(observationProvider);
        Check(std::memcmp(&before, &failed, sizeof(before)) == 0, "failed-vulkan-create-has-no-device-fact");
    }
    info.enabledExtensionCount = 0;
    info.ppEnabledExtensionNames = nullptr;
    Check(IP(vkCreateDevice)(physical, &info, nullptr, &device) == VK_SUCCESS, "real-logical-device-created");
    const auto gdpa = IP(vkGetDeviceProcAddr);
    auto& owned = *retained;
    owned.device = device;
    owned.gdpa = gdpa;
    if (observationProvider)
    {
    const auto fact = GpuObservationProbe::Read(observationProvider, ResourceManagerGpuObservation::Api::Vulkan);
    const auto beforeCount = before.api[static_cast<uint32_t>(ResourceManagerGpuObservation::Api::Vulkan)].returnedDeviceCount;
    Check(fact.returnedDeviceCount == beforeCount + 1 && fact.identity == ResourceManagerGpuObservation::Identity::Adapter &&
        fact.adapterLuid == Identity(instance, physical), "vulkan-fact-matches-returned-device-physical-adapter");
    std::printf("{\"deviceObservation\":true,\"apiIndex\":2,\"returnedDeviceCount\":%llu,\"actualLuid\":\"%016llx\"}\n",
        static_cast<unsigned long long>(fact.returnedDeviceCount), static_cast<unsigned long long>(fact.adapterLuid));
    }
    Check(gdpa(device, "vkEnumeratePhysicalDevices") == nullptr && gdpa(device, "vkRmUnknownFunction") == nullptr, "device-dispatch-does-not-invent-functions");
    auto& buffer = owned.buffer;
    auto& memory = owned.memory;
    auto& pool = owned.pool;
    auto& fence = owned.fence;
    auto& mapped = owned.mapped;
    VkBufferCreateInfo bufferInfo{};
    bufferInfo.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    bufferInfo.size = 4096;
    bufferInfo.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    bufferInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    Check(DP(vkCreateBuffer)(device, &bufferInfo, nullptr, &buffer) == VK_SUCCESS, "buffer-created");
    VkMemoryRequirements requirements{};
    DP(vkGetBufferMemoryRequirements)(device, buffer, &requirements);
    uint32_t memoryType = UINT32_MAX;
    for (uint32_t i = 0; i < capabilities.memory.memoryTypeCount; ++i)
        if ((requirements.memoryTypeBits & (1u << i)) && (capabilities.memory.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT))
        { memoryType = i; break; }
    Check(memoryType != UINT32_MAX, "real-host-visible-memory-type");
    VkMemoryAllocateInfo allocation{};
    allocation.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    allocation.allocationSize = requirements.size;
    allocation.memoryTypeIndex = memoryType;
    Check(DP(vkAllocateMemory)(device, &allocation, nullptr, &memory) == VK_SUCCESS &&
        DP(vkBindBufferMemory)(device, buffer, memory, 0) == VK_SUCCESS, "memory-allocated-bound");
    Check(DP(vkMapMemory)(device, memory, 0, VK_WHOLE_SIZE, 0, &mapped) == VK_SUCCESS, "memory-mapped");
    std::memset(mapped, 0, 4096);
    VkMappedMemoryRange range{};
    range.sType = VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE;
    range.memory = memory;
    range.size = VK_WHOLE_SIZE;
    Check(DP(vkFlushMappedMemoryRanges)(device, 1, &range) == VK_SUCCESS, "initial-zero-flushed");
    VkCommandPoolCreateInfo poolInfo{};
    poolInfo.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    poolInfo.queueFamilyIndex = family;
    Check(DP(vkCreateCommandPool)(device, &poolInfo, nullptr, &pool) == VK_SUCCESS, "command-pool-created");
    VkCommandBufferAllocateInfo commandInfo{};
    commandInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    commandInfo.commandPool = pool;
    commandInfo.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    commandInfo.commandBufferCount = 1;
    auto& command = owned.command;
    Check(DP(vkAllocateCommandBuffers)(device, &commandInfo, &command) == VK_SUCCESS, "command-allocated");
    VkCommandBufferBeginInfo begin{};
    begin.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    Check(DP(vkBeginCommandBuffer)(command, &begin) == VK_SUCCESS, "command-begun");
    DP(vkCmdFillBuffer)(command, buffer, 0, 4096, 0x52AFC37Du);
    VkMemoryBarrier barrier{};
    barrier.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
    barrier.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    barrier.dstAccessMask = VK_ACCESS_HOST_READ_BIT;
    DP(vkCmdPipelineBarrier)(command, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &barrier, 0, nullptr, 0, nullptr);
    Check(DP(vkEndCommandBuffer)(command) == VK_SUCCESS, "command-ended");
    VkFenceCreateInfo fenceInfo{};
    fenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    Check(DP(vkCreateFence)(device, &fenceInfo, nullptr, &fence) == VK_SUCCESS, "fence-created");
    auto& queue = owned.queue;
    DP(vkGetDeviceQueue)(device, family, 0, &queue);
    VkSubmitInfo submit{};
    submit.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    submit.commandBufferCount = 1;
    submit.pCommandBuffers = &command;
    Check(DP(vkQueueSubmit)(queue, 1, &submit, fence) == VK_SUCCESS, "real-queue-submit");
    const auto waitResult = DP(vkWaitForFences)(device, 1, &fence, VK_TRUE, 3000000000ull);
    if (waitResult != VK_SUCCESS)
    {
        // Do not destroy an in-flight command pool or wait unboundedly after timeout.
        std::printf("{\"passed\":false,\"fenceResult\":%d,\"cleanup\":\"failed-probe-process-exit\"}\n", waitResult);
        std::fflush(stdout);
        ExitProcess(1);
    }
    Check(true, "gpu-fence-within-three-seconds");
    Check(DP(vkInvalidateMappedMemoryRanges)(device, 1, &range) == VK_SUCCESS, "gpu-memory-invalidated");
    const auto data = static_cast<const uint32_t*>(mapped);
    Check(std::all_of(data, data + 1024, [](uint32_t value) { return value == 0x52AFC37Du; }), "all-1024-gpu-written-words-match");
    std::printf("{\"gpuReadback\":true,\"luid\":\"%016llx\",\"queueFamily\":%u,\"wordCount\":1024,\"deviceName\":\"%s\"}\n",
        static_cast<unsigned long long>(Identity(instance, physical)), family, capabilities.properties.deviceName);
    return retained;
}
std::unique_ptr<OwnedDevice> GpuReadback(VkInstance instance, VkPhysicalDevice physical, const Capabilities& capabilities)
{
    const auto provider = GetModuleHandleW(L"ResourceManager.VulkanPlacementLayer.dll");
    Check(provider != nullptr, "active-layer-observation-export-module");
    return GpuReadbackCore(instance, physical, capabilities, provider);
}
void Groups(VkInstance instance, uint64_t target, const char* name)
{
    auto enumerate = I<PFN_vkEnumeratePhysicalDeviceGroups>(instance, name);
    uint32_t count = 0;
    Check(enumerate(instance, &count, nullptr) == VK_SUCCESS && count == 1, "target-group-count");
    VkPhysicalDeviceGroupProperties group{};
    group.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_GROUP_PROPERTIES;
    count = 0;
    Check(enumerate(instance, &count, &group) == VK_INCOMPLETE && count == 0, "group-zero-capacity-incomplete");
    count = 1;
    Check(enumerate(instance, &count, &group) == VK_SUCCESS && count == 1 && group.physicalDeviceCount == 1 &&
        Identity(instance, group.physicalDevices[0]) == target && group.pNext == nullptr && group.subsetAllocation == VK_FALSE, "group-matches-visible-physical-device");
}

bool ConcurrentDevice(uint64_t target)
{
    try
    {
        OwnedInstance ownedInstance;
        if (Create(true, VK_API_VERSION_1_1, &ownedInstance.value) != VK_SUCCESS) return false;
        const VkInstance instance = ownedInstance.value;
        uint32_t count = 1;
        VkPhysicalDevice physical{};
        if (IP(vkEnumeratePhysicalDevices)(instance, &count, &physical) != VK_SUCCESS || count != 1 || Identity(instance, physical) != target) return false;
        uint32_t familyCount = 0;
        IP(vkGetPhysicalDeviceQueueFamilyProperties)(physical, &familyCount, nullptr);
        std::vector<VkQueueFamilyProperties> families(familyCount);
        IP(vkGetPhysicalDeviceQueueFamilyProperties)(physical, &familyCount, families.data());
        uint32_t family = UINT32_MAX;
        for (uint32_t i = 0; i < familyCount; ++i)
            if (families[i].queueCount && (families[i].queueFlags & VK_QUEUE_GRAPHICS_BIT)) { family = i; break; }
        if (family == UINT32_MAX) return false;
        float priority = 1.0f;
        VkDeviceQueueCreateInfo queue{};
        queue.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
        queue.queueFamilyIndex = family;
        queue.queueCount = 1;
        queue.pQueuePriorities = &priority;
        VkDeviceCreateInfo info{};
        info.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
        info.queueCreateInfoCount = 1;
        info.pQueueCreateInfos = &queue;
        OwnedDevice owned;
        owned.gdpa = IP(vkGetDeviceProcAddr);
        const auto result = IP(vkCreateDevice)(physical, &info, nullptr, &owned.device);
        if (result != VK_SUCCESS) { owned.device = VK_NULL_HANDLE; return false; }
        const auto address = owned.gdpa(owned.device, "vkGetDeviceQueue");
        if (!address) return false;
        VkQueue actual{};
        reinterpret_cast<PFN_vkGetDeviceQueue>(address)(owned.device, family, 0, &actual);
        return actual != VK_NULL_HANDLE;
    }
    catch (...) { return false; }
}
}

int wmain(int argc, wchar_t** argv)
{
    SetErrorMode(32771);
    try
    {
        if (argc == 2 && std::wcscmp(argv[1], L"--inventory") == 0)
        {
            const HMODULE loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            Check(loader != nullptr, "inventory-system-loader");
            const FARPROC raw = GetProcAddress(loader, "vkGetInstanceProcAddr");
            std::memcpy(&gipa, &raw, sizeof(gipa));
            Check(gipa != nullptr, "inventory-loader-entry");
            OwnedInstance instance;
            Check(Create(false, VK_API_VERSION_1_1, &instance.value) == VK_SUCCESS, "inventory-instance");
            for (auto physical : Enumerate(instance.value))
            {
                const uint64_t luid = Identity(instance.value, physical);
                if (luid) std::printf("{\"adapter\":true,\"luid\":\"%016llx\"}\n", static_cast<unsigned long long>(luid));
            }
            std::printf("{\"passed\":true,\"mode\":\"inventory\",\"checks\":%u}\n", checks);
            return 0;
        }
        if (argc == 12 && std::wcscmp(argv[1], L"--broker-child") == 0)
        {
            const auto environment = [](const wchar_t* name)
            {
                const DWORD size = GetEnvironmentVariableW(name, nullptr, 0);
                std::vector<wchar_t> value(size ? size : 1);
                if (size) GetEnvironmentVariableW(name, value.data(), size);
                return std::wstring(value.data());
            };
            FILETIME created{}, exited{}, kernel{}, user{};
            Check(GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user), "broker-child-native-identity");
            std::printf("{\"childPid\":%lu,\"creationFileTimeUtc\":%llu}\n", GetCurrentProcessId(),
                (static_cast<unsigned long long>(created.dwHighDateTime) << 32) | created.dwLowDateTime);
            Check(environment(L"RM_GPU_IFEO_DEPTH") == L"1", "broker-child-depth");
            const std::wstring expectedPolicy = std::wcscmp(argv[6], L"-") == 0 ? L"" : argv[6];
            Check(environment(L"RM_GPU_SHIM_POLICY_FILE") == expectedPolicy, "broker-child-policy-not-self-configured");
            const bool expectedVulkan = std::wcstoull(argv[2], nullptr, 16) != 0;
            const std::wstring inheritedSearch = argv[7];
            const std::wstring layerDirectory = environment(L"RM_TEST_LAYER_DIRECTORY");
            const bool overrideSearch = environment(L"RM_TEST_OVERRIDE_SEARCH") == L"1";
            Check(environment(L"VK_INSTANCE_LAYERS") == (expectedVulkan ? L"VK_LAYER_RESOURCE_MANAGER_gpu_placement" : L""), "broker-child-only-requested-vulkan-enable");
            Check(environment(L"VK_ADD_LAYER_PATH") == (expectedVulkan && !overrideSearch ? layerDirectory + L";" + inheritedSearch : inheritedSearch) &&
                environment(L"VK_LAYER_PATH") == (overrideSearch ? (expectedVulkan ? layerDirectory + L";" : L"") + inheritedSearch : L""), "broker-child-loader-paths-preserved-on-success-and-fallback");
            Check(environment(L"RM_TEST_VALUE") == L"kept=\u4e2d\u6587", "broker-child-unrelated-unicode-environment");
            const auto emptyValue = environment(L"RM_TEST_EMPTY");
            const DWORD emptyQueryError = GetLastError();
            LPWCH environmentBlock = GetEnvironmentStringsW();
            Check(environmentBlock != nullptr, "broker-child-native-environment-block");
            bool emptyFieldPresent = false;
            for (const wchar_t* field = environmentBlock; *field; field += std::wcslen(field) + 1)
                if (std::wcscmp(field, L"RM_TEST_EMPTY=") == 0) emptyFieldPresent = true;
            FreeEnvironmentStringsW(environmentBlock);
            std::printf("{\"emptyEnvironmentFieldPresent\":%s,\"queryLength\":%llu,\"queryLastError\":%lu}\n",
                emptyFieldPresent ? "true" : "false", static_cast<unsigned long long>(emptyValue.size()), emptyQueryError);
            Check(emptyValue.empty() && emptyFieldPresent, "broker-child-empty-variable-preserved");
            std::vector<wchar_t> cwd(32768);
            Check(GetCurrentDirectoryW(static_cast<DWORD>(cwd.size()), cwd.data()) && std::wstring(cwd.data()) == argv[7], "broker-child-working-directory");
            Check(std::wstring(argv[8]).empty() && std::wstring(argv[9]) == L"two words" &&
                std::wstring(argv[10]) == L"tail with space\\" && std::wstring(argv[11]) == L"quote\"\u4e2d\u6587", "broker-child-original-arguments");
            const bool direct = std::wcscmp(argv[4], L"1") == 0;
            const HMODULE provider = GetModuleHandleW(L"ResourceManager.GpuPlacementShim.dll");
            Check((provider != nullptr) == direct, "broker-child-direct3d-activation-independent");
            Check(environment(L"RM_GPU_SHIM_READY_EVENT").empty(), "broker-child-no-unconsumed-ready-event");
            if (direct)
            {
                using Status = DWORD(WINAPI*)(LPVOID);
                const FARPROC raw = GetProcAddress(provider, "ResourceManagerGpuPlacementGetStatus");
                Status status = nullptr;
                std::memcpy(&status, &raw, sizeof(status));
                Check(status && (status(nullptr) & 7) == 7, "broker-child-direct3d-hooks-ready");
            }
            const unsigned long delay = std::wcstoul(argv[3], nullptr, 10);
            Check(delay <= 7000, "broker-child-delay-bound");
            Sleep(delay);
            if (std::wcscmp(argv[5], L"1") == 0)
            {
                const uint64_t target = std::wcstoull(argv[2], nullptr, 16);
                const HMODULE loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
                Check(loader != nullptr, "broker-child-system-loader");
                const FARPROC raw = GetProcAddress(loader, "vkGetInstanceProcAddr");
                std::memcpy(&gipa, &raw, sizeof(gipa));
                Check(gipa != nullptr, "broker-child-loader-entry");
                OwnedInstance instance;
                Check(Create(false, VK_API_VERSION_1_1, &instance.value) == VK_SUCCESS, "broker-child-no-application-layer-enable");
                const auto physical = Enumerate(instance.value);
                Check(physical.size() == 1 && Identity(instance.value, physical[0]) == target, "broker-child-selected-luid");
                GpuReadback(instance.value, physical[0], CapabilitiesOf(instance.value, physical[0]));
            }
            else
            {
                Check(GetModuleHandleW(L"vulkan-1.dll") == nullptr, "broker-child-never-used-vulkan");
            }
            std::printf("{\"passed\":true,\"mode\":\"broker-child\",\"checks\":%u,\"exitCode\":37,\"delayMs\":%lu}\n", checks, delay);
            return 37;
        }
        const bool environmentActivation = argc == 5 && std::wcscmp(argv[1], L"--environment") == 0;
        if (argc != 3 && !environmentActivation) throw std::runtime_error("expected-layer-directory-and-owned-policy-path");
        const wchar_t* layerDirectory = argv[environmentActivation ? 2 : 1];
        const wchar_t* policyPath = argv[environmentActivation ? 3 : 2];
        Check(SetEnvironmentVariableW(L"VK_ADD_LAYER_PATH", layerDirectory) && SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", policyPath), "child-only-environment");
        if (environmentActivation)
        {
            std::wstring layers = L"VK_LAYER_RESOURCE_MANAGER_gpu_placement";
            const DWORD size = GetEnvironmentVariableW(L"VK_INSTANCE_LAYERS", nullptr, 0);
            if (size)
            {
                std::vector<wchar_t> previous(size);
                if (GetEnvironmentVariableW(L"VK_INSTANCE_LAYERS", previous.data(), size)) layers += L";" + std::wstring(previous.data());
            }
            Check(SetEnvironmentVariableW(L"VK_INSTANCE_LAYERS", layers.c_str()), "per-process-layer-enable");
        }
        const HMODULE loader = LoadLibraryExW(L"vulkan-1.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(loader != nullptr, "system-vulkan-loader");
        FARPROC address = GetProcAddress(loader, "vkGetInstanceProcAddr");
        static_assert(sizeof(address) == sizeof(gipa));
        std::memcpy(&gipa, &address, sizeof(gipa));
        Check(gipa != nullptr, "loader-entrypoint");
        if (environmentActivation)
        {
            wchar_t* end = nullptr;
            const uint64_t target = std::wcstoull(argv[4], &end, 16);
            Check(end && *end == L'\0' && target != 0, "environment-target-parsed");
            WritePolicy(policyPath, target);
            OwnedInstance selected;
            Check(Create(false, VK_API_VERSION_1_1, &selected.value) == VK_SUCCESS, "instance-without-explicit-application-layer-list");
            const auto physical = Enumerate(selected.value);
            Check(physical.size() == 1 && Identity(selected.value, physical[0]) == target, "environment-enabled-exact-luid");
            GpuReadback(selected.value, physical[0], CapabilitiesOf(selected.value, physical[0]));
            std::printf("{\"passed\":true,\"mode\":\"environment\",\"checks\":%u,\"targetLuid\":\"%016llx\"}\n", checks, static_cast<unsigned long long>(target));
            return 0;
        }
        uint32_t count = 0;
        Check(I<PFN_vkEnumerateInstanceLayerProperties>(nullptr, "vkEnumerateInstanceLayerProperties")(&count, nullptr) == VK_SUCCESS, "layer-inventory");
        std::vector<VkLayerProperties> layers(count);
        Check(I<PFN_vkEnumerateInstanceLayerProperties>(nullptr, "vkEnumerateInstanceLayerProperties")(&count, layers.data()) == VK_SUCCESS &&
            std::any_of(layers.begin(), layers.end(), [](const auto& p) { return std::strcmp(p.layerName, LayerName) == 0; }), "our-explicit-layer-discovered");
        WritePolicy(argv[2], 0);
        OwnedInstance baseline, defaultLayer;
        Check(Create(false, VK_API_VERSION_1_1, &baseline.value) == VK_SUCCESS, "baseline-created");
        const auto original = Enumerate(baseline.value);
        Check(Create(true, VK_API_VERSION_1_1, &defaultLayer.value) == VK_SUCCESS, "default-layer-created");
        const auto defaults = Enumerate(defaultLayer.value);
        Check(original.size() == defaults.size(), "default-count-unchanged");
        for (size_t i = 0; i < original.size(); ++i)
            Check(Identity(baseline.value, original[i]) == Identity(defaultLayer.value, defaults[i]), "default-order-and-identity-unchanged");
        {
            OwnedInstance legacy;
            Check(Create(true, VK_API_VERSION_1_0, &legacy.value) == VK_SUCCESS, "default-vulkan10-passthrough");
        }
        uint32_t adapters = 0;
        uint64_t firstTarget = 0;
        for (auto physical : original)
        {
            const uint64_t luid = Identity(baseline.value, physical);
            if (!luid) continue;
            if (!firstTarget) firstTarget = luid;
            ++adapters;
            WritePolicy(argv[2], luid);
            const auto expected = CapabilitiesOf(baseline.value, physical);
            OwnedInstance selected;
            Check(Create(true, VK_API_VERSION_1_1, &selected.value, true) == VK_SUCCESS, "target-instance-created");
            const auto visible = Enumerate(selected.value);
            Check(visible.size() == 1 && Identity(selected.value, visible[0]) == luid, "exact-luid-only");
            VkInstance instance = selected.value;
            uint32_t zero = 0;
            VkPhysicalDevice dummy = VK_NULL_HANDLE;
            Check(IP(vkEnumeratePhysicalDevices)(instance, &zero, &dummy) == VK_INCOMPLETE && zero == 0 && dummy == VK_NULL_HANDLE, "physical-zero-capacity-incomplete");
            Groups(instance, luid, "vkEnumeratePhysicalDeviceGroups");
            Groups(instance, luid, "vkEnumeratePhysicalDeviceGroupsKHR");
            const auto actual = CapabilitiesOf(instance, visible[0]);
            Compare(expected, actual);
            WritePolicy(argv[2], UINT64_MAX);
            Check(Enumerate(instance).empty(), "existing-instance-reads-current-startup-policy");
            GpuReadback(instance, visible[0], actual);
            OwnedInstance missing;
            Check(Create(true, VK_API_VERSION_1_1, &missing.value) == VK_SUCCESS, "missing-target-instance-created");
            Check(Enumerate(missing.value).empty(), "missing-target-never-falls-back");
            uint32_t missingGroups = 99;
            Check(I<PFN_vkEnumeratePhysicalDeviceGroups>(missing.value, "vkEnumeratePhysicalDeviceGroups")(missing.value, &missingGroups, nullptr) == VK_SUCCESS && missingGroups == 0, "missing-target-has-no-group");
            OwnedInstance unsupported;
            const VkResult unsupportedResult = Create(true, VK_API_VERSION_1_0, &unsupported.value);
            std::printf("{\"legacyResult\":%d,\"outputNull\":%s}\n", unsupportedResult, unsupported.value ? "false" : "true");
            if (unsupportedResult != VK_SUCCESS) unsupported.value = VK_NULL_HANDLE;
            Check(unsupportedResult == VK_ERROR_INCOMPATIBLE_DRIVER ||
                (unsupportedResult == VK_SUCCESS && Enumerate(unsupported.value).empty()), "legacy-chain-does-not-bypass-target");
        }
        Check(adapters >= 1, "at-least-one-real-luid-adapter");
        const uint64_t target = firstTarget;
        WritePolicy(argv[2], target);
        std::array<bool, 4> concurrent{};
        std::vector<std::thread> workers;
        for (size_t i = 0; i < concurrent.size(); ++i) workers.emplace_back([&, i] { concurrent[i] = ConcurrentDevice(target); });
        for (auto& worker : workers) worker.join();
        for (bool result : concurrent) Check(result, "concurrent-real-instance-device-queue");
        const auto current = Enumerate(defaultLayer.value);
        Check(current.size() == 1 && Identity(defaultLayer.value, current[0]) == target, "original-default-instance-reads-current-policy");
        WritePolicy(argv[2], 0);
        Check(Enumerate(defaultLayer.value).size() == original.size(), "default-file-restores-original-inventory");
        std::printf("{\"passed\":true,\"checks\":%u,\"adapters\":%u,\"wordsPerReadback\":1024,\"concurrentDevices\":4}\n", checks, adapters);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::printf("{\"passed\":false,\"checks\":%u,\"error\":\"%s\"}\n", checks, error.what());
        return 1;
    }
}
