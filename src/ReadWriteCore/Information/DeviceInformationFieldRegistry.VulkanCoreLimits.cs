namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTexelBufferElements =
        new("gpu_all_vulkan_max_texel_buffer_elements", "gpu_adapter", "all", "uint32", "texels",
            "gpu.vulkan_core_1_0_maxTexelBufferElements_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTexelBufferElements");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxUniformBufferRange =
        new("gpu_all_vulkan_max_uniform_buffer_range", "gpu_adapter", "all", "uint32", "B",
            "gpu.vulkan_core_1_0_maxUniformBufferRange_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxUniformBufferRange");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxStorageBufferRange =
        new("gpu_all_vulkan_max_storage_buffer_range", "gpu_adapter", "all", "uint32", "B",
            "gpu.vulkan_core_1_0_maxStorageBufferRange_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxStorageBufferRange");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxPushConstantsSize =
        new("gpu_all_vulkan_max_push_constants_size", "gpu_adapter", "all", "uint32", "B",
            "gpu.vulkan_core_1_0_maxPushConstantsSize_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxPushConstantsSize");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxMemoryAllocationCount =
        new("gpu_all_vulkan_max_memory_allocation_count", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxMemoryAllocationCount_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxMemoryAllocationCount");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxSamplerAllocationCount =
        new("gpu_all_vulkan_max_sampler_allocation_count", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxSamplerAllocationCount_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxSamplerAllocationCount");

    public static readonly DeviceInformationDefinition GpuAllVulkanBufferImageGranularity =
        new("gpu_all_vulkan_buffer_image_granularity", "gpu_adapter", "all", "unsigned64", "B",
            "gpu.vulkan_core_1_0_bufferImageGranularity_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.bufferImageGranularity");

    public static readonly DeviceInformationDefinition GpuAllVulkanSparseAddressSpaceSize =
        new("gpu_all_vulkan_sparse_address_space_size", "gpu_adapter", "all", "unsigned64", "B",
            "gpu.vulkan_core_1_0_sparseAddressSpaceSize_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.sparseAddressSpaceSize");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxBoundDescriptorSets =
        new("gpu_all_vulkan_max_bound_descriptor_sets", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxBoundDescriptorSets_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxBoundDescriptorSets");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxPerStageDescriptorSamplers =
        new("gpu_all_vulkan_max_per_stage_descriptor_samplers", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxPerStageDescriptorSamplers_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxPerStageDescriptorSamplers");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxPerStageDescriptorUniformBuffers =
        new("gpu_all_vulkan_max_per_stage_descriptor_uniform_buffers", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxPerStageDescriptorUniformBuffers_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxPerStageDescriptorUniformBuffers");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxPerStageDescriptorStorageBuffers =
        new("gpu_all_vulkan_max_per_stage_descriptor_storage_buffers", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxPerStageDescriptorStorageBuffers_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxPerStageDescriptorStorageBuffers");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxPerStageDescriptorSampledImages =
        new("gpu_all_vulkan_max_per_stage_descriptor_sampled_images", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxPerStageDescriptorSampledImages_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxPerStageDescriptorSampledImages");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxPerStageDescriptorStorageImages =
        new("gpu_all_vulkan_max_per_stage_descriptor_storage_images", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxPerStageDescriptorStorageImages_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxPerStageDescriptorStorageImages");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxPerStageDescriptorInputAttachments =
        new("gpu_all_vulkan_max_per_stage_descriptor_input_attachments", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxPerStageDescriptorInputAttachments_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxPerStageDescriptorInputAttachments");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxPerStageResources =
        new("gpu_all_vulkan_max_per_stage_resources", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxPerStageResources_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxPerStageResources");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxDescriptorSetSamplers =
        new("gpu_all_vulkan_max_descriptor_set_samplers", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxDescriptorSetSamplers_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxDescriptorSetSamplers");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxDescriptorSetUniformBuffers =
        new("gpu_all_vulkan_max_descriptor_set_uniform_buffers", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxDescriptorSetUniformBuffers_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxDescriptorSetUniformBuffers");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxDescriptorSetUniformBuffersDynamic =
        new("gpu_all_vulkan_max_descriptor_set_uniform_buffers_dynamic", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxDescriptorSetUniformBuffersDynamic_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxDescriptorSetUniformBuffersDynamic");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxDescriptorSetStorageBuffers =
        new("gpu_all_vulkan_max_descriptor_set_storage_buffers", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxDescriptorSetStorageBuffers_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxDescriptorSetStorageBuffers");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxDescriptorSetStorageBuffersDynamic =
        new("gpu_all_vulkan_max_descriptor_set_storage_buffers_dynamic", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxDescriptorSetStorageBuffersDynamic_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxDescriptorSetStorageBuffersDynamic");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxDescriptorSetSampledImages =
        new("gpu_all_vulkan_max_descriptor_set_sampled_images", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxDescriptorSetSampledImages_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxDescriptorSetSampledImages");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxDescriptorSetStorageImages =
        new("gpu_all_vulkan_max_descriptor_set_storage_images", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxDescriptorSetStorageImages_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxDescriptorSetStorageImages");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxDescriptorSetInputAttachments =
        new("gpu_all_vulkan_max_descriptor_set_input_attachments", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxDescriptorSetInputAttachments_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxDescriptorSetInputAttachments");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxVertexInputAttributes =
        new("gpu_all_vulkan_max_vertex_input_attributes", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxVertexInputAttributes_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxVertexInputAttributes");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxVertexInputBindings =
        new("gpu_all_vulkan_max_vertex_input_bindings", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxVertexInputBindings_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxVertexInputBindings");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxVertexInputAttributeOffset =
        new("gpu_all_vulkan_max_vertex_input_attribute_offset", "gpu_adapter", "all", "uint32", "B",
            "gpu.vulkan_core_1_0_maxVertexInputAttributeOffset_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxVertexInputAttributeOffset");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxVertexInputBindingStride =
        new("gpu_all_vulkan_max_vertex_input_binding_stride", "gpu_adapter", "all", "uint32", "B",
            "gpu.vulkan_core_1_0_maxVertexInputBindingStride_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxVertexInputBindingStride");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxVertexOutputComponents =
        new("gpu_all_vulkan_max_vertex_output_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxVertexOutputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxVertexOutputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTessellationGenerationLevel =
        new("gpu_all_vulkan_max_tessellation_generation_level", "gpu_adapter", "all", "uint32", "levels",
            "gpu.vulkan_core_1_0_maxTessellationGenerationLevel_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTessellationGenerationLevel");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTessellationPatchSize =
        new("gpu_all_vulkan_max_tessellation_patch_size", "gpu_adapter", "all", "uint32", "vertices",
            "gpu.vulkan_core_1_0_maxTessellationPatchSize_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTessellationPatchSize");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTessellationControlPerVertexInputComponents =
        new("gpu_all_vulkan_max_tessellation_control_per_vertex_input_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxTessellationControlPerVertexInputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTessellationControlPerVertexInputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTessellationControlPerVertexOutputComponents =
        new("gpu_all_vulkan_max_tessellation_control_per_vertex_output_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxTessellationControlPerVertexOutputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTessellationControlPerVertexOutputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTessellationControlPerPatchOutputComponents =
        new("gpu_all_vulkan_max_tessellation_control_per_patch_output_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxTessellationControlPerPatchOutputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTessellationControlPerPatchOutputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTessellationControlTotalOutputComponents =
        new("gpu_all_vulkan_max_tessellation_control_total_output_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxTessellationControlTotalOutputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTessellationControlTotalOutputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTessellationEvaluationInputComponents =
        new("gpu_all_vulkan_max_tessellation_evaluation_input_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxTessellationEvaluationInputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTessellationEvaluationInputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTessellationEvaluationOutputComponents =
        new("gpu_all_vulkan_max_tessellation_evaluation_output_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxTessellationEvaluationOutputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTessellationEvaluationOutputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxGeometryShaderInvocations =
        new("gpu_all_vulkan_max_geometry_shader_invocations", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxGeometryShaderInvocations_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxGeometryShaderInvocations");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxGeometryInputComponents =
        new("gpu_all_vulkan_max_geometry_input_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxGeometryInputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxGeometryInputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxGeometryOutputComponents =
        new("gpu_all_vulkan_max_geometry_output_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxGeometryOutputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxGeometryOutputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxGeometryOutputVertices =
        new("gpu_all_vulkan_max_geometry_output_vertices", "gpu_adapter", "all", "uint32", "vertices",
            "gpu.vulkan_core_1_0_maxGeometryOutputVertices_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxGeometryOutputVertices");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxGeometryTotalOutputComponents =
        new("gpu_all_vulkan_max_geometry_total_output_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxGeometryTotalOutputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxGeometryTotalOutputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxFragmentInputComponents =
        new("gpu_all_vulkan_max_fragment_input_components", "gpu_adapter", "all", "uint32", "components",
            "gpu.vulkan_core_1_0_maxFragmentInputComponents_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxFragmentInputComponents");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxFragmentOutputAttachments =
        new("gpu_all_vulkan_max_fragment_output_attachments", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxFragmentOutputAttachments_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxFragmentOutputAttachments");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxFragmentDualSrcAttachments =
        new("gpu_all_vulkan_max_fragment_dual_src_attachments", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxFragmentDualSrcAttachments_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxFragmentDualSrcAttachments");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxFragmentCombinedOutputResources =
        new("gpu_all_vulkan_max_fragment_combined_output_resources", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxFragmentCombinedOutputResources_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxFragmentCombinedOutputResources");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxComputeSharedMemorySize =
        new("gpu_all_vulkan_max_compute_shared_memory_size", "gpu_adapter", "all", "uint32", "B",
            "gpu.vulkan_core_1_0_maxComputeSharedMemorySize_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxComputeSharedMemorySize");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxComputeWorkGroupCount =
        new("gpu_all_vulkan_max_compute_work_group_count", "gpu_adapter", "all", "array", "workgroups[3]",
            "gpu.vulkan_core_1_0_maxComputeWorkGroupCount_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxComputeWorkGroupCount");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxComputeWorkGroupInvocations =
        new("gpu_all_vulkan_max_compute_work_group_invocations", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxComputeWorkGroupInvocations_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxComputeWorkGroupInvocations");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxComputeWorkGroupSize =
        new("gpu_all_vulkan_max_compute_work_group_size", "gpu_adapter", "all", "array", "invocations[3]",
            "gpu.vulkan_core_1_0_maxComputeWorkGroupSize_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxComputeWorkGroupSize");

    public static readonly DeviceInformationDefinition GpuAllVulkanSubPixelPrecisionBits =
        new("gpu_all_vulkan_sub_pixel_precision_bits", "gpu_adapter", "all", "uint32", "bit",
            "gpu.vulkan_core_1_0_subPixelPrecisionBits_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.subPixelPrecisionBits");

    public static readonly DeviceInformationDefinition GpuAllVulkanSubTexelPrecisionBits =
        new("gpu_all_vulkan_sub_texel_precision_bits", "gpu_adapter", "all", "uint32", "bit",
            "gpu.vulkan_core_1_0_subTexelPrecisionBits_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.subTexelPrecisionBits");

    public static readonly DeviceInformationDefinition GpuAllVulkanMipmapPrecisionBits =
        new("gpu_all_vulkan_mipmap_precision_bits", "gpu_adapter", "all", "uint32", "bit",
            "gpu.vulkan_core_1_0_mipmapPrecisionBits_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.mipmapPrecisionBits");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxDrawIndexedIndexValue =
        new("gpu_all_vulkan_max_draw_indexed_index_value", "gpu_adapter", "all", "uint32", "index",
            "gpu.vulkan_core_1_0_maxDrawIndexedIndexValue_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxDrawIndexedIndexValue");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxDrawIndirectCount =
        new("gpu_all_vulkan_max_draw_indirect_count", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxDrawIndirectCount_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxDrawIndirectCount");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxSamplerLodBias =
        new("gpu_all_vulkan_max_sampler_lod_bias", "gpu_adapter", "all", "number", "mip-level",
            "gpu.vulkan_core_1_0_maxSamplerLodBias_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxSamplerLodBias");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxSamplerAnisotropy =
        new("gpu_all_vulkan_max_sampler_anisotropy", "gpu_adapter", "all", "number", "ratio",
            "gpu.vulkan_core_1_0_maxSamplerAnisotropy_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxSamplerAnisotropy");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxViewports =
        new("gpu_all_vulkan_max_viewports", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxViewports_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxViewports");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxViewportDimensions =
        new("gpu_all_vulkan_max_viewport_dimensions", "gpu_adapter", "all", "array", "pixels[2]",
            "gpu.vulkan_core_1_0_maxViewportDimensions_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxViewportDimensions");

    public static readonly DeviceInformationDefinition GpuAllVulkanViewportBoundsRange =
        new("gpu_all_vulkan_viewport_bounds_range", "gpu_adapter", "all", "array", "viewport-coordinate[min,max]",
            "gpu.vulkan_core_1_0_viewportBoundsRange_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.viewportBoundsRange");

    public static readonly DeviceInformationDefinition GpuAllVulkanViewportSubPixelBits =
        new("gpu_all_vulkan_viewport_sub_pixel_bits", "gpu_adapter", "all", "uint32", "bit",
            "gpu.vulkan_core_1_0_viewportSubPixelBits_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.viewportSubPixelBits");

    public static readonly DeviceInformationDefinition GpuAllVulkanMinMemoryMapAlignment =
        new("gpu_all_vulkan_min_memory_map_alignment", "gpu_adapter", "all", "unsigned-integer", "B",
            "gpu.vulkan_core_1_0_minMemoryMapAlignment_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.minMemoryMapAlignment");

    public static readonly DeviceInformationDefinition GpuAllVulkanMinTexelBufferOffsetAlignment =
        new("gpu_all_vulkan_min_texel_buffer_offset_alignment", "gpu_adapter", "all", "unsigned64", "B",
            "gpu.vulkan_core_1_0_minTexelBufferOffsetAlignment_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.minTexelBufferOffsetAlignment");

    public static readonly DeviceInformationDefinition GpuAllVulkanMinUniformBufferOffsetAlignment =
        new("gpu_all_vulkan_min_uniform_buffer_offset_alignment", "gpu_adapter", "all", "unsigned64", "B",
            "gpu.vulkan_core_1_0_minUniformBufferOffsetAlignment_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.minUniformBufferOffsetAlignment");

    public static readonly DeviceInformationDefinition GpuAllVulkanMinStorageBufferOffsetAlignment =
        new("gpu_all_vulkan_min_storage_buffer_offset_alignment", "gpu_adapter", "all", "unsigned64", "B",
            "gpu.vulkan_core_1_0_minStorageBufferOffsetAlignment_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.minStorageBufferOffsetAlignment");

    public static readonly DeviceInformationDefinition GpuAllVulkanMinTexelOffset =
        new("gpu_all_vulkan_min_texel_offset", "gpu_adapter", "all", "signed32", "texels",
            "gpu.vulkan_core_1_0_minTexelOffset_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.minTexelOffset");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTexelOffset =
        new("gpu_all_vulkan_max_texel_offset", "gpu_adapter", "all", "uint32", "texels",
            "gpu.vulkan_core_1_0_maxTexelOffset_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTexelOffset");

    public static readonly DeviceInformationDefinition GpuAllVulkanMinTexelGatherOffset =
        new("gpu_all_vulkan_min_texel_gather_offset", "gpu_adapter", "all", "signed32", "texels",
            "gpu.vulkan_core_1_0_minTexelGatherOffset_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.minTexelGatherOffset");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxTexelGatherOffset =
        new("gpu_all_vulkan_max_texel_gather_offset", "gpu_adapter", "all", "uint32", "texels",
            "gpu.vulkan_core_1_0_maxTexelGatherOffset_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxTexelGatherOffset");

    public static readonly DeviceInformationDefinition GpuAllVulkanMinInterpolationOffset =
        new("gpu_all_vulkan_min_interpolation_offset", "gpu_adapter", "all", "number", "interpolation-offset",
            "gpu.vulkan_core_1_0_minInterpolationOffset_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.minInterpolationOffset");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxInterpolationOffset =
        new("gpu_all_vulkan_max_interpolation_offset", "gpu_adapter", "all", "number", "interpolation-offset",
            "gpu.vulkan_core_1_0_maxInterpolationOffset_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxInterpolationOffset");

    public static readonly DeviceInformationDefinition GpuAllVulkanSubPixelInterpolationOffsetBits =
        new("gpu_all_vulkan_sub_pixel_interpolation_offset_bits", "gpu_adapter", "all", "uint32", "bit",
            "gpu.vulkan_core_1_0_subPixelInterpolationOffsetBits_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.subPixelInterpolationOffsetBits");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxFramebufferWidth =
        new("gpu_all_vulkan_max_framebuffer_width", "gpu_adapter", "all", "uint32", "pixels",
            "gpu.vulkan_core_1_0_maxFramebufferWidth_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxFramebufferWidth");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxFramebufferHeight =
        new("gpu_all_vulkan_max_framebuffer_height", "gpu_adapter", "all", "uint32", "pixels",
            "gpu.vulkan_core_1_0_maxFramebufferHeight_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxFramebufferHeight");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxFramebufferLayers =
        new("gpu_all_vulkan_max_framebuffer_layers", "gpu_adapter", "all", "uint32", "layers",
            "gpu.vulkan_core_1_0_maxFramebufferLayers_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxFramebufferLayers");

    public static readonly DeviceInformationDefinition GpuAllVulkanFramebufferColorSampleCounts =
        new("gpu_all_vulkan_framebuffer_color_sample_counts", "gpu_adapter", "all", "uint32", "bitmask32",
            "gpu.vulkan_core_1_0_framebufferColorSampleCounts_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.framebufferColorSampleCounts");

    public static readonly DeviceInformationDefinition GpuAllVulkanFramebufferDepthSampleCounts =
        new("gpu_all_vulkan_framebuffer_depth_sample_counts", "gpu_adapter", "all", "uint32", "bitmask32",
            "gpu.vulkan_core_1_0_framebufferDepthSampleCounts_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.framebufferDepthSampleCounts");

    public static readonly DeviceInformationDefinition GpuAllVulkanFramebufferStencilSampleCounts =
        new("gpu_all_vulkan_framebuffer_stencil_sample_counts", "gpu_adapter", "all", "uint32", "bitmask32",
            "gpu.vulkan_core_1_0_framebufferStencilSampleCounts_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.framebufferStencilSampleCounts");

    public static readonly DeviceInformationDefinition GpuAllVulkanFramebufferNoAttachmentsSampleCounts =
        new("gpu_all_vulkan_framebuffer_no_attachments_sample_counts", "gpu_adapter", "all", "uint32", "bitmask32",
            "gpu.vulkan_core_1_0_framebufferNoAttachmentsSampleCounts_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.framebufferNoAttachmentsSampleCounts");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxColorAttachments =
        new("gpu_all_vulkan_max_color_attachments", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxColorAttachments_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxColorAttachments");

    public static readonly DeviceInformationDefinition GpuAllVulkanSampledImageColorSampleCounts =
        new("gpu_all_vulkan_sampled_image_color_sample_counts", "gpu_adapter", "all", "uint32", "bitmask32",
            "gpu.vulkan_core_1_0_sampledImageColorSampleCounts_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.sampledImageColorSampleCounts");

    public static readonly DeviceInformationDefinition GpuAllVulkanSampledImageIntegerSampleCounts =
        new("gpu_all_vulkan_sampled_image_integer_sample_counts", "gpu_adapter", "all", "uint32", "bitmask32",
            "gpu.vulkan_core_1_0_sampledImageIntegerSampleCounts_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.sampledImageIntegerSampleCounts");

    public static readonly DeviceInformationDefinition GpuAllVulkanSampledImageDepthSampleCounts =
        new("gpu_all_vulkan_sampled_image_depth_sample_counts", "gpu_adapter", "all", "uint32", "bitmask32",
            "gpu.vulkan_core_1_0_sampledImageDepthSampleCounts_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.sampledImageDepthSampleCounts");

    public static readonly DeviceInformationDefinition GpuAllVulkanSampledImageStencilSampleCounts =
        new("gpu_all_vulkan_sampled_image_stencil_sample_counts", "gpu_adapter", "all", "uint32", "bitmask32",
            "gpu.vulkan_core_1_0_sampledImageStencilSampleCounts_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.sampledImageStencilSampleCounts");

    public static readonly DeviceInformationDefinition GpuAllVulkanStorageImageSampleCounts =
        new("gpu_all_vulkan_storage_image_sample_counts", "gpu_adapter", "all", "uint32", "bitmask32",
            "gpu.vulkan_core_1_0_storageImageSampleCounts_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.storageImageSampleCounts");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxSampleMaskWords =
        new("gpu_all_vulkan_max_sample_mask_words", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxSampleMaskWords_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxSampleMaskWords");

    public static readonly DeviceInformationDefinition GpuAllVulkanTimestampComputeAndGraphics =
        new("gpu_all_vulkan_timestamp_compute_and_graphics", "gpu_adapter", "all", "boolean", "boolean",
            "gpu.vulkan_core_1_0_timestampComputeAndGraphics_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.timestampComputeAndGraphics");

    public static readonly DeviceInformationDefinition GpuAllVulkanTimestampPeriod =
        new("gpu_all_vulkan_timestamp_period", "gpu_adapter", "all", "number", "ns/tick",
            "gpu.vulkan_core_1_0_timestampPeriod_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.timestampPeriod");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxClipDistances =
        new("gpu_all_vulkan_max_clip_distances", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxClipDistances_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxClipDistances");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxCullDistances =
        new("gpu_all_vulkan_max_cull_distances", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxCullDistances_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxCullDistances");

    public static readonly DeviceInformationDefinition GpuAllVulkanMaxCombinedClipAndCullDistances =
        new("gpu_all_vulkan_max_combined_clip_and_cull_distances", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_maxCombinedClipAndCullDistances_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.maxCombinedClipAndCullDistances");

    public static readonly DeviceInformationDefinition GpuAllVulkanDiscreteQueuePriorities =
        new("gpu_all_vulkan_discrete_queue_priorities", "gpu_adapter", "all", "uint32", "count",
            "gpu.vulkan_core_1_0_discreteQueuePriorities_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.discreteQueuePriorities");

    public static readonly DeviceInformationDefinition GpuAllVulkanPointSizeRange =
        new("gpu_all_vulkan_point_size_range", "gpu_adapter", "all", "array", "pixels[min,max]",
            "gpu.vulkan_core_1_0_pointSizeRange_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.pointSizeRange");

    public static readonly DeviceInformationDefinition GpuAllVulkanLineWidthRange =
        new("gpu_all_vulkan_line_width_range", "gpu_adapter", "all", "array", "pixels[min,max]",
            "gpu.vulkan_core_1_0_lineWidthRange_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.lineWidthRange");

    public static readonly DeviceInformationDefinition GpuAllVulkanPointSizeGranularity =
        new("gpu_all_vulkan_point_size_granularity", "gpu_adapter", "all", "number", "pixels",
            "gpu.vulkan_core_1_0_pointSizeGranularity_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.pointSizeGranularity");

    public static readonly DeviceInformationDefinition GpuAllVulkanLineWidthGranularity =
        new("gpu_all_vulkan_line_width_granularity", "gpu_adapter", "all", "number", "pixels",
            "gpu.vulkan_core_1_0_lineWidthGranularity_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.lineWidthGranularity");

    public static readonly DeviceInformationDefinition GpuAllVulkanStrictLines =
        new("gpu_all_vulkan_strict_lines", "gpu_adapter", "all", "boolean", "boolean",
            "gpu.vulkan_core_1_0_strictLines_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.strictLines");

    public static readonly DeviceInformationDefinition GpuAllVulkanStandardSampleLocations =
        new("gpu_all_vulkan_standard_sample_locations", "gpu_adapter", "all", "boolean", "boolean",
            "gpu.vulkan_core_1_0_standardSampleLocations_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.standardSampleLocations");

    public static readonly DeviceInformationDefinition GpuAllVulkanOptimalBufferCopyOffsetAlignment =
        new("gpu_all_vulkan_optimal_buffer_copy_offset_alignment", "gpu_adapter", "all", "unsigned64", "B",
            "gpu.vulkan_core_1_0_optimalBufferCopyOffsetAlignment_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.optimalBufferCopyOffsetAlignment");

    public static readonly DeviceInformationDefinition GpuAllVulkanOptimalBufferCopyRowPitchAlignment =
        new("gpu_all_vulkan_optimal_buffer_copy_row_pitch_alignment", "gpu_adapter", "all", "unsigned64", "B",
            "gpu.vulkan_core_1_0_optimalBufferCopyRowPitchAlignment_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.optimalBufferCopyRowPitchAlignment");

    public static readonly DeviceInformationDefinition GpuAllVulkanNonCoherentAtomSize =
        new("gpu_all_vulkan_non_coherent_atom_size", "gpu_adapter", "all", "unsigned64", "B",
            "gpu.vulkan_core_1_0_nonCoherentAtomSize_reported_by_exact_luid_and_pci_matched_physical_device",
            "vulkan.vkGetPhysicalDeviceProperties2.properties.limits.nonCoherentAtomSize");
}
