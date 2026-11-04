#define wmain RetainedVulkanLayerContractMain
#include "VulkanLayerContractProbe.cpp"
#undef wmain
#include <atomic>
#include <thread>

int wmain(int argc, wchar_t** argv)
{
    using namespace ResourceManagerGpuObservation;
    SetErrorMode(32771);
    if (argc != 2 || !SetEnvironmentVariableW(L"RM_GPU_SHIM_POLICY_FILE", argv[1])) return 2;
    PolicyFile(argv[1], false);
    CallObservationRequest request{16, 1, 4, 60000};
    auto start = [&] { return ResourceManagerGpuPlacementStartApiObservation(&request); };
    auto read = [] {
        CallObservationSnapshot result;
        if (ResourceManagerGpuPlacementReadApiObservation(&result) != 1) std::abort();
        return result;
    };
    auto stop = [] {
        CallObservationSnapshot result;
        if (ResourceManagerGpuPlacementStopApiObservation(&result) != 1) std::abort();
        return result;
    };
    auto enumOnce = [] {
        uint32_t count = 0;
        return vkEnumeratePhysicalDevices(fakeInstance, &count, nullptr);
    };
    Check(CreateFake(VK_API_VERSION_1_1) == VK_SUCCESS, "pre-window-instance-created");
    Check(read().observedApis == 0 && read().recording == 0, "old-create-not-api-evidence");
    Check(ResourceManagerGpuPlacementStartApiObservation(nullptr) == 0 && GetLastError() == ERROR_INVALID_PARAMETER,
        "null-start-rejected");
    for (const uint32_t mask : {0u, 1u, 2u, 8u, 16u, 5u, 31u, 32u})
    {
        request.apis = mask;
        Check(start() == 0 && GetLastError() == (mask ? ERROR_NOT_SUPPORTED : ERROR_INVALID_PARAMETER),
            "only-vulkan-request-supported");
    }
    request.apis = 4;
    request.byteSize = 15;
    Check(start() == 0 && GetLastError() == ERROR_INVALID_PARAMETER, "start-size-rejected");
    request.byteSize = 16;
    request.version = 2;
    Check(start() == 0 && GetLastError() == ERROR_INVALID_PARAMETER, "start-version-rejected");
    request.version = 1;
    request.durationMilliseconds = 0;
    Check(start() == 0 && GetLastError() == ERROR_INVALID_PARAMETER, "zero-duration-rejected");
    request.durationMilliseconds = 60000;
    Check(start() == 1 && GetLastError() == ERROR_SUCCESS, "window-started");
    Check(start() == 0 && GetLastError() == ERROR_BUSY, "active-window-not-replaced");
    uint32_t count = 0;
    vkEnumerateInstanceLayerProperties(&count, nullptr);
    vkEnumerateInstanceExtensionProperties(LayerName, &count, nullptr);
    vkEnumerateDeviceExtensionProperties(Physical(0), LayerName, &count, nullptr);
    vkGetInstanceProcAddr(fakeInstance, "vkEnumeratePhysicalDevices");
    vk_layerGetPhysicalDeviceProcAddr(fakeInstance, "vkRmTestPhysicalExtension");
    ResourceManagerGpuPlacementGetStatus(nullptr);
    Snapshot devicesBefore;
    ResourceManagerGpuPlacementReadDeviceObservations(&devicesBefore);
    Check(read().observedApis == 0 && read().recording == 1 && runtimePolicyPath.empty(),
        "metadata-and-controls-neither-record-nor-configure");
    CallObservationSnapshot malformed;
    malformed.version = 2;
    Check(ResourceManagerGpuPlacementStopApiObservation(&malformed) == 0 && GetLastError() == ERROR_INVALID_DATA &&
        read().recording == 1, "invalid-stop-does-not-stop");
    Check(ResourceManagerGpuPlacementReadApiObservation(nullptr) == 0 && GetLastError() == ERROR_INVALID_PARAMETER,
        "null-read-rejected");
    malformed.version = 1;
    malformed.byteSize = 15;
    Check(ResourceManagerGpuPlacementReadApiObservation(&malformed) == 0 && GetLastError() == ERROR_INVALID_DATA,
        "invalid-read-size-rejected");
    Check(enumOnce() == VK_SUCCESS && read().observedApis == 4, "actual-enumeration-recorded");
    Check(stop().observedApis == 4 && read().recording == 0, "stop-preserves-actual-result");
    Check(start() == 1 && read().observedApis == 0, "next-window-does-not-replay-old-calls");
    enumerateResult = VK_ERROR_OUT_OF_HOST_MEMORY;
    count = 73;
    SetLastError(9137);
    const auto originalSelection = ReadCurrentSelection();
    ResourceManagerVulkan::EnumeratePhysicalDevices(NextEnumerate, fakeInstance, &count, nullptr,
        originalSelection, Instance(fakeInstance).identityProperties);
    const auto originalError = GetLastError();
    SetLastError(9137);
    const auto failed = vkEnumeratePhysicalDevices(fakeInstance, &count, nullptr);
    const auto lastError = GetLastError();
    Check(failed == VK_ERROR_OUT_OF_HOST_MEMORY && count == 73 && lastError == originalError && read().observedApis == 4,
        "failed-actual-call-and-original-error-output-preserved");
    enumerateResult = VK_SUCCESS;
    stop();
    Check(start() == 1, "group-window-started");
    count = 0;
    Check(vkEnumeratePhysicalDeviceGroups(fakeInstance, &count, nullptr) == VK_SUCCESS && read().observedApis == 4,
        "actual-group-entry-recorded");
    stop();
    Check(start() == 1, "destroy-window-started");
    vkDestroyInstance(fakeInstance, nullptr);
    Check(read().observedApis == 4 && instances.empty(), "actual-destroy-recorded-and-owned-state-cleared");
    stop();
    Check(start() == 1, "no-instance-window-started");
    Check(ResourceManagerGpuPlacementConfigure(argv[1]) == 0 && read().recording == 1,
        "configure-without-instance-does-not-stop");
    Check(CreateFake(VK_API_VERSION_1_0) == VK_SUCCESS && read().observedApis == 4, "actual-create-recorded");
    PolicyFile(argv[1], true);
    Check(ResourceManagerGpuPlacementConfigure(argv[1]) == 0 && read().recording == 1 && runtimePolicyPath.empty(),
        "legacy-instance-configure-rejection-keeps-window");
    vkDestroyInstance(fakeInstance, nullptr);
    PolicyFile(argv[1], false);
    Check(CreateFake(VK_API_VERSION_1_1) == VK_SUCCESS, "current-instance-recreated");
    stop();
    request.durationMilliseconds = 1;
    Check(start() == 1, "bounded-expiry-window-started");
    Sleep(20);
    Check(enumOnce() == VK_SUCCESS && read().recording == 0 && read().observedApis == 0,
        "expired-window-does-not-record-late-entry");
    request.durationMilliseconds = 60000;
    Check(start() == 1, "expired-window-can-be-replaced");
    std::atomic<unsigned> errors{0};
    auto reader = [&] {
        for (unsigned i = 0; i < 500; ++i)
        {
            const auto value = read();
            if (value.byteSize != 16 || value.version != 1 || (value.observedApis & ~4u) || value.recording > 1) ++errors;
        }
    };
    std::thread first(reader), second(reader);
    for (unsigned i = 0; i < 500; ++i) if (enumOnce() != VK_SUCCESS) ++errors;
    first.join();
    second.join();
    Check(errors == 0 && read().observedApis == 4, "concurrent-complete-snapshots-and-actual-calls");
    failNextAllocation = true;
    const auto rejected = ResourceManagerGpuPlacementConfigure(argv[1]);
    failNextAllocation = false;
    Check(rejected == 0 && read().recording == 1 && runtimePolicyPath.empty(), "allocation-failure-keeps-window");
    Check(ResourceManagerGpuPlacementConfigure(argv[1]) == 23 && read().recording == 0 && read().observedApis == 4,
        "accepted-configuration-stops-without-erasing-result");
    Check(start() == 0 && GetLastError() == ERROR_INVALID_STATE, "configured-layer-rejects-observation-restart");
    vkDestroyInstance(fakeInstance, nullptr);
    Check(instances.empty() && devices.empty() && ResourceManagerGpuPlacementGetStatus(nullptr) == 0,
        "normal-owned-instance-cleanup");
    Check(DeleteFileW(argv[1]) != 0, "owned-policy-file-removed");
    std::printf("{\"passed\":%s,\"checks\":%u,\"failedChecks\":%u,\"mode\":\"vulkan-layer-api-observation\"}\n",
        failures ? "false" : "true", checks, failures);
    return failures ? 1 : 0;
}
