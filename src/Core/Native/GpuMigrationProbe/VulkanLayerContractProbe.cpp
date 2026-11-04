#include <cstdlib>
#include <new>
bool failNextAllocation = false;
__declspec(noinline) void* operator new(size_t size)
{
    if (failNextAllocation) { failNextAllocation = false; throw std::bad_alloc(); }
    if (auto memory = std::malloc(size)) return memory;
    throw std::bad_alloc();
}
__declspec(noinline) void operator delete(void* memory) noexcept { std::free(memory); }
__declspec(noinline) void operator delete(void* memory, size_t) noexcept { std::free(memory); }
#include "../GpuPlacementShim/VulkanPlacementLayer.cpp"
#include <cstdio>
#include <stdexcept>
#include <array>

namespace
{
unsigned checks = 0, failures = 0, createCalls = 0, destroyCalls = 0, enumCalls = 0;
bool slotPreserved = false, chainAdvanced = false;
bool deviceArgumentsPreserved = false;
unsigned deviceCreateCalls = 0;
unsigned deviceDestroyCalls = 0, identityQueries = 0;
bool deviceSuccess = false, failDeviceRegistration = false;
VkResult enumerateResult = VK_SUCCESS;
int instanceKey, deviceKey;
struct FakeHandle { void* key; } instanceHandle{&instanceKey}, deviceHandle{&deviceKey};
struct PhysicalHandle { void* key; uint64_t luid; VkBool32 valid; } physicalHandles[] = {
    {&instanceKey, 0x1234, VK_TRUE}, {&instanceKey, 0x5678, VK_TRUE}, {&instanceKey, 0x1234, VK_FALSE}
};
VkInstance fakeInstance = reinterpret_cast<VkInstance>(&instanceHandle);
VkDevice fakeDevice = reinterpret_cast<VkDevice>(&deviceHandle);
VkPhysicalDevice Physical(size_t index) { return reinterpret_cast<VkPhysicalDevice>(&physicalHandles[index]); }
void Check(bool value, const char* name)
{
    ++checks;
    if (!value) ++failures;
    std::printf("{\"check\":\"%s\",\"passed\":%s}\n", name, value ? "true" : "false");
}
VkResult VKAPI_CALL NextCreate(const VkInstanceCreateInfo* info, const VkAllocationCallbacks*, VkInstance* output)
{
    ++createCalls;
    slotPreserved = *output == fakeInstance;
    auto link = reinterpret_cast<const VkLayerInstanceCreateInfo*>(info->pNext);
    chainAdvanced = link->u.pLayerInfo == nullptr;
    *output = fakeInstance;
    return VK_SUCCESS;
}
void VKAPI_CALL NextDestroy(VkInstance, const VkAllocationCallbacks*) { ++destroyCalls; }
VkResult VKAPI_CALL NextEnumerate(VkInstance, uint32_t* count, VkPhysicalDevice* output)
{
    ++enumCalls;
    if (enumerateResult < 0) return enumerateResult;
    if (!output) { *count = 3; return VK_SUCCESS; }
    const uint32_t written = std::min(*count, 3u);
    for (uint32_t i = 0; i < written; ++i) output[i] = Physical(i);
    *count = written;
    return written < 3 ? VK_INCOMPLETE : enumerateResult;
}
void VKAPI_CALL NextProperties(VkPhysicalDevice physical, VkPhysicalDeviceProperties2* properties)
{
    ++identityQueries;
    auto* identity = static_cast<VkPhysicalDeviceIDProperties*>(properties->pNext);
    auto* handle = reinterpret_cast<PhysicalHandle*>(physical);
    std::memcpy(identity->deviceLUID, &handle->luid, sizeof(handle->luid));
    identity->deviceLUIDValid = handle->valid;
}
VkResult VKAPI_CALL NextGroups(VkInstance, uint32_t* count, VkPhysicalDeviceGroupProperties* output)
{
    if (!output) { *count = 2; return VK_SUCCESS; }
    const uint32_t written = std::min(*count, 2u);
    if (written > 0)
    {
        output[0].physicalDeviceCount = 2;
        output[0].physicalDevices[0] = Physical(0);
        output[0].physicalDevices[1] = Physical(1);
        output[0].subsetAllocation = VK_TRUE;
    }
    if (written > 1)
    {
        output[1].physicalDeviceCount = 1;
        output[1].physicalDevices[0] = Physical(2);
        output[1].subsetAllocation = VK_TRUE;
    }
    *count = written;
    return written < 2 ? VK_INCOMPLETE : VK_SUCCESS;
}
void VKAPI_CALL NextUnknown() {}
VkResult VKAPI_CALL NextCreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* info,
    const VkAllocationCallbacks*, VkDevice* output)
{
    ++deviceCreateCalls;
    const auto link = static_cast<const VkLayerDeviceCreateInfo*>(info->pNext);
    deviceArgumentsPreserved = physical == Physical(1) && link->u.pLayerInfo == nullptr &&
        info->pEnabledFeatures && info->pEnabledFeatures->shaderInt64 == VK_TRUE && info->queueCreateInfoCount == 1 &&
        info->pQueueCreateInfos[0].queueFamilyIndex == 5 && info->pQueueCreateInfos[0].pQueuePriorities[0] == 0.5f &&
        info->enabledExtensionCount == 1 && std::strcmp(info->ppEnabledExtensionNames[0], "VK_RM_fixture_extension") == 0;
    if (!deviceSuccess) return VK_ERROR_FEATURE_NOT_PRESENT;
    *output = fakeDevice;
    failNextAllocation = failDeviceRegistration;
    return VK_SUCCESS;
}
void VKAPI_CALL NextDestroyDevice(VkDevice, const VkAllocationCallbacks*) { ++deviceDestroyCalls; }
PFN_vkVoidFunction VKAPI_CALL NextDevice(VkDevice, const char* name)
{
    if (std::strcmp(name, "vkDestroyDevice") == 0) return reinterpret_cast<PFN_vkVoidFunction>(NextDestroyDevice);
    if (std::strcmp(name, "vkRmTestDeviceExtension") == 0) return NextUnknown;
    return nullptr;
}
PFN_vkVoidFunction VKAPI_CALL NextPhysical(VkInstance, const char* name)
{
    if (std::strcmp(name, "vkRmTestPhysicalExtension") == 0) return NextUnknown;
    return nullptr;
}
PFN_vkVoidFunction VKAPI_CALL NextInstance(VkInstance, const char* name)
{
#define NEXT(n, f) if (std::strcmp(name, n) == 0) return reinterpret_cast<PFN_vkVoidFunction>(f)
    NEXT("vkCreateInstance", NextCreate);
    NEXT("vkDestroyInstance", NextDestroy);
    NEXT("vkEnumeratePhysicalDevices", NextEnumerate);
    NEXT("vkGetPhysicalDeviceProperties2", NextProperties);
    NEXT("vkEnumeratePhysicalDeviceGroups", NextGroups);
    NEXT("vkGetDeviceProcAddr", NextDevice);
    NEXT("vkCreateDevice", NextCreateDevice);
    NEXT("vkRmTestInstanceExtension", NextUnknown);
#undef NEXT
    return nullptr;
}
VkResult CreateFake(uint32_t api)
{
    VkLayerInstanceLink next{};
    next.pfnNextGetInstanceProcAddr = NextInstance;
    next.pfnNextGetPhysicalDeviceProcAddr = NextPhysical;
    VkLayerInstanceCreateInfo chain{};
    chain.sType = VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO;
    chain.function = VK_LAYER_LINK_INFO;
    chain.u.pLayerInfo = &next;
    VkApplicationInfo app{};
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.apiVersion = api;
    VkInstanceCreateInfo info{};
    info.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    info.pApplicationInfo = &app;
    info.pNext = &chain;
    VkInstance output = fakeInstance;
    return vkCreateInstance(&info, nullptr, &output);
}
void PolicyFile(const wchar_t* path, bool target)
{
    FILE* file = _wfopen(path, L"wb");
    if (!file) throw std::runtime_error("policy-open");
    std::fputs(target ? "version=1\nmode=targetLuid\ntargetLuid=0x00000000_0x00001234\n" : "mode=default\n", file);
    if (std::fclose(file) != 0) throw std::runtime_error("policy-close");
}
}
int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) return 2;
    if (!SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", argv[1])) return 2;
    VkNegotiateLayerInterface negotiation{};
    negotiation.sType = LAYER_NEGOTIATE_INTERFACE_STRUCT;
    negotiation.loaderLayerInterfaceVersion = 1;
    Check(vkNegotiateLoaderLayerInterfaceVersion(&negotiation) == VK_ERROR_INITIALIZATION_FAILED, "old-loader-interface-rejected");
    negotiation.loaderLayerInterfaceVersion = 99;
    Check(vkNegotiateLoaderLayerInterfaceVersion(&negotiation) == VK_SUCCESS && negotiation.loaderLayerInterfaceVersion == 2 &&
        negotiation.pfnGetInstanceProcAddr == vkGetInstanceProcAddr && negotiation.pfnGetDeviceProcAddr == vkGetDeviceProcAddr &&
        negotiation.pfnGetPhysicalDeviceProcAddr == vk_layerGetPhysicalDeviceProcAddr, "version-two-negotiated");
    PolicyFile(argv[1], true);
    Check(CreateFake(VK_API_VERSION_1_0) == VK_ERROR_INCOMPATIBLE_DRIVER && createCalls == 0, "exact-entry-vulkan10-rejected-before-next");
    Check(CreateFake(VK_API_VERSION_1_1) == VK_SUCCESS, "fake-target-instance-created");
    Check(slotPreserved, "loader-prefilled-instance-slot-preserved");
    Check(chainAdvanced && createCalls == 1, "next-link-advanced-exactly-once");
    Check(vkGetInstanceProcAddr(fakeInstance, "vkRmTestInstanceExtension") == NextUnknown, "unknown-instance-dispatch-forwarded");
    Check(vkGetInstanceProcAddr(fakeInstance, "vkEnumeratePhysicalDeviceGroupsKHR") == nullptr, "disabled-group-alias-not-invented");
    Check(vk_layerGetPhysicalDeviceProcAddr(fakeInstance, "vkRmTestPhysicalExtension") == NextUnknown &&
        vk_layerGetPhysicalDeviceProcAddr(fakeInstance, "vkCreateInstance") == nullptr, "physical-extension-dispatch-preserved");
    uint32_t count = 0;
    Check(vkEnumeratePhysicalDevices(fakeInstance, &count, nullptr) == VK_SUCCESS && count == 1, "target-filters-other-and-invalid-luid");
    VkPhysicalDevice output[3]{};
    count = 0;
    Check(vkEnumeratePhysicalDevices(fakeInstance, &count, output) == VK_INCOMPLETE && count == 0 && output[0] == VK_NULL_HANDLE, "zero-capacity-not-written");
    count = 3;
    Check(vkEnumeratePhysicalDevices(fakeInstance, &count, output) == VK_SUCCESS && count == 1 && output[0] == Physical(0) && output[1] == VK_NULL_HANDLE, "original-handle-and-tail-preserved");
    enumerateResult = VK_ERROR_OUT_OF_HOST_MEMORY;
    count = 73;
    Check(vkEnumeratePhysicalDevices(fakeInstance, &count, output) == VK_ERROR_OUT_OF_HOST_MEMORY && count == 73, "original-enumeration-error-preserved");
    enumerateResult = VK_INCOMPLETE;
    count = 3;
    Check(vkEnumeratePhysicalDevices(fakeInstance, &count, output) == VK_INCOMPLETE && count == 1, "upstream-incomplete-not-hidden");
    enumerateResult = VK_SUCCESS;
    count = 0;
    Check(vkEnumeratePhysicalDeviceGroups(fakeInstance, &count, nullptr) == VK_SUCCESS && count == 1, "visible-group-count");
    VkPhysicalDeviceGroupProperties groups[2]{};
    groups[0].sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_GROUP_PROPERTIES;
    groups[1].physicalDeviceCount = 88;
    count = 2;
    Check(vkEnumeratePhysicalDeviceGroups(fakeInstance, &count, groups) == VK_SUCCESS && count == 1 &&
        groups[0].physicalDeviceCount == 1 && groups[0].physicalDevices[0] == Physical(0) && groups[0].subsetAllocation == VK_FALSE &&
        groups[1].physicalDeviceCount == 88, "group-visible-subset-real-flags-and-unwritten-tail");
    PolicyFile(argv[1], false);
    count = 0;
    Check(vkEnumeratePhysicalDevices(fakeInstance, &count, nullptr) == VK_SUCCESS && count == 3, "held-instance-follows-current-default-policy");
    vkDestroyInstance(fakeInstance, nullptr);
    Check(instances.empty() && destroyCalls == 1, "instance-state-released-on-normal-destroy");
    Check(CreateFake(VK_API_VERSION_1_0) == VK_SUCCESS, "default-entry-vulkan10-passthrough");
    const unsigned before = enumCalls;
    count = 2;
    Check(vkEnumeratePhysicalDevices(fakeInstance, &count, output) == VK_INCOMPLETE && count == 2 && enumCalls == before + 1 &&
        output[0] == Physical(0) && output[1] == Physical(1), "default-original-call-and-order");
    VkLayerDeviceLink deviceLink{};
    deviceLink.pfnNextGetInstanceProcAddr = NextInstance;
    deviceLink.pfnNextGetDeviceProcAddr = NextDevice;
    VkLayerDeviceCreateInfo deviceChain{};
    deviceChain.sType = VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO;
    deviceChain.function = VK_LAYER_LINK_INFO;
    deviceChain.u.pLayerInfo = &deviceLink;
    float priority = 0.5f;
    VkDeviceQueueCreateInfo queue{};
    queue.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    queue.queueFamilyIndex = 5;
    queue.queueCount = 1;
    queue.pQueuePriorities = &priority;
    VkPhysicalDeviceFeatures features{};
    features.shaderInt64 = VK_TRUE;
    const char* extension = "VK_RM_fixture_extension";
    VkDeviceCreateInfo deviceInfo{};
    deviceInfo.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    deviceInfo.pNext = &deviceChain;
    deviceInfo.queueCreateInfoCount = 1;
    deviceInfo.pQueueCreateInfos = &queue;
    deviceInfo.pEnabledFeatures = &features;
    deviceInfo.enabledExtensionCount = 1;
    deviceInfo.ppEnabledExtensionNames = &extension;
    VkDevice failedDevice = fakeDevice;
    Check(vkCreateDevice(Physical(1), &deviceInfo, nullptr, &failedDevice) == VK_ERROR_FEATURE_NOT_PRESENT && deviceCreateCalls == 1,
        "original-device-feature-failure-no-retry");
    Check(deviceArgumentsPreserved && devices.empty(), "physical-device-features-extensions-queue-and-chain-preserved");
    using namespace ResourceManagerGpuObservation;
    auto readFact = [] {
        Snapshot snapshot;
        if (ResourceManagerGpuPlacementReadDeviceObservations(&snapshot) != 1) std::abort();
        return snapshot.api[static_cast<uint32_t>(Api::Vulkan)];
    };
    Check(readFact().returnedDeviceCount == 0, "failed-device-with-prefilled-output-has-no-fact");
    deviceSuccess = true;
    auto createDevice = [&](VkPhysicalDevice physical, const void* chain = nullptr) {
        deviceChain.u.pLayerInfo = &deviceLink;
        deviceChain.pNext = chain;
        VkDevice result = fakeDevice;
        const auto created = vkCreateDevice(physical, &deviceInfo, nullptr, &result);
        if (created == VK_SUCCESS) vkDestroyDevice(result, nullptr);
        return created;
    };
    const auto queryCount = identityQueries;
    Check(createDevice(Physical(1)) == VK_SUCCESS && devices.empty() && deviceDestroyCalls == 1,
        "successful-device-registers-and-destroys-normally");
    Check(readFact().returnedDeviceCount == 1 && readFact().identity == Identity::Unavailable &&
        readFact().adapterLuid == 0 && identityQueries == queryCount, "vulkan10-does-not-use-unenabled-properties2");
    vkDestroyInstance(fakeInstance, nullptr);
    Check(instances.empty() && destroyCalls == 2, "reused-instance-address-clean");
    Check(CreateFake(VK_API_VERSION_1_1) == VK_SUCCESS && createDevice(Physical(1)) == VK_SUCCESS,
        "vulkan11-device-return-success");
    Check(readFact().identity == Identity::Adapter && readFact().adapterLuid == 0x5678 && readFact().returnedDeviceCount == 2,
        "actual-physical-identity-not-policy-target");
    Check(createDevice(Physical(2)) == VK_SUCCESS && readFact().identity == Identity::Unavailable && readFact().adapterLuid == 0,
        "invalid-luid-overwrites-prior-identity");
    VkPhysicalDevice members[] = {Physical(0), Physical(1)};
    VkDeviceGroupDeviceCreateInfo group{};
    group.sType = VK_STRUCTURE_TYPE_DEVICE_GROUP_DEVICE_CREATE_INFO;
    group.physicalDeviceCount = 1;
    group.pPhysicalDevices = members;
    Check(createDevice(Physical(0), &group) == VK_SUCCESS && readFact().identity == Identity::Adapter && readFact().adapterLuid == 0x1234,
        "single-member-group-observed");
    group.physicalDeviceCount = 2;
    Check(createDevice(Physical(0), &group) == VK_SUCCESS && readFact().identity == Identity::MultipleAdapters && readFact().adapterLuid == 0,
        "multi-member-group-not-single-adapter");
    std::array<VkBaseInStructure, 65> longChain{};
    for (size_t i = 1; i < longChain.size(); ++i) longChain[i-1].pNext = &longChain[i];
    Check(createDevice(Physical(0), longChain.data()) == VK_SUCCESS && readFact().identity == Identity::Unavailable,
        "bounded-identity-traversal-does-not-reject-device");
    const auto beforeRegistration = readFact();
    const auto beforeDestroy = deviceDestroyCalls;
    failDeviceRegistration = true;
    deviceChain.u.pLayerInfo = &deviceLink;
    deviceChain.pNext = nullptr;
    VkDevice registrationOutput = fakeDevice;
    const auto registrationResult = vkCreateDevice(Physical(1), &deviceInfo, nullptr, &registrationOutput);
    failNextAllocation = failDeviceRegistration = false;
    const auto afterRegistration = readFact();
    Check(registrationResult == VK_ERROR_OUT_OF_HOST_MEMORY && registrationOutput == VK_NULL_HANDLE && devices.empty() &&
        deviceDestroyCalls == beforeDestroy + 1, "registration-allocation-failure-destroys-provisional-device");
    Check(std::memcmp(&beforeRegistration, &afterRegistration, sizeof(beforeRegistration)) == 0,
        "rolled-back-device-not-published");
    vkDestroyInstance(fakeInstance, nullptr);
    devices.emplace(Key(fakeDevice), DeviceState{NextDevice});
    Check(vkGetDeviceProcAddr(fakeDevice, "vkRmTestDeviceExtension") == NextUnknown &&
        vkGetDeviceProcAddr(fakeDevice, "vkGetDeviceProcAddr") == nullptr, "device-unknown-and-disabled-forwarding");
    devices.clear();
    std::printf("{\"passed\":%s,\"checks\":%u,\"failedChecks\":%u,\"mode\":\"vulkan-contract\"}\n", failures ? "false" : "true", checks, failures);
    return failures ? 1 : 0;
}
