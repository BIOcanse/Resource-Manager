using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>One exact physical-device observation. Null values mean that selected query was unavailable;
/// an empty extension list is a successful query that reported no device extensions.</summary>
public sealed record WindowsVulkanAdapterCapabilityRead(
    WindowsD3dAdapterIdentity Adapter,
    string? ApiVersion,
    ImmutableArray<string>? Extensions,
    ImmutableArray<WindowsVulkanFeatureRead>? CoreFeatures)
{
    public WindowsVulkanCoreLimitsRead? CoreLimits { get; init; }
}

public sealed record WindowsVulkanFeatureRead(string Name, bool? Supported);

/// <summary>Selected Vulkan 1.0 core physical-device limits. A null member was not selected;
/// a null record means the selected limits could not be read. Fixed arrays retain their Vulkan member order.</summary>
public sealed record WindowsVulkanCoreLimitsRead(
    uint? MaxImageDimension1D,
    uint? MaxImageDimension2D,
    uint? MaxImageDimension3D,
    uint? MaxImageDimensionCube = null,
    uint? MaxImageArrayLayers = null)
{
    public uint? MaxTexelBufferElements { get; init; }
    public uint? MaxUniformBufferRange { get; init; }
    public uint? MaxStorageBufferRange { get; init; }
    public uint? MaxPushConstantsSize { get; init; }
    public uint? MaxMemoryAllocationCount { get; init; }
    public uint? MaxSamplerAllocationCount { get; init; }
    public ulong? BufferImageGranularity { get; init; }
    public ulong? SparseAddressSpaceSize { get; init; }
    public uint? MaxBoundDescriptorSets { get; init; }
    public uint? MaxPerStageDescriptorSamplers { get; init; }
    public uint? MaxPerStageDescriptorUniformBuffers { get; init; }
    public uint? MaxPerStageDescriptorStorageBuffers { get; init; }
    public uint? MaxPerStageDescriptorSampledImages { get; init; }
    public uint? MaxPerStageDescriptorStorageImages { get; init; }
    public uint? MaxPerStageDescriptorInputAttachments { get; init; }
    public uint? MaxPerStageResources { get; init; }
    public uint? MaxDescriptorSetSamplers { get; init; }
    public uint? MaxDescriptorSetUniformBuffers { get; init; }
    public uint? MaxDescriptorSetUniformBuffersDynamic { get; init; }
    public uint? MaxDescriptorSetStorageBuffers { get; init; }
    public uint? MaxDescriptorSetStorageBuffersDynamic { get; init; }
    public uint? MaxDescriptorSetSampledImages { get; init; }
    public uint? MaxDescriptorSetStorageImages { get; init; }
    public uint? MaxDescriptorSetInputAttachments { get; init; }
    public uint? MaxVertexInputAttributes { get; init; }
    public uint? MaxVertexInputBindings { get; init; }
    public uint? MaxVertexInputAttributeOffset { get; init; }
    public uint? MaxVertexInputBindingStride { get; init; }
    public uint? MaxVertexOutputComponents { get; init; }
    public uint? MaxTessellationGenerationLevel { get; init; }
    public uint? MaxTessellationPatchSize { get; init; }
    public uint? MaxTessellationControlPerVertexInputComponents { get; init; }
    public uint? MaxTessellationControlPerVertexOutputComponents { get; init; }
    public uint? MaxTessellationControlPerPatchOutputComponents { get; init; }
    public uint? MaxTessellationControlTotalOutputComponents { get; init; }
    public uint? MaxTessellationEvaluationInputComponents { get; init; }
    public uint? MaxTessellationEvaluationOutputComponents { get; init; }
    public uint? MaxGeometryShaderInvocations { get; init; }
    public uint? MaxGeometryInputComponents { get; init; }
    public uint? MaxGeometryOutputComponents { get; init; }
    public uint? MaxGeometryOutputVertices { get; init; }
    public uint? MaxGeometryTotalOutputComponents { get; init; }
    public uint? MaxFragmentInputComponents { get; init; }
    public uint? MaxFragmentOutputAttachments { get; init; }
    public uint? MaxFragmentDualSrcAttachments { get; init; }
    public uint? MaxFragmentCombinedOutputResources { get; init; }
    public uint? MaxComputeSharedMemorySize { get; init; }
    public ImmutableArray<uint>? MaxComputeWorkGroupCount { get; init; }
    public uint? MaxComputeWorkGroupInvocations { get; init; }
    public ImmutableArray<uint>? MaxComputeWorkGroupSize { get; init; }
    public uint? SubPixelPrecisionBits { get; init; }
    public uint? SubTexelPrecisionBits { get; init; }
    public uint? MipmapPrecisionBits { get; init; }
    public uint? MaxDrawIndexedIndexValue { get; init; }
    public uint? MaxDrawIndirectCount { get; init; }
    public float? MaxSamplerLodBias { get; init; }
    public float? MaxSamplerAnisotropy { get; init; }
    public uint? MaxViewports { get; init; }
    public ImmutableArray<uint>? MaxViewportDimensions { get; init; }
    public ImmutableArray<float>? ViewportBoundsRange { get; init; }
    public uint? ViewportSubPixelBits { get; init; }
    public nuint? MinMemoryMapAlignment { get; init; }
    public ulong? MinTexelBufferOffsetAlignment { get; init; }
    public ulong? MinUniformBufferOffsetAlignment { get; init; }
    public ulong? MinStorageBufferOffsetAlignment { get; init; }
    public int? MinTexelOffset { get; init; }
    public uint? MaxTexelOffset { get; init; }
    public int? MinTexelGatherOffset { get; init; }
    public uint? MaxTexelGatherOffset { get; init; }
    public float? MinInterpolationOffset { get; init; }
    public float? MaxInterpolationOffset { get; init; }
    public uint? SubPixelInterpolationOffsetBits { get; init; }
    public uint? MaxFramebufferWidth { get; init; }
    public uint? MaxFramebufferHeight { get; init; }
    public uint? MaxFramebufferLayers { get; init; }
    public uint? FramebufferColorSampleCounts { get; init; }
    public uint? FramebufferDepthSampleCounts { get; init; }
    public uint? FramebufferStencilSampleCounts { get; init; }
    public uint? FramebufferNoAttachmentsSampleCounts { get; init; }
    public uint? MaxColorAttachments { get; init; }
    public uint? SampledImageColorSampleCounts { get; init; }
    public uint? SampledImageIntegerSampleCounts { get; init; }
    public uint? SampledImageDepthSampleCounts { get; init; }
    public uint? SampledImageStencilSampleCounts { get; init; }
    public uint? StorageImageSampleCounts { get; init; }
    public uint? MaxSampleMaskWords { get; init; }
    public bool? TimestampComputeAndGraphics { get; init; }
    public float? TimestampPeriod { get; init; }
    public uint? MaxClipDistances { get; init; }
    public uint? MaxCullDistances { get; init; }
    public uint? MaxCombinedClipAndCullDistances { get; init; }
    public uint? DiscreteQueuePriorities { get; init; }
    public ImmutableArray<float>? PointSizeRange { get; init; }
    public ImmutableArray<float>? LineWidthRange { get; init; }
    public float? PointSizeGranularity { get; init; }
    public float? LineWidthGranularity { get; init; }
    public bool? StrictLines { get; init; }
    public bool? StandardSampleLocations { get; init; }
    public ulong? OptimalBufferCopyOffsetAlignment { get; init; }
    public ulong? OptimalBufferCopyRowPitchAlignment { get; init; }
    public ulong? NonCoherentAtomSize { get; init; }
}

/// <summary>DirectorySucceeded means physical-device enumeration and identity inspection completed.
/// A successful directory can contain no row for a requested adapter, which projects selected values as unavailable.</summary>
public sealed record WindowsVulkanCapabilityReadResult(
    bool DirectorySucceeded,
    ImmutableArray<WindowsVulkanAdapterCapabilityRead> Adapters)
{
    internal static WindowsVulkanCapabilityReadResult Unavailable => new(false, []);
}

/// <summary>Read-only observations for an explicit Vulkan API field and adapter scope.</summary>
public interface IWindowsVulkanCapabilityReader
{
    WindowsVulkanCapabilityReadResult Read(WindowsVulkanCapabilityScope scope,
        WindowsVulkanCapabilityFields selectedFields, CancellationToken cancellationToken);
}

internal static class WindowsVulkanCore10FeatureCatalog
{
    internal static readonly ImmutableArray<string> Names =
    [
        "robustBufferAccess", "fullDrawIndexUint32", "imageCubeArray", "independentBlend",
        "geometryShader", "tessellationShader", "sampleRateShading", "dualSrcBlend", "logicOp",
        "multiDrawIndirect", "drawIndirectFirstInstance", "depthClamp", "depthBiasClamp", "fillModeNonSolid",
        "depthBounds", "wideLines", "largePoints", "alphaToOne", "multiViewport", "samplerAnisotropy",
        "textureCompressionETC2", "textureCompressionASTC_LDR", "textureCompressionBC", "occlusionQueryPrecise",
        "pipelineStatisticsQuery", "vertexPipelineStoresAndAtomics", "fragmentStoresAndAtomics",
        "shaderTessellationAndGeometryPointSize", "shaderImageGatherExtended", "shaderStorageImageExtendedFormats",
        "shaderStorageImageMultisample", "shaderStorageImageReadWithoutFormat", "shaderStorageImageWriteWithoutFormat",
        "shaderUniformBufferArrayDynamicIndexing", "shaderSampledImageArrayDynamicIndexing",
        "shaderStorageBufferArrayDynamicIndexing", "shaderStorageImageArrayDynamicIndexing", "shaderClipDistance",
        "shaderCullDistance", "shaderFloat64", "shaderInt64", "shaderInt16", "shaderResourceResidency",
        "shaderResourceMinLod", "sparseBinding", "sparseResidencyBuffer", "sparseResidencyImage2D",
        "sparseResidencyImage3D", "sparseResidency2Samples", "sparseResidency4Samples", "sparseResidency8Samples",
        "sparseResidency16Samples", "sparseResidencyAliased", "variableMultisampleRate", "inheritedQueries"
    ];
}
