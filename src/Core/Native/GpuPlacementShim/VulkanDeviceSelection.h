#pragma once
#ifndef VK_NO_PROTOTYPES
#define VK_NO_PROTOTYPES
#endif
#include "GpuPlacementPolicy.h"
#include "GpuPlacementDeviceObservation.h"
#include <vulkan/vulkan_core.h>
#include <vector>

namespace ResourceManagerVulkan
{
struct Selection
{
    bool select = false;
    bool hasTarget = false;
    LUID target{};
};
using ResourceManagerGpuPolicy::ReadCurrentPolicy;
inline Selection ResolveSelection(const ResourceManagerGpuPolicy::GpuShimPolicy& policy)
{
    using namespace ResourceManagerGpuPolicy;
    Selection result;
    result.select = policy.mode != GpuShimPolicyMode::Default;
    if (policy.mode == GpuShimPolicyMode::TargetLuid)
    {
        result.target = policy.targetLuid;
        result.hasTarget = true;
    }
    else if (auto adapter = SelectAdapter(policy))
    {
        DXGI_ADAPTER_DESC1 description{};
        result.hasTarget = SUCCEEDED(adapter->GetDesc1(&description));
        result.target = description.AdapterLuid;
        adapter->Release();
    }
    return result;
}
inline bool Matches(const Selection& selection, PFN_vkGetPhysicalDeviceProperties2 identityProperties, VkPhysicalDevice physical)
{
    if (!selection.hasTarget || !identityProperties) return false;
    VkPhysicalDeviceIDProperties identity{};
    identity.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES;
    VkPhysicalDeviceProperties2 properties{};
    properties.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2;
    properties.pNext = &identity;
    identityProperties(physical, &properties);
    LUID luid{};
    static_assert(sizeof(luid) == VK_LUID_SIZE);
    std::memcpy(&luid, identity.deviceLUID, sizeof(luid));
    return identity.deviceLUIDValid && ResourceManagerGpuPolicy::SameLuid(luid, selection.target);
}

inline void ObserveReturnedDevice(ResourceManagerGpuObservation::Store& deviceObservations,
    PFN_vkGetPhysicalDeviceProperties2 identityProperties, VkPhysicalDevice physical, const VkDeviceCreateInfo* info)
{
    using namespace ResourceManagerGpuObservation;
    // A diagnostic traversal cap never changes the application's creation result.
    constexpr size_t MaximumIdentityChainEntries = 64;
    auto entry = reinterpret_cast<const VkBaseInStructure*>(info->pNext);
    size_t visited = 0;
    while (entry && visited++ < MaximumIdentityChainEntries)
    {
        if (entry->sType == VK_STRUCTURE_TYPE_DEVICE_GROUP_DEVICE_CREATE_INFO)
        {
            const auto group = reinterpret_cast<const VkDeviceGroupDeviceCreateInfo*>(entry);
            if (group->physicalDeviceCount > 1)
            {
                deviceObservations.Publish(Api::Vulkan, Identity::MultipleAdapters);
                return;
            }
            if (group->physicalDeviceCount == 1) physical = group->pPhysicalDevices[0];
            break;
        }
        entry = entry->pNext;
    }
    if ((entry && visited > MaximumIdentityChainEntries) || !identityProperties)
    {
        deviceObservations.Publish(Api::Vulkan, Identity::Unavailable);
        return;
    }
    VkPhysicalDeviceIDProperties identity{};
    identity.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES;
    VkPhysicalDeviceProperties2 properties{};
    properties.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2;
    properties.pNext = &identity;
    identityProperties(physical, &properties);
    LUID luid{};
    std::memcpy(&luid, identity.deviceLUID, sizeof(luid));
    deviceObservations.Publish(Api::Vulkan, identity.deviceLUIDValid ? Identity::Adapter : Identity::Unavailable, luid);
}

inline VkResult EnumeratePhysicalDevices(PFN_vkEnumeratePhysicalDevices enumerate, VkInstance instance,
    uint32_t* count, VkPhysicalDevice* output, const Selection& selection, PFN_vkGetPhysicalDeviceProperties2 identityProperties)
{
    if (!selection.select) return enumerate(instance, count, output);
    try
    {
        uint32_t size = 0;
        VkResult result = enumerate(instance, &size, nullptr);
        if (result != VK_SUCCESS) return result;
        std::vector<VkPhysicalDevice> available(size);
        if (size > 0)
        {
            result = enumerate(instance, &size, available.data());
            if (result != VK_SUCCESS && result != VK_INCOMPLETE) return result;
        }
        uint32_t written = 0, matched = 0;
        for (uint32_t i = 0; i < size; ++i)
        {
            if (!Matches(selection, identityProperties, available[i])) continue;
            ++matched;
            if (output && written < *count) output[written++] = available[i];
        }
        *count = output ? written : matched;
        return output && written < matched ? VK_INCOMPLETE : result;
    }
    catch (const std::bad_alloc&) { return VK_ERROR_OUT_OF_HOST_MEMORY; }
}

inline VkResult EnumerateGroups(PFN_vkEnumeratePhysicalDeviceGroups enumerate, VkInstance instance,
    uint32_t* count, VkPhysicalDeviceGroupProperties* output, const Selection& selection, PFN_vkGetPhysicalDeviceProperties2 identityProperties)
{
    if (!selection.select) return enumerate(instance, count, output);
    try
    {
        uint32_t size = 0;
        VkResult result = enumerate(instance, &size, nullptr);
        if (result != VK_SUCCESS) return result;
        std::vector<VkPhysicalDeviceGroupProperties> groups(size);
        for (auto& group : groups) group.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_GROUP_PROPERTIES;
        if (size > 0)
        {
            result = enumerate(instance, &size, groups.data());
            if (result != VK_SUCCESS && result != VK_INCOMPLETE) return result;
        }
        uint32_t written = 0, matched = 0;
        for (uint32_t i = 0; i < size; ++i)
        {
            auto& group = groups[i];
            uint32_t visible = 0;
            for (uint32_t j = 0; j < group.physicalDeviceCount; ++j)
                if (Matches(selection, identityProperties, group.physicalDevices[j])) group.physicalDevices[visible++] = group.physicalDevices[j];
            if (visible == 0) continue;
            ++matched;
            if (output && written < *count)
            {
                // Only the visible subset changes; caller-owned sType/pNext remain intact.
                output[written].physicalDeviceCount = visible;
                std::copy_n(group.physicalDevices, visible, output[written].physicalDevices);
                output[written].subsetAllocation = visible == 1 ? VK_FALSE : group.subsetAllocation;
                ++written;
            }
        }
        *count = output ? written : matched;
        return output && written < matched ? VK_INCOMPLETE : result;
    }
    catch (const std::bad_alloc&) { return VK_ERROR_OUT_OF_HOST_MEMORY; }
}
}
