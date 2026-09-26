#define WIN32_LEAN_AND_MEAN
#define VK_NO_PROTOTYPES
#include "VulkanDeviceSelection.h"
#include "GpuPlacementApiObservation.h"
#include <vulkan/vk_layer.h>
#include <memory>
#include <mutex>
#include <vector>
#include <unordered_map>

#define LAYER_EXPORT extern "C" __declspec(dllexport)

namespace
{
constexpr char LayerName[] = "VK_LAYER_RESOURCE_MANAGER_gpu_placement";
// Configured, initialized, connected call chain, and Vulkan runtime selection.
constexpr DWORD VulkanRuntimeReadyStatus = 1u | 2u | 4u | 16u;
using ResourceManagerVulkan::Selection;
using ResourceManagerVulkan::ResolveSelection;
struct InstanceState
{
    VkInstance instance;
    PFN_vkGetInstanceProcAddr next;
    PFN_GetPhysicalDeviceProcAddr nextPhysical;
    PFN_vkGetPhysicalDeviceProperties2 identityProperties = nullptr;
};
struct DeviceState
{
    PFN_vkGetDeviceProcAddr next;
};
std::mutex stateMutex;
std::unordered_map<void*, InstanceState> instances;
std::unordered_map<void*, DeviceState> devices;
std::wstring runtimePolicyPath;
ResourceManagerGpuObservation::Store deviceObservations;
ResourceManagerGpuObservation::CallStore apiObservations;

// Physical devices share their instance dispatch key; devices/queues share a device key.
template <typename T> void* Key(T handle) { return *reinterpret_cast<void**>(handle); }
template <typename T> InstanceState Instance(T handle)
{
    std::lock_guard<std::mutex> lock(stateMutex);
    return instances.at(Key(handle));
}
DeviceState Device(VkDevice device)
{
    std::lock_guard<std::mutex> lock(stateMutex);
    return devices.at(Key(device));
}
template <typename T> T Proc(const InstanceState& state, const char* name)
{
    return reinterpret_cast<T>(state.next(state.instance, name));
}
Selection ReadCurrentSelection()
{
    std::wstring path;
    {
        std::lock_guard<std::mutex> lock(stateMutex);
        path = runtimePolicyPath;
    }
    return ResolveSelection(ResourceManagerVulkan::ReadCurrentPolicy(path));
}
}

LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance, const char*);
LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vkGetDeviceProcAddr(VkDevice, const char*);
LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vk_layerGetPhysicalDeviceProcAddr(VkInstance, const char*);

LAYER_EXPORT VkResult VKAPI_CALL vkCreateInstance(const VkInstanceCreateInfo* info,
    const VkAllocationCallbacks* allocator, VkInstance* output)
{
    apiObservations.Record(ResourceManagerGpuObservation::CallApi::Vulkan);
    try
    {
        const auto selection = ReadCurrentSelection();
        const uint32_t api = info->pApplicationInfo ? info->pApplicationInfo->apiVersion : 0;
        if (selection.select && api < VK_API_VERSION_1_1) return VK_ERROR_INCOMPATIBLE_DRIVER;
        auto link = reinterpret_cast<const VkLayerInstanceCreateInfo*>(info->pNext);
        while (link && (link->sType != VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO || link->function != VK_LAYER_LINK_INFO))
            link = reinterpret_cast<const VkLayerInstanceCreateInfo*>(link->pNext);
        if (!link || !link->u.pLayerInfo) return VK_ERROR_INITIALIZATION_FAILED;
        const auto next = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
        const auto nextPhysical = link->u.pLayerInfo->pfnNextGetPhysicalDeviceProcAddr;
        const auto create = reinterpret_cast<PFN_vkCreateInstance>(next(nullptr, "vkCreateInstance"));
        if (!create) return VK_ERROR_INITIALIZATION_FAILED;
        const_cast<VkLayerInstanceCreateInfo*>(link)->u.pLayerInfo = link->u.pLayerInfo->pNext;
        const VkResult result = create(info, allocator, output);
        if (result != VK_SUCCESS) return result;
        InstanceState state{*output, next, nextPhysical};
        try
        {
            if (api >= VK_API_VERSION_1_1)
                state.identityProperties = Proc<PFN_vkGetPhysicalDeviceProperties2>(state, "vkGetPhysicalDeviceProperties2");
            std::lock_guard<std::mutex> lock(stateMutex);
            if (!state.identityProperties && ResolveSelection(ResourceManagerVulkan::ReadCurrentPolicy(runtimePolicyPath)).select)
                throw VK_ERROR_INCOMPATIBLE_DRIVER;
            instances.emplace(Key(*output), state);
        }
        catch (...)
        {
            Proc<PFN_vkDestroyInstance>(state, "vkDestroyInstance")(*output, allocator);
            *output = VK_NULL_HANDLE;
            throw;
        }
        return VK_SUCCESS;
    }
    catch (VkResult error) { return error; }
    catch (const std::bad_alloc&) { return VK_ERROR_OUT_OF_HOST_MEMORY; }
}

LAYER_EXPORT void VKAPI_CALL vkDestroyInstance(VkInstance instance, const VkAllocationCallbacks* allocator)
{
    if (!instance) return;
    apiObservations.Record(ResourceManagerGpuObservation::CallApi::Vulkan);
    const auto state = Instance(instance);
    {
        std::lock_guard<std::mutex> lock(stateMutex);
        instances.erase(Key(instance));
    }
    Proc<PFN_vkDestroyInstance>(state, "vkDestroyInstance")(instance, allocator);
}

LAYER_EXPORT VkResult VKAPI_CALL vkEnumeratePhysicalDevices(VkInstance instance, uint32_t* count, VkPhysicalDevice* output)
{
    apiObservations.Record(ResourceManagerGpuObservation::CallApi::Vulkan);
    try
    {
        const auto state = Instance(instance);
        return ResourceManagerVulkan::EnumeratePhysicalDevices(
            Proc<PFN_vkEnumeratePhysicalDevices>(state, "vkEnumeratePhysicalDevices"), instance,
            count, output, ReadCurrentSelection(), state.identityProperties);
    }
    catch (const std::bad_alloc&) { return VK_ERROR_OUT_OF_HOST_MEMORY; }
}

namespace
{
VkResult EnumerateGroups(VkInstance instance, uint32_t* count, VkPhysicalDeviceGroupProperties* output, const char* name)
{
    try
    {
        const auto state = Instance(instance);
        return ResourceManagerVulkan::EnumerateGroups(
            Proc<PFN_vkEnumeratePhysicalDeviceGroups>(state, name), instance,
            count, output, ReadCurrentSelection(), state.identityProperties);
    }
    catch (const std::bad_alloc&) { return VK_ERROR_OUT_OF_HOST_MEMORY; }
}
}
LAYER_EXPORT VkResult VKAPI_CALL vkEnumeratePhysicalDeviceGroups(VkInstance instance, uint32_t* count, VkPhysicalDeviceGroupProperties* output)
{
    apiObservations.Record(ResourceManagerGpuObservation::CallApi::Vulkan);
    return EnumerateGroups(instance, count, output, "vkEnumeratePhysicalDeviceGroups");
}
LAYER_EXPORT VkResult VKAPI_CALL vkEnumeratePhysicalDeviceGroupsKHR(VkInstance instance, uint32_t* count, VkPhysicalDeviceGroupProperties* output)
{
    apiObservations.Record(ResourceManagerGpuObservation::CallApi::Vulkan);
    return EnumerateGroups(instance, count, output, "vkEnumeratePhysicalDeviceGroupsKHR");
}

LAYER_EXPORT VkResult VKAPI_CALL vkCreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* info,
    const VkAllocationCallbacks* allocator, VkDevice* output)
{
    apiObservations.Record(ResourceManagerGpuObservation::CallApi::Vulkan);
    const auto state = Instance(physical);
    auto link = reinterpret_cast<const VkLayerDeviceCreateInfo*>(info->pNext);
    while (link && (link->sType != VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO || link->function != VK_LAYER_LINK_INFO))
        link = reinterpret_cast<const VkLayerDeviceCreateInfo*>(link->pNext);
    if (!link || !link->u.pLayerInfo) return VK_ERROR_INITIALIZATION_FAILED;
    const auto next = link->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    const auto create = reinterpret_cast<PFN_vkCreateDevice>(link->u.pLayerInfo->pfnNextGetInstanceProcAddr(state.instance, "vkCreateDevice"));
    const_cast<VkLayerDeviceCreateInfo*>(link)->u.pLayerInfo = link->u.pLayerInfo->pNext;
    // No device, queue, feature, extension, allocator or pNext substitution.
    const VkResult result = create(physical, info, allocator, output);
    if (result != VK_SUCCESS) return result;
    try
    {
        std::lock_guard<std::mutex> lock(stateMutex);
        devices.emplace(Key(*output), DeviceState{next});
    }
    catch (const std::bad_alloc&)
    {
        reinterpret_cast<PFN_vkDestroyDevice>(next(*output, "vkDestroyDevice"))(*output, allocator);
        *output = VK_NULL_HANDLE;
        return VK_ERROR_OUT_OF_HOST_MEMORY;
    }
    ResourceManagerVulkan::ObserveReturnedDevice(deviceObservations, state.identityProperties, physical, info);
    return VK_SUCCESS;
}
LAYER_EXPORT void VKAPI_CALL vkDestroyDevice(VkDevice device, const VkAllocationCallbacks* allocator)
{
    if (!device) return;
    apiObservations.Record(ResourceManagerGpuObservation::CallApi::Vulkan);
    const auto state = Device(device);
    {
        std::lock_guard<std::mutex> lock(stateMutex);
        devices.erase(Key(device));
    }
    reinterpret_cast<PFN_vkDestroyDevice>(state.next(device, "vkDestroyDevice"))(device, allocator);
}

LAYER_EXPORT DWORD WINAPI ResourceManagerGpuPlacementReadDeviceObservations(LPVOID parameter)
{
    return deviceObservations.CopyTo(parameter);
}

LAYER_EXPORT DWORD WINAPI ResourceManagerGpuPlacementStartApiObservation(LPVOID parameter)
{
    using namespace ResourceManagerGpuObservation;
    const auto request = static_cast<const CallObservationRequest*>(parameter);
    DWORD error = ERROR_INVALID_PARAMETER;
    if (request && request->byteSize == sizeof(CallObservationRequest) && request->version == 1 &&
        request->apis && request->durationMilliseconds)
    {
        if (request->apis != static_cast<uint32_t>(CallApi::Vulkan)) error = ERROR_NOT_SUPPORTED;
        else
        {
            std::lock_guard<std::mutex> lock(stateMutex);
            error = runtimePolicyPath.empty() ? apiObservations.Start(request->apis, request->durationMilliseconds) : ERROR_INVALID_STATE;
        }
    }
    SetLastError(error);
    return error == ERROR_SUCCESS ? 1 : 0;
}

LAYER_EXPORT DWORD WINAPI ResourceManagerGpuPlacementReadApiObservation(LPVOID parameter)
{
    const auto error = apiObservations.CopyTo(parameter, false);
    SetLastError(error);
    return error == ERROR_SUCCESS ? 1 : 0;
}

LAYER_EXPORT DWORD WINAPI ResourceManagerGpuPlacementStopApiObservation(LPVOID parameter)
{
    const auto error = apiObservations.CopyTo(parameter, true);
    SetLastError(error);
    return error == ERROR_SUCCESS ? 1 : 0;
}

LAYER_EXPORT DWORD WINAPI ResourceManagerGpuPlacementGetStatus(LPVOID)
{
    std::lock_guard<std::mutex> lock(stateMutex);
    return !runtimePolicyPath.empty() && !instances.empty() ? VulkanRuntimeReadyStatus : 0;
}

LAYER_EXPORT DWORD WINAPI ResourceManagerGpuPlacementConfigure(LPVOID parameter)
{
    const auto path = static_cast<const wchar_t*>(parameter);
    if (!path || !path[0]) return ResourceManagerGpuPlacementGetStatus(nullptr);
    try
    {
        std::wstring configuredPath(path);
        const auto selection = ResolveSelection(ResourceManagerVulkan::ReadCurrentPolicy(configuredPath));
        std::lock_guard<std::mutex> lock(stateMutex);
        if (instances.empty()) return 0;
        if (selection.select && std::any_of(instances.begin(), instances.end(),
            [](const auto& item) { return !item.second.identityProperties; })) return 0;
        apiObservations.Stop();
        runtimePolicyPath = std::move(configuredPath);
        return VulkanRuntimeReadyStatus;
    }
    catch (const std::bad_alloc&) { return 0; }
}

LAYER_EXPORT VkResult VKAPI_CALL vkEnumerateInstanceLayerProperties(uint32_t* count, VkLayerProperties* output)
{
    if (!output) { *count = 1; return VK_SUCCESS; }
    if (*count == 0) return VK_INCOMPLETE;
    *count = 1;
    VkLayerProperties properties{};
    std::strcpy(properties.layerName, LayerName);
    std::strcpy(properties.description, "Resource Manager per-instance GPU placement");
    properties.specVersion = VK_API_VERSION_1_4;
    properties.implementationVersion = 1;
    *output = properties;
    return VK_SUCCESS;
}
LAYER_EXPORT VkResult VKAPI_CALL vkEnumerateInstanceExtensionProperties(const char* layer, uint32_t* count, VkExtensionProperties*)
{
    if (!layer || std::strcmp(layer, LayerName) != 0) return VK_ERROR_LAYER_NOT_PRESENT;
    *count = 0;
    return VK_SUCCESS;
}
LAYER_EXPORT VkResult VKAPI_CALL vkEnumerateDeviceExtensionProperties(VkPhysicalDevice physical,
    const char* layer, uint32_t* count, VkExtensionProperties* output)
{
    if (layer && std::strcmp(layer, LayerName) == 0) { *count = 0; return VK_SUCCESS; }
    return Proc<PFN_vkEnumerateDeviceExtensionProperties>(Instance(physical), "vkEnumerateDeviceExtensionProperties")(physical, layer, count, output);
}

LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance instance, const char* name)
{
    if (!name) return nullptr;
#define GLOBAL_PROC(fn) if (std::strcmp(name, #fn) == 0) return reinterpret_cast<PFN_vkVoidFunction>(fn)
    GLOBAL_PROC(vkGetInstanceProcAddr);
    GLOBAL_PROC(vkCreateInstance);
    GLOBAL_PROC(vkEnumerateInstanceLayerProperties);
    GLOBAL_PROC(vkEnumerateInstanceExtensionProperties);
    if (!instance) return nullptr;
    const auto state = Instance(instance);
    const auto next = state.next(instance, name);
    // Extension/core aliases remain unavailable when the next implementation disables them.
#define INSTANCE_PROC(fn) if (std::strcmp(name, #fn) == 0) return next ? reinterpret_cast<PFN_vkVoidFunction>(fn) : nullptr
    GLOBAL_PROC(vk_layerGetPhysicalDeviceProcAddr);
    INSTANCE_PROC(vkGetDeviceProcAddr);
    INSTANCE_PROC(vkDestroyInstance);
    INSTANCE_PROC(vkEnumeratePhysicalDevices);
    INSTANCE_PROC(vkEnumeratePhysicalDeviceGroups);
    INSTANCE_PROC(vkEnumeratePhysicalDeviceGroupsKHR);
    INSTANCE_PROC(vkEnumerateDeviceExtensionProperties);
    INSTANCE_PROC(vkCreateDevice);
    INSTANCE_PROC(vkDestroyDevice);
#undef INSTANCE_PROC
#undef GLOBAL_PROC
    return next;
}
LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vkGetDeviceProcAddr(VkDevice device, const char* name)
{
    if (!device || !name) return nullptr;
    const auto next = Device(device).next(device, name);
    if (!next) return nullptr;
    if (std::strcmp(name, "vkGetDeviceProcAddr") == 0) return reinterpret_cast<PFN_vkVoidFunction>(vkGetDeviceProcAddr);
    if (std::strcmp(name, "vkDestroyDevice") == 0) return reinterpret_cast<PFN_vkVoidFunction>(vkDestroyDevice);
    return next;
}
LAYER_EXPORT PFN_vkVoidFunction VKAPI_CALL vk_layerGetPhysicalDeviceProcAddr(VkInstance instance, const char* name)
{
    if (!instance || !name) return nullptr;
    const auto state = Instance(instance);
    if (std::strcmp(name, "vkEnumerateDeviceExtensionProperties") == 0)
        return reinterpret_cast<PFN_vkVoidFunction>(vkEnumerateDeviceExtensionProperties);
    return state.nextPhysical ? state.nextPhysical(instance, name) : nullptr;
}
LAYER_EXPORT VkResult VKAPI_CALL vkNegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface* version)
{
    if (!version || version->sType != LAYER_NEGOTIATE_INTERFACE_STRUCT || version->loaderLayerInterfaceVersion < 2)
        return VK_ERROR_INITIALIZATION_FAILED;
    version->loaderLayerInterfaceVersion = 2;
    version->pfnGetInstanceProcAddr = vkGetInstanceProcAddr;
    version->pfnGetDeviceProcAddr = vkGetDeviceProcAddr;
    version->pfnGetPhysicalDeviceProcAddr = vk_layerGetPhysicalDeviceProcAddr;
    return VK_SUCCESS;
}
