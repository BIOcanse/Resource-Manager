namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition GpuAllVulkanApiVersion =
        new("gpu_all_vulkan_api_version", "gpu_adapter", "all", "text", "text",
            "gpu.vulkan_api_version_reported_by_the_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.apiVersion");

    public static readonly DeviceInformationDefinition GpuAllVulkanExtension =
        new("gpu_all_vulkan_extension", "gpu_adapter_extension", "all", "text", "text",
            "gpu.vulkan_device_extension_name_reported_by_the_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkEnumerateDeviceExtensionProperties");

    public static readonly DeviceInformationDefinition GpuAllVulkanFeature =
        new("gpu_all_vulkan_feature", "gpu_adapter_feature", "all", "boolean", "boolean",
            "gpu.vulkan_core_1_0_feature_support_boolean_reported_by_the_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceFeatures2.VkPhysicalDeviceFeatures");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxImageDimension1D =
        new("gpu_all_vulkan_max_image_dimension_1d", "gpu_adapter", "all", "uint32", "pixels",
            "gpu.vulkan_core_1_0_maximum_1d_image_width_from_the_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxImageDimension1D");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxImageDimension2D =
        new("gpu_all_vulkan_max_image_dimension_2d", "gpu_adapter", "all", "uint32", "pixels",
            "gpu.vulkan_core_1_0_maximum_2d_image_width_or_height_dimension_from_the_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxImageDimension2D");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxImageDimension3D =
        new("gpu_all_vulkan_max_image_dimension_3d", "gpu_adapter", "all", "uint32", "pixels",
            "gpu.vulkan_core_1_0_maximum_3d_image_width_height_or_depth_dimension_from_the_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxImageDimension3D");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxImageDimensionCube =
        new("gpu_all_vulkan_max_image_dimension_cube", "gpu_adapter", "all", "uint32", "pixels",
            "gpu.vulkan_core_1_0_maximum_cube_image_width_or_height_dimension_from_the_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxImageDimensionCube");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxImageArrayLayers =
        new("gpu_all_vulkan_max_image_array_layers", "gpu_adapter", "all", "uint32", "layers",
            "gpu.vulkan_core_1_0_maximum_image_array_layers_from_the_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxImageArrayLayers");
}
