using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

internal enum WindowsVulkanCoreLimitValueShape
{
    UInt32, UInt64, NativeSize, Signed32, Float32, Boolean, UInt32Array, Float32Array
}

internal sealed record WindowsVulkanCoreLimitBinding(
    DeviceInformationDefinition Definition,
    Func<WindowsVulkanCoreLimitsRead, object?> ReadValue,
    WindowsVulkanCoreLimitValueShape Shape,
    int ArrayLength = 0);

internal static class WindowsVulkanCoreLimitsCatalog
{
    internal static readonly ImmutableArray<WindowsVulkanCoreLimitBinding> Bindings =
    [
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTexelBufferElements, static value => value.MaxTexelBufferElements, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxUniformBufferRange, static value => value.MaxUniformBufferRange, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxStorageBufferRange, static value => value.MaxStorageBufferRange, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxPushConstantsSize, static value => value.MaxPushConstantsSize, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxMemoryAllocationCount, static value => value.MaxMemoryAllocationCount, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxSamplerAllocationCount, static value => value.MaxSamplerAllocationCount, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanBufferImageGranularity, static value => value.BufferImageGranularity, WindowsVulkanCoreLimitValueShape.UInt64),
        new(DeviceInformationFieldRegistry.GpuAllVulkanSparseAddressSpaceSize, static value => value.SparseAddressSpaceSize, WindowsVulkanCoreLimitValueShape.UInt64),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxBoundDescriptorSets, static value => value.MaxBoundDescriptorSets, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxPerStageDescriptorSamplers, static value => value.MaxPerStageDescriptorSamplers, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxPerStageDescriptorUniformBuffers, static value => value.MaxPerStageDescriptorUniformBuffers, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxPerStageDescriptorStorageBuffers, static value => value.MaxPerStageDescriptorStorageBuffers, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxPerStageDescriptorSampledImages, static value => value.MaxPerStageDescriptorSampledImages, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxPerStageDescriptorStorageImages, static value => value.MaxPerStageDescriptorStorageImages, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxPerStageDescriptorInputAttachments, static value => value.MaxPerStageDescriptorInputAttachments, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxPerStageResources, static value => value.MaxPerStageResources, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxDescriptorSetSamplers, static value => value.MaxDescriptorSetSamplers, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxDescriptorSetUniformBuffers, static value => value.MaxDescriptorSetUniformBuffers, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxDescriptorSetUniformBuffersDynamic, static value => value.MaxDescriptorSetUniformBuffersDynamic, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxDescriptorSetStorageBuffers, static value => value.MaxDescriptorSetStorageBuffers, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxDescriptorSetStorageBuffersDynamic, static value => value.MaxDescriptorSetStorageBuffersDynamic, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxDescriptorSetSampledImages, static value => value.MaxDescriptorSetSampledImages, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxDescriptorSetStorageImages, static value => value.MaxDescriptorSetStorageImages, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxDescriptorSetInputAttachments, static value => value.MaxDescriptorSetInputAttachments, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxVertexInputAttributes, static value => value.MaxVertexInputAttributes, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxVertexInputBindings, static value => value.MaxVertexInputBindings, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxVertexInputAttributeOffset, static value => value.MaxVertexInputAttributeOffset, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxVertexInputBindingStride, static value => value.MaxVertexInputBindingStride, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxVertexOutputComponents, static value => value.MaxVertexOutputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTessellationGenerationLevel, static value => value.MaxTessellationGenerationLevel, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTessellationPatchSize, static value => value.MaxTessellationPatchSize, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTessellationControlPerVertexInputComponents, static value => value.MaxTessellationControlPerVertexInputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTessellationControlPerVertexOutputComponents, static value => value.MaxTessellationControlPerVertexOutputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTessellationControlPerPatchOutputComponents, static value => value.MaxTessellationControlPerPatchOutputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTessellationControlTotalOutputComponents, static value => value.MaxTessellationControlTotalOutputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTessellationEvaluationInputComponents, static value => value.MaxTessellationEvaluationInputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTessellationEvaluationOutputComponents, static value => value.MaxTessellationEvaluationOutputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxGeometryShaderInvocations, static value => value.MaxGeometryShaderInvocations, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxGeometryInputComponents, static value => value.MaxGeometryInputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxGeometryOutputComponents, static value => value.MaxGeometryOutputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxGeometryOutputVertices, static value => value.MaxGeometryOutputVertices, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxGeometryTotalOutputComponents, static value => value.MaxGeometryTotalOutputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxFragmentInputComponents, static value => value.MaxFragmentInputComponents, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxFragmentOutputAttachments, static value => value.MaxFragmentOutputAttachments, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxFragmentDualSrcAttachments, static value => value.MaxFragmentDualSrcAttachments, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxFragmentCombinedOutputResources, static value => value.MaxFragmentCombinedOutputResources, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxComputeSharedMemorySize, static value => value.MaxComputeSharedMemorySize, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxComputeWorkGroupCount, static value => value.MaxComputeWorkGroupCount, WindowsVulkanCoreLimitValueShape.UInt32Array, 3),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxComputeWorkGroupInvocations, static value => value.MaxComputeWorkGroupInvocations, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxComputeWorkGroupSize, static value => value.MaxComputeWorkGroupSize, WindowsVulkanCoreLimitValueShape.UInt32Array, 3),
        new(DeviceInformationFieldRegistry.GpuAllVulkanSubPixelPrecisionBits, static value => value.SubPixelPrecisionBits, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanSubTexelPrecisionBits, static value => value.SubTexelPrecisionBits, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMipmapPrecisionBits, static value => value.MipmapPrecisionBits, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxDrawIndexedIndexValue, static value => value.MaxDrawIndexedIndexValue, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxDrawIndirectCount, static value => value.MaxDrawIndirectCount, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxSamplerLodBias, static value => value.MaxSamplerLodBias, WindowsVulkanCoreLimitValueShape.Float32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxSamplerAnisotropy, static value => value.MaxSamplerAnisotropy, WindowsVulkanCoreLimitValueShape.Float32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxViewports, static value => value.MaxViewports, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxViewportDimensions, static value => value.MaxViewportDimensions, WindowsVulkanCoreLimitValueShape.UInt32Array, 2),
        new(DeviceInformationFieldRegistry.GpuAllVulkanViewportBoundsRange, static value => value.ViewportBoundsRange, WindowsVulkanCoreLimitValueShape.Float32Array, 2),
        new(DeviceInformationFieldRegistry.GpuAllVulkanViewportSubPixelBits, static value => value.ViewportSubPixelBits, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMinMemoryMapAlignment, static value => value.MinMemoryMapAlignment, WindowsVulkanCoreLimitValueShape.NativeSize),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMinTexelBufferOffsetAlignment, static value => value.MinTexelBufferOffsetAlignment, WindowsVulkanCoreLimitValueShape.UInt64),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMinUniformBufferOffsetAlignment, static value => value.MinUniformBufferOffsetAlignment, WindowsVulkanCoreLimitValueShape.UInt64),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMinStorageBufferOffsetAlignment, static value => value.MinStorageBufferOffsetAlignment, WindowsVulkanCoreLimitValueShape.UInt64),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMinTexelOffset, static value => value.MinTexelOffset, WindowsVulkanCoreLimitValueShape.Signed32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTexelOffset, static value => value.MaxTexelOffset, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMinTexelGatherOffset, static value => value.MinTexelGatherOffset, WindowsVulkanCoreLimitValueShape.Signed32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxTexelGatherOffset, static value => value.MaxTexelGatherOffset, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMinInterpolationOffset, static value => value.MinInterpolationOffset, WindowsVulkanCoreLimitValueShape.Float32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxInterpolationOffset, static value => value.MaxInterpolationOffset, WindowsVulkanCoreLimitValueShape.Float32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanSubPixelInterpolationOffsetBits, static value => value.SubPixelInterpolationOffsetBits, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxFramebufferWidth, static value => value.MaxFramebufferWidth, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxFramebufferHeight, static value => value.MaxFramebufferHeight, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxFramebufferLayers, static value => value.MaxFramebufferLayers, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanFramebufferColorSampleCounts, static value => value.FramebufferColorSampleCounts, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanFramebufferDepthSampleCounts, static value => value.FramebufferDepthSampleCounts, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanFramebufferStencilSampleCounts, static value => value.FramebufferStencilSampleCounts, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanFramebufferNoAttachmentsSampleCounts, static value => value.FramebufferNoAttachmentsSampleCounts, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxColorAttachments, static value => value.MaxColorAttachments, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanSampledImageColorSampleCounts, static value => value.SampledImageColorSampleCounts, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanSampledImageIntegerSampleCounts, static value => value.SampledImageIntegerSampleCounts, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanSampledImageDepthSampleCounts, static value => value.SampledImageDepthSampleCounts, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanSampledImageStencilSampleCounts, static value => value.SampledImageStencilSampleCounts, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanStorageImageSampleCounts, static value => value.StorageImageSampleCounts, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxSampleMaskWords, static value => value.MaxSampleMaskWords, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanTimestampComputeAndGraphics, static value => value.TimestampComputeAndGraphics, WindowsVulkanCoreLimitValueShape.Boolean),
        new(DeviceInformationFieldRegistry.GpuAllVulkanTimestampPeriod, static value => value.TimestampPeriod, WindowsVulkanCoreLimitValueShape.Float32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxClipDistances, static value => value.MaxClipDistances, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxCullDistances, static value => value.MaxCullDistances, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanMaxCombinedClipAndCullDistances, static value => value.MaxCombinedClipAndCullDistances, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanDiscreteQueuePriorities, static value => value.DiscreteQueuePriorities, WindowsVulkanCoreLimitValueShape.UInt32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanPointSizeRange, static value => value.PointSizeRange, WindowsVulkanCoreLimitValueShape.Float32Array, 2),
        new(DeviceInformationFieldRegistry.GpuAllVulkanLineWidthRange, static value => value.LineWidthRange, WindowsVulkanCoreLimitValueShape.Float32Array, 2),
        new(DeviceInformationFieldRegistry.GpuAllVulkanPointSizeGranularity, static value => value.PointSizeGranularity, WindowsVulkanCoreLimitValueShape.Float32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanLineWidthGranularity, static value => value.LineWidthGranularity, WindowsVulkanCoreLimitValueShape.Float32),
        new(DeviceInformationFieldRegistry.GpuAllVulkanStrictLines, static value => value.StrictLines, WindowsVulkanCoreLimitValueShape.Boolean),
        new(DeviceInformationFieldRegistry.GpuAllVulkanStandardSampleLocations, static value => value.StandardSampleLocations, WindowsVulkanCoreLimitValueShape.Boolean),
        new(DeviceInformationFieldRegistry.GpuAllVulkanOptimalBufferCopyOffsetAlignment, static value => value.OptimalBufferCopyOffsetAlignment, WindowsVulkanCoreLimitValueShape.UInt64),
        new(DeviceInformationFieldRegistry.GpuAllVulkanOptimalBufferCopyRowPitchAlignment, static value => value.OptimalBufferCopyRowPitchAlignment, WindowsVulkanCoreLimitValueShape.UInt64),
        new(DeviceInformationFieldRegistry.GpuAllVulkanNonCoherentAtomSize, static value => value.NonCoherentAtomSize, WindowsVulkanCoreLimitValueShape.UInt64)
    ];

    internal static readonly ImmutableArray<DeviceInformationDefinition> Definitions =
        [.. Bindings.Select(static binding => binding.Definition)];

    internal static bool TryGetBinding(DeviceInformationDefinition definition,
        out WindowsVulkanCoreLimitBinding binding)
    {
        foreach (var candidate in Bindings)
        {
            if (!ReferenceEquals(candidate.Definition, definition)) continue;
            binding = candidate;
            return true;
        }
        binding = null!;
        return false;
    }

    internal static bool IsValidValue(WindowsVulkanCoreLimitBinding binding,
        WindowsVulkanCoreLimitsRead values)
    {
        var value = binding.ReadValue(values);
        return binding.Shape switch
        {
            WindowsVulkanCoreLimitValueShape.UInt32 => value is uint,
            WindowsVulkanCoreLimitValueShape.UInt64 => value is ulong,
            WindowsVulkanCoreLimitValueShape.NativeSize => value is nuint,
            WindowsVulkanCoreLimitValueShape.Signed32 => value is int,
            WindowsVulkanCoreLimitValueShape.Float32 => value is float number && float.IsFinite(number),
            WindowsVulkanCoreLimitValueShape.Boolean => value is bool,
            WindowsVulkanCoreLimitValueShape.UInt32Array => value is ImmutableArray<uint> unsigned
                && !unsigned.IsDefault && unsigned.Length == binding.ArrayLength,
            WindowsVulkanCoreLimitValueShape.Float32Array => value is ImmutableArray<float> numbers
                && !numbers.IsDefault && numbers.Length == binding.ArrayLength
                && numbers.All(float.IsFinite),
            _ => false
        };
    }

    internal static object? ReadValue(WindowsVulkanCoreLimitBinding binding,
        WindowsVulkanCoreLimitsRead values)
        => binding.ReadValue(values);
}
