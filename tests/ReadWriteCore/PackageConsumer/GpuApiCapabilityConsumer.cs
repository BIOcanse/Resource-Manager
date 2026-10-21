using System.Collections.Concurrent;
using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

internal static class GpuApiCapabilityConsumer
{
    private static readonly WindowsD3dAdapterIdentity Adapter = new(0x0000_0001_0000_002A, 0x10DE, 0x2684);
    private static readonly string[] CoreFeatureNames =
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

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dependencyClient = new HttpClient();
        var dependencies = new DependencyActions(dependencyClient, []);
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var glTarget = new WindowsOpenGlAdapterTarget(Adapter, 1);
        var glScope = new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.All, [glTarget]);
        var glReader = new RecordedOpenGlReader();
        var gl = owner.CreateOpenGlCapabilityInformation(glScope, glReader);
        var glCore = new ReadWriteCore(gl.Backend, dependencies);
        var glCatalog = await glCore.DiscoverAsync(CancellationToken.None);
        var glVersion = glCatalog.Fields.Single(item => item.Field.FieldName == "gpu_all_opengl_version"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|node-mask:00000001", StringComparison.Ordinal)).Field;
        var glExtension = glCatalog.Fields.Single(item => item.Field.FieldName == "gpu_all_opengl_extension"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.Contains("|node-mask:00000001", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|extension:GL_EXT_memory_object_win32", StringComparison.Ordinal)).Field;
        Require(glCore.ReadCached(glVersion).Value?.Text == "4.6.0 package-recorded",
            "the exact-context version is publicly discoverable and cached");
        Require(glCore.ReadCached(glExtension).Value?.Text == "GL_EXT_memory_object_win32",
            "the exact-context extension is publicly discoverable and cached");
        AssertReadOnlyCapability(glCore.Query(glVersion), "OpenGL version");
        AssertReadOnlyCapability(glCore.Query(glExtension), "OpenGL extension");
        Require((await glCore.WriteAsync(glVersion, new(Text: "4.7.0"))).Status == "unsupported",
            "the public API rejects writes to OpenGL capability fields");
        Require(glReader.Requests.SequenceEqual([WindowsOpenGlCapabilityFields.All]),
            "explicit OpenGL discovery queries its selected scope once");
        Require(!glCatalog.Fields.Any(item => item.Field.FieldName.StartsWith("gpu_all_opengl_max_", StringComparison.Ordinal)),
            "the legacy OpenGL All scope does not opt in to implementation limits");

        var glRequestsBeforeCache = glReader.Requests.Count;
        _ = glCore.ReadCached(glVersion);
        _ = glCore.ReadCached(glExtension);
        Require(glReader.Requests.Count == glRequestsBeforeCache, "OpenGL cached reads do not sample");
        var selectedGl = (await glCore.CollectAsync([glVersion], CancellationToken.None)).Single();
        Require(selectedGl.Status == FieldObservationStatus.Current && selectedGl.Value?.Text == "4.6.0 package-recorded",
            "explicit OpenGL version selection returns the current exact-context value");
        Require(glReader.Requests.Last() == WindowsOpenGlCapabilityFields.Version,
            "OpenGL selected collection invokes only the version query");

        glReader.Mode = OpenGlMode.NoCurrentContext;
        var noContext = (await glCore.CollectAsync([glVersion], CancellationToken.None)).Single();
        Require(noContext.Status == FieldObservationStatus.Unavailable && noContext.Value is null,
            "a missing current context remains unavailable");
        glReader.Mode = OpenGlMode.LuidMismatch;
        var wrongAdapter = (await glCore.CollectAsync([glVersion], CancellationToken.None)).Single();
        Require(wrongAdapter.Status == FieldObservationStatus.Unavailable && wrongAdapter.Value is null,
            "a context LUID mismatch remains unavailable rather than borrowing another adapter");

        var glLimitScope = new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.Limits, [glTarget]);
        var glLimitReader = new RecordedOpenGlReader();
        var glLimitAccess = owner.CreateOpenGlCapabilityInformation(glLimitScope, glLimitReader);
        var glLimitCore = new ReadWriteCore(glLimitAccess.Backend, dependencies);
        var glLimitCatalog = await glLimitCore.DiscoverAsync(CancellationToken.None);
        Require(glLimitReader.Requests.SequenceEqual([WindowsOpenGlCapabilityFields.Limits])
            && glLimitCatalog.Fields.Length == 13,
            "an explicit OpenGL Limits scope discovers its finite static group through the original owner");
        FieldReference OpenGlLimitField(string name) => glLimitCatalog.Fields.Single(item =>
            item.Field.FieldName == name
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|node-mask:00000001", StringComparison.Ordinal)).Field;
        var glTextureLimit = OpenGlLimitField("gpu_all_opengl_max_texture_size");
        var glLodBias = OpenGlLimitField("gpu_all_opengl_max_texture_lod_bias");
        var glStorageBlockSize = OpenGlLimitField("gpu_all_opengl_max_shader_storage_block_size");
        var textureCapability = glLimitCore.Query(glTextureLimit);
        var lodBiasCapability = glLimitCore.Query(glLodBias);
        var storageBlockCapability = glLimitCore.Query(glStorageBlockSize);
        Require(textureCapability.Definition.ValueKind == "unsigned32" && textureCapability.Definition.Unit == "pixels"
            && lodBiasCapability.Definition.ValueKind == "float64" && lodBiasCapability.Definition.Unit == "lod-bias"
            && storageBlockCapability.Definition.ValueKind == "unsigned64" && storageBlockCapability.Definition.Unit == "B",
            "OpenGL package metadata preserves unsigned32, float64, and unsigned64 limit types and units");
        AssertReadOnlyCapability(textureCapability, "OpenGL uint32 limit");
        AssertReadOnlyCapability(lodBiasCapability, "OpenGL floating limit");
        AssertReadOnlyCapability(storageBlockCapability, "OpenGL unsigned64 limit");
        Require(glLimitCore.ReadCached(glTextureLimit).Value?.Integer == "16384"
            && glLimitCore.ReadCached(glLodBias).Value?.Number == 16.25d
            && glLimitCore.ReadCached(glStorageBlockSize).Value?.Integer == "4294967296",
            "the original owner publishes exact unsigned32, floating, and lossless decimal unsigned64 values");
        Require((await glLimitCore.WriteAsync(glTextureLimit, new(Integer: "32768"))).Status == "unsupported",
            "the public API rejects writes to OpenGL implementation limits");
        var glLimitRequestsBeforeCache = glLimitReader.Requests.Count;
        _ = glLimitCore.ReadCached(glTextureLimit);
        _ = glLimitCore.ReadCached(glLodBias);
        _ = glLimitCore.ReadCached(glStorageBlockSize);
        Require(glLimitReader.Requests.Count == glLimitRequestsBeforeCache,
            "cached OpenGL implementation-limit reads do not sample");

        var selectedLodBias = (await glLimitCore.CollectAsync([glLodBias], CancellationToken.None)).Single();
        Require(selectedLodBias.Status == FieldObservationStatus.Current && selectedLodBias.Value?.Number == 16.25d
            && glLimitReader.Requests.Last() == WindowsOpenGlCapabilityFields.MaxTextureLodBias,
            "selected OpenGL float collection requests only its exact context limit");
        var selectedStorageBlock = (await glLimitCore.CollectAsync([glStorageBlockSize], CancellationToken.None)).Single();
        Require(selectedStorageBlock.Status == FieldObservationStatus.Current
            && selectedStorageBlock.Value?.Integer == "4294967296"
            && glLimitReader.Requests.Last() == WindowsOpenGlCapabilityFields.MaxShaderStorageBlockSize,
            "selected OpenGL unsigned64 collection preserves its exact decimal value and field bit");
        using (var borrow = glLimitAccess.BorrowSubscriptionCapture())
        {
            var binding = borrow.Bind([glTextureLimit]).Single();
            Require(binding.SourceKind == FieldCollectionSourceKind.SourceQuery
                && binding.CollectionItemIds.SequenceEqual([DeviceTopologyCaptureOwner.InformationCollectionItemId]),
                "OpenGL limits bind to the existing topology query item");
            var borrowed = await borrow.CollectAsync([glTextureLimit], new Permit(), CancellationToken.None);
            Require(borrowed.Committed && borrowed.Readings.Single().Status == FieldObservationStatus.Current
                && borrowed.Readings.Single().Value?.Integer == "16384"
                && glLimitReader.Requests.Last() == WindowsOpenGlCapabilityFields.MaxTextureSize,
                "the original owner borrow returns one selected OpenGL uint32 limit");
        }
        glLimitReader.Mode = OpenGlMode.LuidMismatch;
        var unavailableLimit = (await glLimitCore.CollectAsync([glLodBias], CancellationToken.None)).Single();
        Require(unavailableLimit.Status == FieldObservationStatus.Unavailable && unavailableLimit.Value is null
            && glLimitReader.Requests.Last() == WindowsOpenGlCapabilityFields.MaxTextureLodBias,
            "a selected OpenGL limit with a mismatched context identity remains unavailable with no value");

        var vkScope = new WindowsVulkanCapabilityScope(
            WindowsVulkanCapabilityFields.All | WindowsVulkanCapabilityFields.CubeArrayLimits
                | WindowsVulkanCapabilityFields.CoreLimits, [Adapter]);
        var vkReader = new RecordedVulkanReader();
        var vk = owner.CreateVulkanCapabilityInformation(vkScope, vkReader);
        var vkCore = new ReadWriteCore(vk.Backend, dependencies);
        var vkCatalog = await vkCore.DiscoverAsync(CancellationToken.None);
        var vkVersion = vkCatalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_api_version"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var vkExtension = vkCatalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_extension"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|extension:VK_KHR_swapchain", StringComparison.Ordinal)).Field;
        var vkFeature = vkCatalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_feature"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|feature:core-1.0:robustBufferAccess", StringComparison.Ordinal)).Field;
        var vkMaxImageDimension1D = vkCatalog.Fields.Single(item =>
            item.Field.FieldName == "gpu_all_vulkan_max_image_dimension_1d"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var vkMaxImageDimension2D = vkCatalog.Fields.Single(item =>
            item.Field.FieldName == "gpu_all_vulkan_max_image_dimension_2d"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var vkMaxImageDimension3D = vkCatalog.Fields.Single(item =>
            item.Field.FieldName == "gpu_all_vulkan_max_image_dimension_3d"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var vkMaxImageDimensionCube = vkCatalog.Fields.Single(item =>
            item.Field.FieldName == "gpu_all_vulkan_max_image_dimension_cube"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var vkMaxImageArrayLayers = vkCatalog.Fields.Single(item =>
            item.Field.FieldName == "gpu_all_vulkan_max_image_array_layers"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var vkMaxTexelBufferElements = vkCatalog.Fields.Single(item =>
            item.Field.FieldName == "gpu_all_vulkan_max_texel_buffer_elements"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        Require(vkCore.ReadCached(vkVersion).Value?.Text == "1.3.280",
            "the exact physical-device API version is publicly discoverable");
        Require(vkCore.ReadCached(vkExtension).Value?.Text == "VK_KHR_swapchain",
            "the exact physical-device extension is publicly discoverable");
        var unknownFeature = vkCore.ReadCached(vkFeature);
        Require(unknownFeature.Status == FieldObservationStatus.Unavailable && unknownFeature.Value is null,
            "an unknown feature remains unavailable and is not converted to false");
        AssertReadOnlyCapability(vkCore.Query(vkVersion), "Vulkan API version");
        AssertReadOnlyCapability(vkCore.Query(vkExtension), "Vulkan extension");
        AssertReadOnlyCapability(vkCore.Query(vkFeature), "Vulkan feature");
        foreach (var limit in new[] { vkMaxImageDimension1D, vkMaxImageDimension2D, vkMaxImageDimension3D })
        {
            var capability = vkCore.Query(limit);
            Require(capability.Definition.ValueKind == "unsigned32" && capability.Definition.Unit == "pixels",
                "Vulkan image dimensions retain unsigned32 pixel units at the public API");
            AssertReadOnlyCapability(capability, "Vulkan image dimension limit");
        }
        var cubeLimit = vkCore.Query(vkMaxImageDimensionCube);
        Require(cubeLimit.Definition.ValueKind == "unsigned32" && cubeLimit.Definition.Unit == "pixels",
            "Vulkan cube dimensions retain unsigned32 pixel units at the public API");
        AssertReadOnlyCapability(cubeLimit, "Vulkan cube image dimension limit");
        var arrayLayerLimit = vkCore.Query(vkMaxImageArrayLayers);
        Require(arrayLayerLimit.Definition.ValueKind == "unsigned32" && arrayLayerLimit.Definition.Unit == "layers",
            "Vulkan image-array capacity retains unsigned32 layer units at the public API");
        AssertReadOnlyCapability(arrayLayerLimit, "Vulkan image array layer limit");
        var texelBufferLimit = vkCore.Query(vkMaxTexelBufferElements);
        Require(texelBufferLimit.Definition.ValueKind == "unsigned32" && texelBufferLimit.Definition.Unit == "texels",
            "Vulkan texel-buffer capacity retains unsigned32 texel units at the public API");
        AssertReadOnlyCapability(texelBufferLimit, "Vulkan texel-buffer limit");
        Require(vkCore.ReadCached(vkMaxImageDimension1D).Value?.Integer == "12288"
            && vkCore.ReadCached(vkMaxImageDimension2D).Value?.Integer == "16384"
            && vkCore.ReadCached(vkMaxImageDimension3D).Value?.Integer == "4096"
            && vkCore.ReadCached(vkMaxImageDimensionCube).Value?.Integer == "8192"
            && vkCore.ReadCached(vkMaxImageArrayLayers).Value?.Integer == "0"
            && vkCore.ReadCached(vkMaxTexelBufferElements).Value?.Integer == "131072",
            "the Vulkan image and core-limit groups are publicly discoverable as exact integer values, including a known zero");
        Require((await vkCore.WriteAsync(vkVersion, new(Text: "1.4.0"))).Status == "unsupported",
            "the public API rejects writes to Vulkan capability fields");

        var vkRequestsBeforeCache = vkReader.Requests.Count;
        _ = vkCore.ReadCached(vkVersion);
        _ = vkCore.ReadCached(vkExtension);
        _ = vkCore.ReadCached(vkFeature);
        _ = vkCore.ReadCached(vkMaxImageDimensionCube);
        _ = vkCore.ReadCached(vkMaxImageArrayLayers);
        _ = vkCore.ReadCached(vkMaxTexelBufferElements);
        Require(vkReader.Requests.Count == vkRequestsBeforeCache, "Vulkan cached reads do not sample");
        var selectedVk = (await vkCore.CollectAsync([vkVersion], CancellationToken.None)).Single();
        Require(selectedVk.Status == FieldObservationStatus.Current && selectedVk.Value?.Text == "1.3.280",
            "explicit Vulkan API-version selection returns the current exact-device value");
        Require(vkReader.Requests.Last() == WindowsVulkanCapabilityFields.ApiVersion,
            "Vulkan selected collection invokes only the API-version query");

        var beforeCoreLimitCache = vkReader.Requests.Count;
        _ = vkCore.ReadCached(vkMaxTexelBufferElements);
        Require(vkReader.Requests.Count == beforeCoreLimitCache,
            "cached Vulkan core-limit reads do not resample the physical device");
        var selectedCoreLimit = (await vkCore.CollectAsync([vkMaxTexelBufferElements], CancellationToken.None)).Single();
        Require(selectedCoreLimit.Status == FieldObservationStatus.Current
            && selectedCoreLimit.Value?.Integer == "131072",
            "a selected Vulkan core limit returns its exact current value");
        Require(vkReader.Requests.Last() == WindowsVulkanCapabilityFields.CoreLimits,
            "selected Vulkan core-limit collection invokes the one properties2 core-limits query group");

        var vkRequestsBeforeLimitCache = vkReader.Requests.Count;
        _ = vkCore.ReadCached(vkMaxImageDimension2D);
        Require(vkReader.Requests.Count == vkRequestsBeforeLimitCache,
            "cached Vulkan limit reads do not resample the physical device");
        vkReader.MaxImageDimension2D = 16385;
        var selectedLimit = (await vkCore.CollectAsync([vkMaxImageDimension2D], CancellationToken.None)).Single();
        Require(selectedLimit.Status == FieldObservationStatus.Current
            && selectedLimit.Value?.Integer == "16385",
            "a selected Vulkan 2D limit returns a lossless current unsigned32 value");
        Require(vkReader.Requests.Last() == WindowsVulkanCapabilityFields.MaxImageDimension2D,
            "a selected Vulkan 2D limit requests only its exact limit bit");
        Require(vkCore.ReadCached(vkMaxImageDimension1D).Value?.Integer == "12288"
            && vkCore.ReadCached(vkMaxImageDimension3D).Value?.Integer == "4096"
            && vkCore.ReadCached(vkMaxImageDimensionCube).Value?.Integer == "8192"
            && vkCore.ReadCached(vkMaxImageArrayLayers).Value?.Integer == "0",
            "a selected Vulkan limit leaves sibling limits unchanged");

        using var vkBorrow = vk.BorrowSubscriptionCapture();
        var borrowedLimit = await vkBorrow.CollectAsync([vkMaxImageDimension3D], new Permit(), CancellationToken.None);
        Require(borrowedLimit.Committed && borrowedLimit.Readings.Single().Value?.Integer == "4096",
            "the package subscription borrow delivers the selected Vulkan 3D limit from the original owner");
        Require(vkReader.Requests.Last() == WindowsVulkanCapabilityFields.MaxImageDimension3D,
            "a borrowed Vulkan 3D limit requests only its exact limit bit");
        var borrowedArrayLayers = await vkBorrow.CollectAsync([vkMaxImageArrayLayers], new Permit(), CancellationToken.None);
        Require(borrowedArrayLayers.Committed && borrowedArrayLayers.Readings.Single().Value?.Integer == "0",
            "the package subscription borrow preserves a known zero array-layer limit from the original owner");
        Require(vkReader.Requests.Last() == WindowsVulkanCapabilityFields.MaxImageArrayLayers,
            "a borrowed Vulkan array-layer limit requests only its exact limit bit");
        var borrowedCoreLimit = await vkBorrow.CollectAsync([vkMaxTexelBufferElements], new Permit(), CancellationToken.None);
        Require(borrowedCoreLimit.Committed && borrowedCoreLimit.Readings.Single().Value?.Integer == "131072",
            "the package subscription borrow delivers a Vulkan core limit from the original owner");
        Require(vkReader.Requests.Last() == WindowsVulkanCapabilityFields.CoreLimits,
            "a borrowed Vulkan core-limit query uses the original one-call properties2 group");

        Console.WriteLine("[GPU-API-CAPABILITY-PACKAGE] fields discovered; reads supported and dependency-free; writes unsupported; cache, selected identity and unavailable context/mismatch/unknown passed; no GL or Vulkan native read.");
        Console.WriteLine("[VULKAN-CORE-LIMITS-PACKAGE] image and full core-limits groups discovered with typed values; known zero, cached read, exact selected query and original-owner borrow passed; no Vulkan native read.");
    }

    private static void AssertReadOnlyCapability(FieldCapability capability, string label)
    {
        Require(capability.Read.Supported && capability.Read.Dependencies.IsEmpty,
            $"{label} is readable without an automatic install dependency");
        Require(!capability.Write.Supported, $"{label} is read-only");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"GPU API capability package consumer failed: {label}.");
    }

    private enum OpenGlMode { Current, NoCurrentContext, LuidMismatch }

    private sealed class RecordedOpenGlReader : IWindowsOpenGlCapabilityReader
    {
        private readonly ConcurrentQueue<WindowsOpenGlCapabilityFields> requests = new();
        public OpenGlMode Mode { get; set; }
        public IReadOnlyList<WindowsOpenGlCapabilityFields> Requests => requests.ToArray();

        public WindowsOpenGlCapabilityReadResult Read(WindowsOpenGlCapabilityScope scope,
            WindowsOpenGlCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            var target = scope.Targets.Single();
            if (Mode == OpenGlMode.NoCurrentContext)
                return new(true, [new(target, null, null, null, null,
                    (selectedFields & WindowsOpenGlCapabilityFields.Limits) != 0
                        ? CreateUnavailableLimits(selectedFields) : null)]);
            var matches = Mode != OpenGlMode.LuidMismatch;
            return new(true, [new(target, matches ? target.Adapter.Luid : target.Adapter.Luid + 1,
                target.NodeMask,
                matches && (selectedFields & WindowsOpenGlCapabilityFields.Version) != 0 ? "4.6.0 package-recorded" : null,
                matches && (selectedFields & WindowsOpenGlCapabilityFields.Extensions) != 0
                    ? ImmutableArray.Create("GL_EXT_memory_object", "GL_EXT_memory_object_win32") : null,
                (selectedFields & WindowsOpenGlCapabilityFields.Limits) != 0
                    ? matches ? CreateLimits(selectedFields) : CreateUnavailableLimits(selectedFields)
                    : null)]);
        }

        private static ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead> CreateLimits(
            WindowsOpenGlCapabilityFields selectedFields)
        {
            var values = ImmutableDictionary.CreateBuilder<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>();
            foreach (var limit in Enum.GetValues<WindowsOpenGlCoreLimit>())
            {
                var field = LimitField(limit);
                if ((selectedFields & field) == 0) continue;
                values.Add(limit, limit switch
                {
                    WindowsOpenGlCoreLimit.MaxTextureSize => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 16384),
                    WindowsOpenGlCoreLimit.Max3DTextureSize => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 2048),
                    WindowsOpenGlCoreLimit.MaxCubeMapTextureSize => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 16384),
                    WindowsOpenGlCoreLimit.MaxTextureLodBias => new(WindowsOpenGlLimitReadStatus.Queried, NumberValue: 16.25),
                    WindowsOpenGlCoreLimit.MaxVertexAttribs => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 16),
                    WindowsOpenGlCoreLimit.MaxTextureImageUnits => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 32),
                    WindowsOpenGlCoreLimit.MaxCombinedTextureImageUnits => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 192),
                    WindowsOpenGlCoreLimit.MaxDrawBuffers => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 8),
                    WindowsOpenGlCoreLimit.MaxRenderbufferSize => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 16384),
                    WindowsOpenGlCoreLimit.MaxArrayTextureLayers => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 2048),
                    WindowsOpenGlCoreLimit.MaxUniformBufferBindings => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 90),
                    WindowsOpenGlCoreLimit.MaxUniformBlockSize => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 65536),
                    WindowsOpenGlCoreLimit.MaxShaderStorageBlockSize => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned64Value: 4294967296),
                    _ => throw new ArgumentOutOfRangeException(nameof(limit))
                });
            }
            return values.ToImmutable();
        }

        private static ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead> CreateUnavailableLimits(
            WindowsOpenGlCapabilityFields selectedFields)
        {
            var values = ImmutableDictionary.CreateBuilder<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>();
            foreach (var limit in Enum.GetValues<WindowsOpenGlCoreLimit>())
                if ((selectedFields & LimitField(limit)) != 0)
                    values.Add(limit, new(WindowsOpenGlLimitReadStatus.Unavailable));
            return values.ToImmutable();
        }

        private static WindowsOpenGlCapabilityFields LimitField(WindowsOpenGlCoreLimit limit) => limit switch
        {
            WindowsOpenGlCoreLimit.MaxTextureSize => WindowsOpenGlCapabilityFields.MaxTextureSize,
            WindowsOpenGlCoreLimit.Max3DTextureSize => WindowsOpenGlCapabilityFields.Max3DTextureSize,
            WindowsOpenGlCoreLimit.MaxCubeMapTextureSize => WindowsOpenGlCapabilityFields.MaxCubeMapTextureSize,
            WindowsOpenGlCoreLimit.MaxTextureLodBias => WindowsOpenGlCapabilityFields.MaxTextureLodBias,
            WindowsOpenGlCoreLimit.MaxVertexAttribs => WindowsOpenGlCapabilityFields.MaxVertexAttribs,
            WindowsOpenGlCoreLimit.MaxTextureImageUnits => WindowsOpenGlCapabilityFields.MaxTextureImageUnits,
            WindowsOpenGlCoreLimit.MaxCombinedTextureImageUnits => WindowsOpenGlCapabilityFields.MaxCombinedTextureImageUnits,
            WindowsOpenGlCoreLimit.MaxDrawBuffers => WindowsOpenGlCapabilityFields.MaxDrawBuffers,
            WindowsOpenGlCoreLimit.MaxRenderbufferSize => WindowsOpenGlCapabilityFields.MaxRenderbufferSize,
            WindowsOpenGlCoreLimit.MaxArrayTextureLayers => WindowsOpenGlCapabilityFields.MaxArrayTextureLayers,
            WindowsOpenGlCoreLimit.MaxUniformBufferBindings => WindowsOpenGlCapabilityFields.MaxUniformBufferBindings,
            WindowsOpenGlCoreLimit.MaxUniformBlockSize => WindowsOpenGlCapabilityFields.MaxUniformBlockSize,
            WindowsOpenGlCoreLimit.MaxShaderStorageBlockSize => WindowsOpenGlCapabilityFields.MaxShaderStorageBlockSize,
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
    }

    private sealed class RecordedVulkanReader : IWindowsVulkanCapabilityReader
    {
        private readonly ConcurrentQueue<WindowsVulkanCapabilityFields> requests = new();
        public IReadOnlyList<WindowsVulkanCapabilityFields> Requests => requests.ToArray();
        public uint MaxImageDimension2D { get; set; } = 16384;

        public WindowsVulkanCapabilityReadResult Read(WindowsVulkanCapabilityScope scope,
            WindowsVulkanCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            var coreLimits = (selectedFields & (WindowsVulkanCapabilityFields.ImageDimensionLimits
                    | WindowsVulkanCapabilityFields.CubeArrayLimits | WindowsVulkanCapabilityFields.CoreLimits)) != 0
                ? (selectedFields & WindowsVulkanCapabilityFields.CoreLimits) != 0
                    ? CreateCoreLimits(MaxImageDimension2D)
                    : new WindowsVulkanCoreLimitsRead(
                        (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension1D) != 0 ? 12288U : null,
                        (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension2D) != 0 ? MaxImageDimension2D : null,
                        (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension3D) != 0 ? 4096U : null,
                        (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimensionCube) != 0 ? 8192U : null,
                        (selectedFields & WindowsVulkanCapabilityFields.MaxImageArrayLayers) != 0 ? 0U : null)
                : null;
            ImmutableArray<WindowsVulkanFeatureRead>? features = (selectedFields & WindowsVulkanCapabilityFields.CoreFeatures) != 0
                ? ImmutableArray.CreateRange(CoreFeatureNames.Select(static name =>
                    new WindowsVulkanFeatureRead(name, null)))
                : null;
            return new(true, [new(scope.Adapters.Single(),
                (selectedFields & WindowsVulkanCapabilityFields.ApiVersion) != 0 ? "1.3.280" : null,
                (selectedFields & WindowsVulkanCapabilityFields.Extensions) != 0
                    ? ImmutableArray.Create("VK_KHR_swapchain") : null,
                features) { CoreLimits = coreLimits }]);
        }
    }

    private static WindowsVulkanCoreLimitsRead CreateCoreLimits(uint maxImageDimension2D) =>
        new(12288U, maxImageDimension2D, 4096U, 8192U, 0U)
        {
            MaxTexelBufferElements = 131072U,
            MaxUniformBufferRange = 1U,
            MaxStorageBufferRange = 1U,
            MaxPushConstantsSize = 1U,
            MaxMemoryAllocationCount = 1U,
            MaxSamplerAllocationCount = 1U,
            BufferImageGranularity = 4096UL,
            SparseAddressSpaceSize = ulong.MaxValue - 1,
            MaxBoundDescriptorSets = 1U,
            MaxPerStageDescriptorSamplers = 1U,
            MaxPerStageDescriptorUniformBuffers = 1U,
            MaxPerStageDescriptorStorageBuffers = 1U,
            MaxPerStageDescriptorSampledImages = 1U,
            MaxPerStageDescriptorStorageImages = 1U,
            MaxPerStageDescriptorInputAttachments = 1U,
            MaxPerStageResources = 1U,
            MaxDescriptorSetSamplers = 1U,
            MaxDescriptorSetUniformBuffers = 1U,
            MaxDescriptorSetUniformBuffersDynamic = 1U,
            MaxDescriptorSetStorageBuffers = 1U,
            MaxDescriptorSetStorageBuffersDynamic = 1U,
            MaxDescriptorSetSampledImages = 1U,
            MaxDescriptorSetStorageImages = 1U,
            MaxDescriptorSetInputAttachments = 1U,
            MaxVertexInputAttributes = 1U,
            MaxVertexInputBindings = 1U,
            MaxVertexInputAttributeOffset = 1U,
            MaxVertexInputBindingStride = 1U,
            MaxVertexOutputComponents = 1U,
            MaxTessellationGenerationLevel = 1U,
            MaxTessellationPatchSize = 1U,
            MaxTessellationControlPerVertexInputComponents = 1U,
            MaxTessellationControlPerVertexOutputComponents = 1U,
            MaxTessellationControlPerPatchOutputComponents = 1U,
            MaxTessellationControlTotalOutputComponents = 1U,
            MaxTessellationEvaluationInputComponents = 1U,
            MaxTessellationEvaluationOutputComponents = 1U,
            MaxGeometryShaderInvocations = 1U,
            MaxGeometryInputComponents = 1U,
            MaxGeometryOutputComponents = 1U,
            MaxGeometryOutputVertices = 1U,
            MaxGeometryTotalOutputComponents = 1U,
            MaxFragmentInputComponents = 1U,
            MaxFragmentOutputAttachments = 1U,
            MaxFragmentDualSrcAttachments = 1U,
            MaxFragmentCombinedOutputResources = 1U,
            MaxComputeSharedMemorySize = 1U,
            MaxComputeWorkGroupCount = ImmutableArray.Create(64U, 64U, 64U),
            MaxComputeWorkGroupInvocations = 1U,
            MaxComputeWorkGroupSize = ImmutableArray.Create(1024U, 1024U, 64U),
            SubPixelPrecisionBits = 1U,
            SubTexelPrecisionBits = 1U,
            MipmapPrecisionBits = 1U,
            MaxDrawIndexedIndexValue = 1U,
            MaxDrawIndirectCount = 1U,
            MaxSamplerLodBias = 1.25F,
            MaxSamplerAnisotropy = 1.25F,
            MaxViewports = 1U,
            MaxViewportDimensions = ImmutableArray.Create(16384U, 16384U),
            ViewportBoundsRange = ImmutableArray.Create(1.25F, 1.25F),
            ViewportSubPixelBits = 1U,
            MinMemoryMapAlignment = (nuint)64,
            MinTexelBufferOffsetAlignment = 4096UL,
            MinUniformBufferOffsetAlignment = 4096UL,
            MinStorageBufferOffsetAlignment = 4096UL,
            MinTexelOffset = -17,
            MaxTexelOffset = 1U,
            MinTexelGatherOffset = -7,
            MaxTexelGatherOffset = 1U,
            MinInterpolationOffset = 1.25F,
            MaxInterpolationOffset = 1.25F,
            SubPixelInterpolationOffsetBits = 1U,
            MaxFramebufferWidth = 1U,
            MaxFramebufferHeight = 1U,
            MaxFramebufferLayers = 1U,
            FramebufferColorSampleCounts = 1U,
            FramebufferDepthSampleCounts = 1U,
            FramebufferStencilSampleCounts = 1U,
            FramebufferNoAttachmentsSampleCounts = 1U,
            MaxColorAttachments = 1U,
            SampledImageColorSampleCounts = 1U,
            SampledImageIntegerSampleCounts = 1U,
            SampledImageDepthSampleCounts = 1U,
            SampledImageStencilSampleCounts = 1U,
            StorageImageSampleCounts = 1U,
            MaxSampleMaskWords = 1U,
            TimestampComputeAndGraphics = true,
            TimestampPeriod = 1.25F,
            MaxClipDistances = 1U,
            MaxCullDistances = 1U,
            MaxCombinedClipAndCullDistances = 1U,
            DiscreteQueuePriorities = 1U,
            PointSizeRange = ImmutableArray.Create(1.25F, 1.25F),
            LineWidthRange = ImmutableArray.Create(1.25F, 1.25F),
            PointSizeGranularity = 1.25F,
            LineWidthGranularity = 1.25F,
            StrictLines = true,
            StandardSampleLocations = true,
            OptimalBufferCopyOffsetAlignment = 4096UL,
            OptimalBufferCopyRowPitchAlignment = 4096UL,
            NonCoherentAtomSize = 4096UL
        };

    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
}
