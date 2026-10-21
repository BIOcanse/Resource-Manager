using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>Explicit read-only Vulkan 1.1 physical-device capability queries. Creates and destroys one Vulkan
/// instance per call; it creates no VkDevice, queue, context, command buffer, or workload.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsVulkanCapabilityNativeReader : IWindowsVulkanCapabilityReader
{
    internal static (int IdLuidOffset, int IdLuidValidOffset, int PropertiesDataOffset,
        int FeaturesDataOffset, int ExtensionPropertiesSize, int CoreFeatureBytes,
        int PropertiesLimitsOffset, int LimitsSize, int PropertiesPrefixSize,
        int MaxImageDimension1DOffset, int MaxImageDimension2DOffset, int MaxImageDimension3DOffset,
        int MaxImageDimensionCubeOffset, int MaxImageArrayLayersOffset,
        int MaxImageDimension1DAbsoluteOffset, int MaxImageDimension2DAbsoluteOffset,
        int MaxImageDimension3DAbsoluteOffset, int MaxImageDimensionCubeAbsoluteOffset,
        int MaxImageArrayLayersAbsoluteOffset) AbiLayout
    {
        get
        {
            var propertiesDataOffset = Marshal.OffsetOf<VkPhysicalDeviceProperties2Buffer>(
                nameof(VkPhysicalDeviceProperties2Buffer.Properties)).ToInt32();
            var propertiesLimitsOffset = Marshal.OffsetOf<VkPhysicalDevicePropertiesPrefix>(
                nameof(VkPhysicalDevicePropertiesPrefix.Limits)).ToInt32();
            var dimension1Offset = Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(
                nameof(VkPhysicalDeviceLimitsLayout.MaxImageDimension1D)).ToInt32();
            var dimension2Offset = Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(
                nameof(VkPhysicalDeviceLimitsLayout.MaxImageDimension2D)).ToInt32();
            var dimension3Offset = Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(
                nameof(VkPhysicalDeviceLimitsLayout.MaxImageDimension3D)).ToInt32();
            var dimensionCubeOffset = Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(
                nameof(VkPhysicalDeviceLimitsLayout.MaxImageDimensionCube)).ToInt32();
            var arrayLayersOffset = Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(
                nameof(VkPhysicalDeviceLimitsLayout.MaxImageArrayLayers)).ToInt32();
            return (
                Marshal.OffsetOf<VkPhysicalDeviceIDProperties>(nameof(VkPhysicalDeviceIDProperties.DeviceLuid)).ToInt32(),
                Marshal.OffsetOf<VkPhysicalDeviceIDProperties>(nameof(VkPhysicalDeviceIDProperties.DeviceLuidValid)).ToInt32(),
                propertiesDataOffset,
                Marshal.OffsetOf<VkPhysicalDeviceFeatures2Buffer>(nameof(VkPhysicalDeviceFeatures2Buffer.Features)).ToInt32(),
                sizeof(VkExtensionProperties), WindowsVulkanCore10FeatureCatalog.Names.Length * sizeof(uint),
                propertiesLimitsOffset, Marshal.SizeOf<VkPhysicalDeviceLimitsLayout>(),
                Marshal.SizeOf<VkPhysicalDevicePropertiesPrefix>(),
                dimension1Offset, dimension2Offset, dimension3Offset, dimensionCubeOffset, arrayLayersOffset,
                checked(propertiesDataOffset + propertiesLimitsOffset + dimension1Offset),
                checked(propertiesDataOffset + propertiesLimitsOffset + dimension2Offset),
                checked(propertiesDataOffset + propertiesLimitsOffset + dimension3Offset),
                checked(propertiesDataOffset + propertiesLimitsOffset + dimensionCubeOffset),
                checked(propertiesDataOffset + propertiesLimitsOffset + arrayLayersOffset));
        }
    }

    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const uint VkApiVersion11 = (1U << 22) | (1U << 12);
    private const uint VkStructureTypeApplicationInfo = 0;
    private const uint VkStructureTypeInstanceCreateInfo = 1;
    private const uint VkStructureTypePhysicalDeviceFeatures2 = 1000059000;
    private const uint VkStructureTypePhysicalDeviceProperties2 = 1000059001;
    private const uint VkStructureTypePhysicalDeviceIdProperties = 1000071004;
    private const int VkSuccess = 0;
    private const int VkIncomplete = 5;
    private const int MaximumExtensionNameBytes = 256;
    private const WindowsVulkanCapabilityFields ImageLimitFields =
        WindowsVulkanCapabilityFields.ImageDimensionLimits | WindowsVulkanCapabilityFields.CubeArrayLimits
        | WindowsVulkanCapabilityFields.CoreLimits;

    private static readonly UTF8Encoding StrictAscii = new(false, true);

    public WindowsVulkanCapabilityReadResult Read(WindowsVulkanCapabilityScope scope,
        WindowsVulkanCapabilityFields selectedFields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (selectedFields == WindowsVulkanCapabilityFields.None || (selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        cancellationToken.ThrowIfCancellationRequested();
        if (IntPtr.Size != 8) return WindowsVulkanCapabilityReadResult.Unavailable;
        if (!OperatingSystem.IsWindows()) return WindowsVulkanCapabilityReadResult.Unavailable;

        try { return ReadCore(scope, selectedFields, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return WindowsVulkanCapabilityReadResult.Unavailable;
        }
    }

    internal static ImmutableArray<int> CoreLimitsMemberOffsets =>
    [
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTexelBufferElements)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxUniformBufferRange)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxStorageBufferRange)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxPushConstantsSize)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxMemoryAllocationCount)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxSamplerAllocationCount)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.BufferImageGranularity)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.SparseAddressSpaceSize)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxBoundDescriptorSets)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxPerStageDescriptorSamplers)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxPerStageDescriptorUniformBuffers)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxPerStageDescriptorStorageBuffers)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxPerStageDescriptorSampledImages)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxPerStageDescriptorStorageImages)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxPerStageDescriptorInputAttachments)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxPerStageResources)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxDescriptorSetSamplers)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxDescriptorSetUniformBuffers)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxDescriptorSetUniformBuffersDynamic)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxDescriptorSetStorageBuffers)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxDescriptorSetStorageBuffersDynamic)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxDescriptorSetSampledImages)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxDescriptorSetStorageImages)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxDescriptorSetInputAttachments)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxVertexInputAttributes)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxVertexInputBindings)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxVertexInputAttributeOffset)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxVertexInputBindingStride)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxVertexOutputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTessellationGenerationLevel)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTessellationPatchSize)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTessellationControlPerVertexInputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTessellationControlPerVertexOutputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTessellationControlPerPatchOutputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTessellationControlTotalOutputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTessellationEvaluationInputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTessellationEvaluationOutputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxGeometryShaderInvocations)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxGeometryInputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxGeometryOutputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxGeometryOutputVertices)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxGeometryTotalOutputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxFragmentInputComponents)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxFragmentOutputAttachments)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxFragmentDualSrcAttachments)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxFragmentCombinedOutputResources)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxComputeSharedMemorySize)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxComputeWorkGroupCount)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxComputeWorkGroupInvocations)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxComputeWorkGroupSize)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.SubPixelPrecisionBits)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.SubTexelPrecisionBits)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MipmapPrecisionBits)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxDrawIndexedIndexValue)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxDrawIndirectCount)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxSamplerLodBias)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxSamplerAnisotropy)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxViewports)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxViewportDimensions)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.ViewportBoundsRange)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.ViewportSubPixelBits)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MinMemoryMapAlignment)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MinTexelBufferOffsetAlignment)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MinUniformBufferOffsetAlignment)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MinStorageBufferOffsetAlignment)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MinTexelOffset)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTexelOffset)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MinTexelGatherOffset)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxTexelGatherOffset)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MinInterpolationOffset)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxInterpolationOffset)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.SubPixelInterpolationOffsetBits)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxFramebufferWidth)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxFramebufferHeight)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxFramebufferLayers)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.FramebufferColorSampleCounts)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.FramebufferDepthSampleCounts)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.FramebufferStencilSampleCounts)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.FramebufferNoAttachmentsSampleCounts)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxColorAttachments)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.SampledImageColorSampleCounts)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.SampledImageIntegerSampleCounts)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.SampledImageDepthSampleCounts)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.SampledImageStencilSampleCounts)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.StorageImageSampleCounts)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxSampleMaskWords)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.TimestampComputeAndGraphics)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.TimestampPeriod)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxClipDistances)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxCullDistances)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.MaxCombinedClipAndCullDistances)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.DiscreteQueuePriorities)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.PointSizeRange)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.LineWidthRange)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.PointSizeGranularity)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.LineWidthGranularity)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.StrictLines)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.StandardSampleLocations)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.OptimalBufferCopyOffsetAlignment)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.OptimalBufferCopyRowPitchAlignment)).ToInt32(),
Marshal.OffsetOf<VkPhysicalDeviceLimitsLayout>(nameof(VkPhysicalDeviceLimitsLayout.NonCoherentAtomSize)).ToInt32()
    ];

    private static WindowsVulkanCapabilityReadResult ReadCore(WindowsVulkanCapabilityScope scope,
        WindowsVulkanCapabilityFields selectedFields, CancellationToken cancellationToken)
    {
        var module = LoadLibraryExW("vulkan-1.dll", IntPtr.Zero, LoadLibrarySearchSystem32);
        if (module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The system Vulkan loader is unavailable.");

        IntPtr instance = IntPtr.Zero;
        VkDestroyInstanceDelegate? destroyInstance = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var getInstanceProcAddress = Marshal.GetDelegateForFunctionPointer<VkGetInstanceProcAddressDelegate>(
                NativeLibrary.GetExport(module, "vkGetInstanceProcAddr"));
            var createInstance = GetDelegate<VkCreateInstanceDelegate>(getInstanceProcAddress, IntPtr.Zero, "vkCreateInstance");
            if (!TryCreateInstance(createInstance, out instance))
                return WindowsVulkanCapabilityReadResult.Unavailable;

            destroyInstance = GetDelegate<VkDestroyInstanceDelegate>(getInstanceProcAddress, instance, "vkDestroyInstance");
            var enumeratePhysicalDevices = GetDelegate<VkEnumeratePhysicalDevicesDelegate>(getInstanceProcAddress,
                instance, "vkEnumeratePhysicalDevices");
            var getProperties2 = GetDelegate<VkGetPhysicalDeviceProperties2Delegate>(getInstanceProcAddress,
                instance, "vkGetPhysicalDeviceProperties2");
            var enumerateDeviceExtensions = GetDelegate<VkEnumerateDeviceExtensionPropertiesDelegate>(getInstanceProcAddress,
                instance, "vkEnumerateDeviceExtensionProperties");
            var getFeatures2 = GetDelegate<VkGetPhysicalDeviceFeatures2Delegate>(getInstanceProcAddress,
                instance, "vkGetPhysicalDeviceFeatures2");

            var devices = EnumeratePhysicalDevices(instance, enumeratePhysicalDevices,
                scope.MaximumPhysicalDeviceCount, cancellationToken);
            var candidates = new Dictionary<WindowsD3dAdapterIdentity, List<PhysicalDeviceObservation>>();
            foreach (var adapter in scope.Adapters) candidates.Add(adapter, []);
            foreach (var physicalDevice in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observation = ReadIdentity(physicalDevice, getProperties2, scope.Adapters, selectedFields);
                if (observation is not { } observed || observed.MatchedAdapter is not { } target) continue;
                candidates[target].Add(observed);
            }

            var rows = ImmutableArray.CreateBuilder<WindowsVulkanAdapterCapabilityRead>(scope.Adapters.Length);
            foreach (var adapter in scope.Adapters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates[adapter].Count != 1)
                {
                    rows.Add(UnavailableAdapter(adapter));
                    continue;
                }

                var match = candidates[adapter][0];
                string? apiVersion = null;
                ImmutableArray<string>? extensions = null;
                ImmutableArray<WindowsVulkanFeatureRead>? features = null;
                WindowsVulkanCoreLimitsRead? coreLimits = null;
                if ((selectedFields & WindowsVulkanCapabilityFields.ApiVersion) != 0)
                    apiVersion = VersionText(match.ApiVersion);
                if ((selectedFields & WindowsVulkanCapabilityFields.Extensions) != 0)
                {
                    try
                    {
                        extensions = ReadExtensions(match.Handle, enumerateDeviceExtensions,
                            scope.MaximumExtensionCount, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { }
                }
                if ((selectedFields & WindowsVulkanCapabilityFields.CoreFeatures) != 0)
                {
                    try { features = ReadCoreFeatures(match.Handle, getFeatures2, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { }
                }
                if ((selectedFields & ImageLimitFields) != 0)
                    coreLimits = match.CoreLimits;
                rows.Add(new(adapter, apiVersion, extensions, features) { CoreLimits = coreLimits });
            }

            return new(true, rows.ToImmutable());
        }
        finally
        {
            if (instance != IntPtr.Zero)
            {
                try
                {
                    destroyInstance ??= GetDelegate<VkDestroyInstanceDelegate>(
                        Marshal.GetDelegateForFunctionPointer<VkGetInstanceProcAddressDelegate>(
                            NativeLibrary.GetExport(module, "vkGetInstanceProcAddr")), instance, "vkDestroyInstance");
                    destroyInstance(instance, IntPtr.Zero);
                }
                catch { }
            }
            _ = FreeLibrary(module);
        }
    }

    private static bool TryCreateInstance(VkCreateInstanceDelegate createInstance, out IntPtr instance)
    {
        instance = IntPtr.Zero;
        var applicationName = Marshal.StringToCoTaskMemUTF8("ResourceManager capability query");
        var engineName = Marshal.StringToCoTaskMemUTF8("ResourceManager");
        try
        {
            var application = new VkApplicationInfo
            {
                SType = VkStructureTypeApplicationInfo,
                PApplicationName = applicationName,
                ApplicationVersion = 1,
                PEngineName = engineName,
                EngineVersion = 1,
                ApiVersion = VkApiVersion11
            };
            var createInfo = new VkInstanceCreateInfo
            {
                SType = VkStructureTypeInstanceCreateInfo,
                PApplicationInfo = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>())
            };
            try
            {
                Marshal.StructureToPtr(application, createInfo.PApplicationInfo, false);
                return createInstance(ref createInfo, IntPtr.Zero, out instance) == VkSuccess && instance != IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(createInfo.PApplicationInfo); }
        }
        finally
        {
            Marshal.FreeCoTaskMem(applicationName);
            Marshal.FreeCoTaskMem(engineName);
        }
    }

    private static ImmutableArray<IntPtr> EnumeratePhysicalDevices(IntPtr instance,
        VkEnumeratePhysicalDevicesDelegate enumerate, int maximumCount, CancellationToken cancellationToken)
    {
        uint count = 0;
        cancellationToken.ThrowIfCancellationRequested();
        Check(enumerate(instance, &count, IntPtr.Zero), "vkEnumeratePhysicalDevices(count)");
        cancellationToken.ThrowIfCancellationRequested();
        if (count > (uint)maximumCount) throw new VulkanDataException("Vulkan physical-device enumeration exceeded its configured bound.");
        if (count == 0) return [];

        var buffer = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        try
        {
            new Span<byte>((void*)buffer, checked((int)count * IntPtr.Size)).Clear();
            cancellationToken.ThrowIfCancellationRequested();
            var returned = count;
            var result = enumerate(instance, &returned, buffer);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != VkSuccess || returned != count)
                throw new VulkanDataException("Vulkan physical-device enumeration changed or was incomplete.");
            var values = ImmutableArray.CreateBuilder<IntPtr>(checked((int)count));
            var seen = new HashSet<IntPtr>();
            for (var index = 0; index < count; index++)
            {
                var handle = Marshal.ReadIntPtr(buffer, checked((int)index * IntPtr.Size));
                if (handle == IntPtr.Zero || !seen.Add(handle))
                    throw new VulkanDataException("Vulkan returned a null or duplicate physical-device handle.");
                values.Add(handle);
            }
            return values.ToImmutable();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static PhysicalDeviceObservation? ReadIdentity(IntPtr physicalDevice,
        VkGetPhysicalDeviceProperties2Delegate getProperties2,
        ImmutableArray<WindowsD3dAdapterIdentity> selectedAdapters,
        WindowsVulkanCapabilityFields selectedFields)
    {
        VkPhysicalDeviceIDProperties* id = stackalloc VkPhysicalDeviceIDProperties[1];
        *id = default;
        id->SType = VkStructureTypePhysicalDeviceIdProperties;
        VkPhysicalDeviceProperties2Buffer* properties = stackalloc VkPhysicalDeviceProperties2Buffer[1];
        *properties = default;
        properties->SType = VkStructureTypePhysicalDeviceProperties2;
        properties->PNext = (IntPtr)id;
        getProperties2(physicalDevice, (IntPtr)properties);

        var identity = DecodeIdentityResponse(
            new ReadOnlySpan<byte>((byte*)properties, sizeof(VkPhysicalDeviceProperties2Buffer)),
            new ReadOnlySpan<byte>((byte*)id, sizeof(VkPhysicalDeviceIDProperties)),
            selectedAdapters, selectedFields);
        if (identity is not { } decoded) return null;
        return new(physicalDevice, decoded.Luid, checked((ushort)decoded.VendorId),
            checked((ushort)decoded.DeviceId), decoded.ApiVersion, decoded.MatchedAdapter, decoded.CoreLimits);
    }

    // Shared by the native call path and recorded ABI tests. The input spans are complete
    // VkPhysicalDeviceProperties2Buffer and VkPhysicalDeviceIDProperties output layouts.
    internal static WindowsVulkanDecodedPhysicalDeviceIdentity? DecodeIdentityResponse(
        ReadOnlySpan<byte> properties2Response, ReadOnlySpan<byte> idPropertiesResponse,
        ImmutableArray<WindowsD3dAdapterIdentity> selectedAdapters,
        WindowsVulkanCapabilityFields selectedFields)
    {
        if (selectedAdapters.IsDefaultOrEmpty)
            throw new ArgumentException("At least one selected adapter is required.", nameof(selectedAdapters));
        if (selectedFields == WindowsVulkanCapabilityFields.None
            || (selectedFields & ~WindowsVulkanCapabilityScope.KnownFields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));

        var propertiesOffset = Marshal.OffsetOf<VkPhysicalDeviceProperties2Buffer>(
            nameof(VkPhysicalDeviceProperties2Buffer.Properties)).ToInt32();
        var idLength = Marshal.SizeOf<VkPhysicalDeviceIDProperties>();
        var propertiesLength = checked(propertiesOffset + Marshal.SizeOf<VkPhysicalDevicePropertiesPrefix>());
        if (idPropertiesResponse.Length < idLength)
            throw new ArgumentException("Vulkan ID-properties response is shorter than its declared structure.", nameof(idPropertiesResponse));
        if (properties2Response.Length < propertiesLength)
            throw new ArgumentException("Vulkan properties response does not contain the typed limits prefix.", nameof(properties2Response));

        fixed (byte* idBytes = idPropertiesResponse)
        fixed (byte* propertiesBytes = properties2Response)
        {
            var id = (VkPhysicalDeviceIDProperties*)idBytes;
            var properties2 = (VkPhysicalDeviceProperties2Buffer*)propertiesBytes;
            var properties = (VkPhysicalDevicePropertiesPrefix*)(propertiesBytes + propertiesOffset);
            if (id->SType != VkStructureTypePhysicalDeviceIdProperties
                || properties2->SType != VkStructureTypePhysicalDeviceProperties2
                || id->DeviceLuidValid != 1 || id->DeviceNodeMask == 0
                || (id->DeviceNodeMask & (id->DeviceNodeMask - 1)) != 0)
                return null;

            byte* luidBytes = id->DeviceLuid;
            var luid = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(luidBytes, 8));
            var apiVersion = properties->ApiVersion;
            var vendorId = properties->VendorId;
            var deviceId = properties->DeviceId;
            if (luid == 0 || apiVersion == 0 || vendorId is 0 or > ushort.MaxValue
                || deviceId is 0 or > ushort.MaxValue)
                return null;

            WindowsD3dAdapterIdentity? matchedAdapter = null;
            foreach (var adapter in selectedAdapters)
            {
                if (adapter.Luid == luid && adapter.PciVendorId == vendorId && adapter.PciDeviceId == deviceId)
                {
                    matchedAdapter = adapter;
                    break;
                }
            }

            WindowsVulkanCoreLimitsRead? coreLimits = null;
            if (matchedAdapter is not null && (selectedFields & ImageLimitFields) != 0)
            {
                var limits = properties->Limits;
                var readCoreLimits = (selectedFields & WindowsVulkanCapabilityFields.CoreLimits) != 0;
                coreLimits = new(
                    readCoreLimits || (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension1D) != 0
                        ? limits.MaxImageDimension1D : null,
                    readCoreLimits || (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension2D) != 0
                        ? limits.MaxImageDimension2D : null,
                    readCoreLimits || (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension3D) != 0
                        ? limits.MaxImageDimension3D : null,
                    readCoreLimits || (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimensionCube) != 0
                        ? limits.MaxImageDimensionCube : null,
                    readCoreLimits || (selectedFields & WindowsVulkanCapabilityFields.MaxImageArrayLayers) != 0
                        ? limits.MaxImageArrayLayers : null)
                {
                    MaxTexelBufferElements = readCoreLimits ? limits.MaxTexelBufferElements : null,
                    MaxUniformBufferRange = readCoreLimits ? limits.MaxUniformBufferRange : null,
                    MaxStorageBufferRange = readCoreLimits ? limits.MaxStorageBufferRange : null,
                    MaxPushConstantsSize = readCoreLimits ? limits.MaxPushConstantsSize : null,
                    MaxMemoryAllocationCount = readCoreLimits ? limits.MaxMemoryAllocationCount : null,
                    MaxSamplerAllocationCount = readCoreLimits ? limits.MaxSamplerAllocationCount : null,
                    BufferImageGranularity = readCoreLimits ? limits.BufferImageGranularity : null,
                    SparseAddressSpaceSize = readCoreLimits ? limits.SparseAddressSpaceSize : null,
                    MaxBoundDescriptorSets = readCoreLimits ? limits.MaxBoundDescriptorSets : null,
                    MaxPerStageDescriptorSamplers = readCoreLimits ? limits.MaxPerStageDescriptorSamplers : null,
                    MaxPerStageDescriptorUniformBuffers = readCoreLimits ? limits.MaxPerStageDescriptorUniformBuffers : null,
                    MaxPerStageDescriptorStorageBuffers = readCoreLimits ? limits.MaxPerStageDescriptorStorageBuffers : null,
                    MaxPerStageDescriptorSampledImages = readCoreLimits ? limits.MaxPerStageDescriptorSampledImages : null,
                    MaxPerStageDescriptorStorageImages = readCoreLimits ? limits.MaxPerStageDescriptorStorageImages : null,
                    MaxPerStageDescriptorInputAttachments = readCoreLimits ? limits.MaxPerStageDescriptorInputAttachments : null,
                    MaxPerStageResources = readCoreLimits ? limits.MaxPerStageResources : null,
                    MaxDescriptorSetSamplers = readCoreLimits ? limits.MaxDescriptorSetSamplers : null,
                    MaxDescriptorSetUniformBuffers = readCoreLimits ? limits.MaxDescriptorSetUniformBuffers : null,
                    MaxDescriptorSetUniformBuffersDynamic = readCoreLimits ? limits.MaxDescriptorSetUniformBuffersDynamic : null,
                    MaxDescriptorSetStorageBuffers = readCoreLimits ? limits.MaxDescriptorSetStorageBuffers : null,
                    MaxDescriptorSetStorageBuffersDynamic = readCoreLimits ? limits.MaxDescriptorSetStorageBuffersDynamic : null,
                    MaxDescriptorSetSampledImages = readCoreLimits ? limits.MaxDescriptorSetSampledImages : null,
                    MaxDescriptorSetStorageImages = readCoreLimits ? limits.MaxDescriptorSetStorageImages : null,
                    MaxDescriptorSetInputAttachments = readCoreLimits ? limits.MaxDescriptorSetInputAttachments : null,
                    MaxVertexInputAttributes = readCoreLimits ? limits.MaxVertexInputAttributes : null,
                    MaxVertexInputBindings = readCoreLimits ? limits.MaxVertexInputBindings : null,
                    MaxVertexInputAttributeOffset = readCoreLimits ? limits.MaxVertexInputAttributeOffset : null,
                    MaxVertexInputBindingStride = readCoreLimits ? limits.MaxVertexInputBindingStride : null,
                    MaxVertexOutputComponents = readCoreLimits ? limits.MaxVertexOutputComponents : null,
                    MaxTessellationGenerationLevel = readCoreLimits ? limits.MaxTessellationGenerationLevel : null,
                    MaxTessellationPatchSize = readCoreLimits ? limits.MaxTessellationPatchSize : null,
                    MaxTessellationControlPerVertexInputComponents = readCoreLimits ? limits.MaxTessellationControlPerVertexInputComponents : null,
                    MaxTessellationControlPerVertexOutputComponents = readCoreLimits ? limits.MaxTessellationControlPerVertexOutputComponents : null,
                    MaxTessellationControlPerPatchOutputComponents = readCoreLimits ? limits.MaxTessellationControlPerPatchOutputComponents : null,
                    MaxTessellationControlTotalOutputComponents = readCoreLimits ? limits.MaxTessellationControlTotalOutputComponents : null,
                    MaxTessellationEvaluationInputComponents = readCoreLimits ? limits.MaxTessellationEvaluationInputComponents : null,
                    MaxTessellationEvaluationOutputComponents = readCoreLimits ? limits.MaxTessellationEvaluationOutputComponents : null,
                    MaxGeometryShaderInvocations = readCoreLimits ? limits.MaxGeometryShaderInvocations : null,
                    MaxGeometryInputComponents = readCoreLimits ? limits.MaxGeometryInputComponents : null,
                    MaxGeometryOutputComponents = readCoreLimits ? limits.MaxGeometryOutputComponents : null,
                    MaxGeometryOutputVertices = readCoreLimits ? limits.MaxGeometryOutputVertices : null,
                    MaxGeometryTotalOutputComponents = readCoreLimits ? limits.MaxGeometryTotalOutputComponents : null,
                    MaxFragmentInputComponents = readCoreLimits ? limits.MaxFragmentInputComponents : null,
                    MaxFragmentOutputAttachments = readCoreLimits ? limits.MaxFragmentOutputAttachments : null,
                    MaxFragmentDualSrcAttachments = readCoreLimits ? limits.MaxFragmentDualSrcAttachments : null,
                    MaxFragmentCombinedOutputResources = readCoreLimits ? limits.MaxFragmentCombinedOutputResources : null,
                    MaxComputeSharedMemorySize = readCoreLimits ? limits.MaxComputeSharedMemorySize : null,
                    MaxComputeWorkGroupCount = readCoreLimits ? ImmutableArray.Create(limits.MaxComputeWorkGroupCount[0], limits.MaxComputeWorkGroupCount[1], limits.MaxComputeWorkGroupCount[2]) : null,
                    MaxComputeWorkGroupInvocations = readCoreLimits ? limits.MaxComputeWorkGroupInvocations : null,
                    MaxComputeWorkGroupSize = readCoreLimits ? ImmutableArray.Create(limits.MaxComputeWorkGroupSize[0], limits.MaxComputeWorkGroupSize[1], limits.MaxComputeWorkGroupSize[2]) : null,
                    SubPixelPrecisionBits = readCoreLimits ? limits.SubPixelPrecisionBits : null,
                    SubTexelPrecisionBits = readCoreLimits ? limits.SubTexelPrecisionBits : null,
                    MipmapPrecisionBits = readCoreLimits ? limits.MipmapPrecisionBits : null,
                    MaxDrawIndexedIndexValue = readCoreLimits ? limits.MaxDrawIndexedIndexValue : null,
                    MaxDrawIndirectCount = readCoreLimits ? limits.MaxDrawIndirectCount : null,
                    MaxSamplerLodBias = readCoreLimits ? limits.MaxSamplerLodBias : null,
                    MaxSamplerAnisotropy = readCoreLimits ? limits.MaxSamplerAnisotropy : null,
                    MaxViewports = readCoreLimits ? limits.MaxViewports : null,
                    MaxViewportDimensions = readCoreLimits ? ImmutableArray.Create(limits.MaxViewportDimensions[0], limits.MaxViewportDimensions[1]) : null,
                    ViewportBoundsRange = readCoreLimits ? ImmutableArray.Create(limits.ViewportBoundsRange[0], limits.ViewportBoundsRange[1]) : null,
                    ViewportSubPixelBits = readCoreLimits ? limits.ViewportSubPixelBits : null,
                    MinMemoryMapAlignment = readCoreLimits ? limits.MinMemoryMapAlignment : null,
                    MinTexelBufferOffsetAlignment = readCoreLimits ? limits.MinTexelBufferOffsetAlignment : null,
                    MinUniformBufferOffsetAlignment = readCoreLimits ? limits.MinUniformBufferOffsetAlignment : null,
                    MinStorageBufferOffsetAlignment = readCoreLimits ? limits.MinStorageBufferOffsetAlignment : null,
                    MinTexelOffset = readCoreLimits ? limits.MinTexelOffset : null,
                    MaxTexelOffset = readCoreLimits ? limits.MaxTexelOffset : null,
                    MinTexelGatherOffset = readCoreLimits ? limits.MinTexelGatherOffset : null,
                    MaxTexelGatherOffset = readCoreLimits ? limits.MaxTexelGatherOffset : null,
                    MinInterpolationOffset = readCoreLimits ? limits.MinInterpolationOffset : null,
                    MaxInterpolationOffset = readCoreLimits ? limits.MaxInterpolationOffset : null,
                    SubPixelInterpolationOffsetBits = readCoreLimits ? limits.SubPixelInterpolationOffsetBits : null,
                    MaxFramebufferWidth = readCoreLimits ? limits.MaxFramebufferWidth : null,
                    MaxFramebufferHeight = readCoreLimits ? limits.MaxFramebufferHeight : null,
                    MaxFramebufferLayers = readCoreLimits ? limits.MaxFramebufferLayers : null,
                    FramebufferColorSampleCounts = readCoreLimits ? limits.FramebufferColorSampleCounts : null,
                    FramebufferDepthSampleCounts = readCoreLimits ? limits.FramebufferDepthSampleCounts : null,
                    FramebufferStencilSampleCounts = readCoreLimits ? limits.FramebufferStencilSampleCounts : null,
                    FramebufferNoAttachmentsSampleCounts = readCoreLimits ? limits.FramebufferNoAttachmentsSampleCounts : null,
                    MaxColorAttachments = readCoreLimits ? limits.MaxColorAttachments : null,
                    SampledImageColorSampleCounts = readCoreLimits ? limits.SampledImageColorSampleCounts : null,
                    SampledImageIntegerSampleCounts = readCoreLimits ? limits.SampledImageIntegerSampleCounts : null,
                    SampledImageDepthSampleCounts = readCoreLimits ? limits.SampledImageDepthSampleCounts : null,
                    SampledImageStencilSampleCounts = readCoreLimits ? limits.SampledImageStencilSampleCounts : null,
                    StorageImageSampleCounts = readCoreLimits ? limits.StorageImageSampleCounts : null,
                    MaxSampleMaskWords = readCoreLimits ? limits.MaxSampleMaskWords : null,
                    TimestampComputeAndGraphics = readCoreLimits ? DecodeVkBool32(limits.TimestampComputeAndGraphics) : null,
                    TimestampPeriod = readCoreLimits ? limits.TimestampPeriod : null,
                    MaxClipDistances = readCoreLimits ? limits.MaxClipDistances : null,
                    MaxCullDistances = readCoreLimits ? limits.MaxCullDistances : null,
                    MaxCombinedClipAndCullDistances = readCoreLimits ? limits.MaxCombinedClipAndCullDistances : null,
                    DiscreteQueuePriorities = readCoreLimits ? limits.DiscreteQueuePriorities : null,
                    PointSizeRange = readCoreLimits ? ImmutableArray.Create(limits.PointSizeRange[0], limits.PointSizeRange[1]) : null,
                    LineWidthRange = readCoreLimits ? ImmutableArray.Create(limits.LineWidthRange[0], limits.LineWidthRange[1]) : null,
                    PointSizeGranularity = readCoreLimits ? limits.PointSizeGranularity : null,
                    LineWidthGranularity = readCoreLimits ? limits.LineWidthGranularity : null,
                    StrictLines = readCoreLimits ? DecodeVkBool32(limits.StrictLines) : null,
                    StandardSampleLocations = readCoreLimits ? DecodeVkBool32(limits.StandardSampleLocations) : null,
                    OptimalBufferCopyOffsetAlignment = readCoreLimits ? limits.OptimalBufferCopyOffsetAlignment : null,
                    OptimalBufferCopyRowPitchAlignment = readCoreLimits ? limits.OptimalBufferCopyRowPitchAlignment : null,
                    NonCoherentAtomSize = readCoreLimits ? limits.NonCoherentAtomSize : null
                };
            }

            return new WindowsVulkanDecodedPhysicalDeviceIdentity(luid, id->DeviceNodeMask,
                apiVersion, vendorId, deviceId, matchedAdapter, coreLimits);
        }
    }

    private static bool DecodeVkBool32(uint value) => value switch
    {
        0 => false,
        1 => true,
        _ => throw new VulkanDataException("Vulkan returned a malformed VkBool32 limit.")
    };

    private static ImmutableArray<string> ReadExtensions(IntPtr physicalDevice,
        VkEnumerateDeviceExtensionPropertiesDelegate enumerate, int maximumCount,
        CancellationToken cancellationToken)
    {
        uint count = 0;
        cancellationToken.ThrowIfCancellationRequested();
        Check(enumerate(physicalDevice, IntPtr.Zero, &count, IntPtr.Zero),
            "vkEnumerateDeviceExtensionProperties(count)");
        cancellationToken.ThrowIfCancellationRequested();
        if (count > (uint)maximumCount) throw new VulkanDataException("Vulkan extension enumeration exceeded its configured bound.");
        if (count == 0) return [];

        var structureSize = sizeof(VkExtensionProperties);
        var buffer = Marshal.AllocHGlobal(checked((int)count * structureSize));
        try
        {
            new Span<byte>((void*)buffer, checked((int)count * structureSize)).Clear();
            cancellationToken.ThrowIfCancellationRequested();
            var returned = count;
            var result = enumerate(physicalDevice, IntPtr.Zero, &returned, buffer);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != VkSuccess || returned != count)
                throw new VulkanDataException("Vulkan extension enumeration changed or was incomplete.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = (VkExtensionProperties*)((byte*)buffer + checked((int)index * structureSize));
                {
                    byte* name = row->ExtensionName;
                    var length = 0;
                    while (length < MaximumExtensionNameBytes && name[length] != 0) length++;
                    if (length == 0 || length == MaximumExtensionNameBytes)
                        throw new VulkanDataException("Vulkan returned a malformed extension name.");
                    string decoded;
                    try { decoded = StrictAscii.GetString(new ReadOnlySpan<byte>(name, length)); }
                    catch (DecoderFallbackException exception)
                    {
                        throw new VulkanDataException("Vulkan returned a non-ASCII extension name.", exception);
                    }
                    if (!IsExtensionName(decoded) || !names.Add(decoded))
                        throw new VulkanDataException("Vulkan returned a malformed or duplicate extension name.");
                }
            }
            return [.. names.OrderBy(static name => name, StringComparer.Ordinal)];
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ImmutableArray<WindowsVulkanFeatureRead> ReadCoreFeatures(IntPtr physicalDevice,
        VkGetPhysicalDeviceFeatures2Delegate getFeatures2, CancellationToken cancellationToken)
    {
        VkPhysicalDeviceFeatures2Buffer* features = stackalloc VkPhysicalDeviceFeatures2Buffer[1];
        *features = default;
        features->SType = VkStructureTypePhysicalDeviceFeatures2;
        byte* featureBytes = features->Features;
        new Span<byte>(featureBytes, 256).Fill(byte.MaxValue);
        cancellationToken.ThrowIfCancellationRequested();
        getFeatures2(physicalDevice, (IntPtr)features);
        cancellationToken.ThrowIfCancellationRequested();

        {
            byte* bytes = features->Features;
            var values = ImmutableArray.CreateBuilder<WindowsVulkanFeatureRead>(WindowsVulkanCore10FeatureCatalog.Names.Length);
            for (var index = 0; index < WindowsVulkanCore10FeatureCatalog.Names.Length; index++)
            {
                var value = *(uint*)(bytes + index * sizeof(uint));
                if (value > 1) throw new VulkanDataException("Vulkan returned a malformed VkBool32 feature value.");
                values.Add(new(WindowsVulkanCore10FeatureCatalog.Names[index], value == 1));
            }
            return values.ToImmutable();
        }
    }

    private static WindowsVulkanAdapterCapabilityRead UnavailableAdapter(WindowsD3dAdapterIdentity adapter)
        => new(adapter, null, null, null);

    private static string? VersionText(uint apiVersion)
    {
        if ((apiVersion >> 29) != 0) return null;
        var major = (apiVersion >> 22) & 0x7F;
        var minor = (apiVersion >> 12) & 0x3FF;
        var patch = apiVersion & 0xFFF;
        return major == 0 ? null : $"{major}.{minor}.{patch}";
    }

    private static bool IsExtensionName(string name)
        => name.Length > 0 && name.All(static character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static T GetDelegate<T>(VkGetInstanceProcAddressDelegate getProcAddress,
        IntPtr instance, string name) where T : Delegate
    {
        var pointer = getProcAddress(instance, name);
        if (pointer == IntPtr.Zero) throw new VulkanCallException($"Vulkan function {name} is unavailable.");
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static void Check(int result, string operation)
    {
        if (result != VkSuccess)
            throw new VulkanCallException($"{operation} failed with VkResult {result}"
                + (result == VkIncomplete ? " (VK_INCOMPLETE)." : "."));
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr VkGetInstanceProcAddressDelegate(IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VkCreateInstanceDelegate(ref VkInstanceCreateInfo createInfo,
        IntPtr allocator, out IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void VkDestroyInstanceDelegate(IntPtr instance, IntPtr allocator);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VkEnumeratePhysicalDevicesDelegate(IntPtr instance, uint* count, IntPtr physicalDevices);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void VkGetPhysicalDeviceProperties2Delegate(IntPtr physicalDevice, IntPtr properties);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VkEnumerateDeviceExtensionPropertiesDelegate(IntPtr physicalDevice,
        IntPtr layerName, uint* count, IntPtr properties);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void VkGetPhysicalDeviceFeatures2Delegate(IntPtr physicalDevice, IntPtr features);

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public uint SType;
        public IntPtr PNext;
        public IntPtr PApplicationName;
        public uint ApplicationVersion;
        public IntPtr PEngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr PApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkPhysicalDeviceIDProperties
    {
        public uint SType;
        public IntPtr PNext;
        public fixed byte DeviceUUID[16];
        public fixed byte DriverUUID[16];
        public fixed byte DeviceLuid[8];
        public uint DeviceNodeMask;
        public uint DeviceLuidValid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkPhysicalDeviceProperties2Buffer
    {
        public uint SType;
        public IntPtr PNext;
        public fixed byte Properties[4096];
    }

    // Vulkan-Headers VkPhysicalDeviceProperties through the complete VkPhysicalDeviceLimits member.
    // Pack=8 reproduces the Windows x64 layout; this output overlay is never passed as input.
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private unsafe struct VkPhysicalDevicePropertiesPrefix
    {
        public uint ApiVersion;
        public uint DriverVersion;
        public uint VendorId;
        public uint DeviceId;
        public uint DeviceType;
        public fixed byte DeviceName[256];
        public fixed byte PipelineCacheUuid[16];
        public VkPhysicalDeviceLimitsLayout Limits;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private unsafe struct VkPhysicalDeviceLimitsLayout
    {
        public uint MaxImageDimension1D;
        public uint MaxImageDimension2D;
        public uint MaxImageDimension3D;
        public uint MaxImageDimensionCube;
        public uint MaxImageArrayLayers;
        public uint MaxTexelBufferElements;
        public uint MaxUniformBufferRange;
        public uint MaxStorageBufferRange;
        public uint MaxPushConstantsSize;
        public uint MaxMemoryAllocationCount;
        public uint MaxSamplerAllocationCount;
        public ulong BufferImageGranularity;
        public ulong SparseAddressSpaceSize;
        public uint MaxBoundDescriptorSets;
        public uint MaxPerStageDescriptorSamplers;
        public uint MaxPerStageDescriptorUniformBuffers;
        public uint MaxPerStageDescriptorStorageBuffers;
        public uint MaxPerStageDescriptorSampledImages;
        public uint MaxPerStageDescriptorStorageImages;
        public uint MaxPerStageDescriptorInputAttachments;
        public uint MaxPerStageResources;
        public uint MaxDescriptorSetSamplers;
        public uint MaxDescriptorSetUniformBuffers;
        public uint MaxDescriptorSetUniformBuffersDynamic;
        public uint MaxDescriptorSetStorageBuffers;
        public uint MaxDescriptorSetStorageBuffersDynamic;
        public uint MaxDescriptorSetSampledImages;
        public uint MaxDescriptorSetStorageImages;
        public uint MaxDescriptorSetInputAttachments;
        public uint MaxVertexInputAttributes;
        public uint MaxVertexInputBindings;
        public uint MaxVertexInputAttributeOffset;
        public uint MaxVertexInputBindingStride;
        public uint MaxVertexOutputComponents;
        public uint MaxTessellationGenerationLevel;
        public uint MaxTessellationPatchSize;
        public uint MaxTessellationControlPerVertexInputComponents;
        public uint MaxTessellationControlPerVertexOutputComponents;
        public uint MaxTessellationControlPerPatchOutputComponents;
        public uint MaxTessellationControlTotalOutputComponents;
        public uint MaxTessellationEvaluationInputComponents;
        public uint MaxTessellationEvaluationOutputComponents;
        public uint MaxGeometryShaderInvocations;
        public uint MaxGeometryInputComponents;
        public uint MaxGeometryOutputComponents;
        public uint MaxGeometryOutputVertices;
        public uint MaxGeometryTotalOutputComponents;
        public uint MaxFragmentInputComponents;
        public uint MaxFragmentOutputAttachments;
        public uint MaxFragmentDualSrcAttachments;
        public uint MaxFragmentCombinedOutputResources;
        public uint MaxComputeSharedMemorySize;
        public fixed uint MaxComputeWorkGroupCount[3];
        public uint MaxComputeWorkGroupInvocations;
        public fixed uint MaxComputeWorkGroupSize[3];
        public uint SubPixelPrecisionBits;
        public uint SubTexelPrecisionBits;
        public uint MipmapPrecisionBits;
        public uint MaxDrawIndexedIndexValue;
        public uint MaxDrawIndirectCount;
        public float MaxSamplerLodBias;
        public float MaxSamplerAnisotropy;
        public uint MaxViewports;
        public fixed uint MaxViewportDimensions[2];
        public fixed float ViewportBoundsRange[2];
        public uint ViewportSubPixelBits;
        public nuint MinMemoryMapAlignment;
        public ulong MinTexelBufferOffsetAlignment;
        public ulong MinUniformBufferOffsetAlignment;
        public ulong MinStorageBufferOffsetAlignment;
        public int MinTexelOffset;
        public uint MaxTexelOffset;
        public int MinTexelGatherOffset;
        public uint MaxTexelGatherOffset;
        public float MinInterpolationOffset;
        public float MaxInterpolationOffset;
        public uint SubPixelInterpolationOffsetBits;
        public uint MaxFramebufferWidth;
        public uint MaxFramebufferHeight;
        public uint MaxFramebufferLayers;
        public uint FramebufferColorSampleCounts;
        public uint FramebufferDepthSampleCounts;
        public uint FramebufferStencilSampleCounts;
        public uint FramebufferNoAttachmentsSampleCounts;
        public uint MaxColorAttachments;
        public uint SampledImageColorSampleCounts;
        public uint SampledImageIntegerSampleCounts;
        public uint SampledImageDepthSampleCounts;
        public uint SampledImageStencilSampleCounts;
        public uint StorageImageSampleCounts;
        public uint MaxSampleMaskWords;
        public uint TimestampComputeAndGraphics;
        public float TimestampPeriod;
        public uint MaxClipDistances;
        public uint MaxCullDistances;
        public uint MaxCombinedClipAndCullDistances;
        public uint DiscreteQueuePriorities;
        public fixed float PointSizeRange[2];
        public fixed float LineWidthRange[2];
        public float PointSizeGranularity;
        public float LineWidthGranularity;
        public uint StrictLines;
        public uint StandardSampleLocations;
        public ulong OptimalBufferCopyOffsetAlignment;
        public ulong OptimalBufferCopyRowPitchAlignment;
        public ulong NonCoherentAtomSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkPhysicalDeviceFeatures2Buffer
    {
        public uint SType;
        public IntPtr PNext;
        public fixed byte Features[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkExtensionProperties
    {
        public fixed byte ExtensionName[MaximumExtensionNameBytes];
        public uint SpecVersion;
    }

    internal readonly record struct WindowsVulkanDecodedPhysicalDeviceIdentity(ulong Luid,
        uint DeviceNodeMask, uint ApiVersion, uint VendorId, uint DeviceId,
        WindowsD3dAdapterIdentity? MatchedAdapter, WindowsVulkanCoreLimitsRead? CoreLimits);

    private readonly record struct PhysicalDeviceObservation(IntPtr Handle, ulong Luid,
        ushort VendorId, ushort DeviceId, uint ApiVersion, WindowsD3dAdapterIdentity? MatchedAdapter,
        WindowsVulkanCoreLimitsRead? CoreLimits);

    private sealed class VulkanDataException(string message, Exception? innerException = null)
        : InvalidOperationException(message, innerException);
    private sealed class VulkanCallException(string message) : InvalidOperationException(message);
}
