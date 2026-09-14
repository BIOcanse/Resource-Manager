#include "../GpuPlacementShim/VulkanPlacementLayer.cpp"
#include <cstdio>
#include <stdexcept>

namespace
{
unsigned checks = 0;
void Check(bool value, const char* name)
{
    ++checks;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", name, value ? "true" : "false");
    if (!value) throw std::runtime_error(name);
}
void VKAPI_CALL IdentityAvailable(VkPhysicalDevice, VkPhysicalDeviceProperties2*) { }
void PolicyFile(const wchar_t* path, bool active)
{
    FILE* file = _wfopen(path, L"wb");
    if (!file) throw std::runtime_error("fixture-policy-file");
    std::fputs(active ? "mode=targetLuid\ntargetLuid=0x00000000_0x00000001\n" : "mode=default\n", file);
    if (std::fclose(file) != 0) throw std::runtime_error("fixture-policy-close");
}
const wchar_t* racePolicy = nullptr;
bool raceDestroyed = false;
void* raceDispatch = reinterpret_cast<void*>(3);
VkResult VKAPI_CALL RacingCreate(const VkInstanceCreateInfo*, const VkAllocationCallbacks*, VkInstance* output)
{
    PolicyFile(racePolicy, true);
    Check(ResourceManagerGpuPlacementConfigure(const_cast<wchar_t*>(racePolicy)) == VulkanRuntimeReadyStatus,
        "configure-between-native-create-and-registration");
    *output = reinterpret_cast<VkInstance>(&raceDispatch);
    return VK_SUCCESS;
}
void VKAPI_CALL RacingDestroy(VkInstance, const VkAllocationCallbacks*) { raceDestroyed = true; }
PFN_vkVoidFunction VKAPI_CALL RacingProc(VkInstance, const char* name)
{
    if (std::strcmp(name, "vkCreateInstance") == 0) return reinterpret_cast<PFN_vkVoidFunction>(&RacingCreate);
    if (std::strcmp(name, "vkDestroyInstance") == 0) return reinterpret_cast<PFN_vkVoidFunction>(&RacingDestroy);
    return nullptr;
}
}
int wmain(int argc, wchar_t** argv)
{
    try
    {
        Check(argc == 2, "owned-policy-path");
        PolicyFile(argv[1], true);
        Check(ResourceManagerGpuPlacementConfigure(argv[1]) == 0 && runtimePolicyPath.empty(),
            "no-connected-instance-rejected");
        InstanceState legacy{};
        instances.emplace(reinterpret_cast<void*>(1), legacy);
        Check(ResourceManagerGpuPlacementConfigure(argv[1]) == 0 && runtimePolicyPath.empty(),
            "missing-identity-rejected-without-update");
        instances.at(reinterpret_cast<void*>(1)).identityProperties = IdentityAvailable;
        Check(ResourceManagerGpuPlacementConfigure(argv[1]) == VulkanRuntimeReadyStatus &&
            runtimePolicyPath == argv[1] && ReadCurrentSelection().select && ReadCurrentSelection().target.LowPart == 1,
            "available-identity-accepts-exact-selection");
        instances.emplace(reinterpret_cast<void*>(2), legacy);
        const auto prior = runtimePolicyPath;
        Check(ResourceManagerGpuPlacementConfigure(argv[1]) == 0 && runtimePolicyPath == prior,
            "mixed-instances-reject-without-partial-update");
        PolicyFile(argv[1], false);
        Check(ResourceManagerGpuPlacementConfigure(argv[1]) == VulkanRuntimeReadyStatus && !ReadCurrentSelection().select,
            "default-remains-available-with-missing-identity");
        instances.erase(reinterpret_cast<void*>(2));
        racePolicy = argv[1];
        VkLayerInstanceLink link{nullptr, RacingProc, nullptr};
        VkLayerInstanceCreateInfo chain{};
        chain.sType = VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO;
        chain.function = VK_LAYER_LINK_INFO;
        chain.u.pLayerInfo = &link;
        VkApplicationInfo application{};
        application.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
        application.apiVersion = VK_API_VERSION_1_0;
        VkInstanceCreateInfo create{};
        create.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
        create.pApplicationInfo = &application;
        create.pNext = &chain;
        VkInstance output{};
        const auto created = vkCreateInstance(&create, nullptr, &output);
        Check(created == VK_ERROR_INCOMPATIBLE_DRIVER && !output,
            "new-instance-admission-uses-configuration-at-registration");
        Check(raceDestroyed && instances.count(raceDispatch) == 0,
            "rejected-downstream-instance-destroyed-and-not-registered");
        instances.clear();
        Check(ResourceManagerGpuPlacementGetStatus(nullptr) == 0, "destroyed-instances-not-ready");
        std::printf("{\"passed\":true,\"checks\":%u,\"fixtureOnly\":true,\"gpuCreated\":false}\n", checks);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::printf("{\"passed\":false,\"error\":\"%s\"}\n", error.what());
        return 1;
    }
}
