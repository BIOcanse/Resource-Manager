using ResourceManager.App.Domain.Metrics;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class GpuApiCapabilitySubscriptionConsumer
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
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        await VerifyOpenGlAsync(owner);
        await VerifyOpenGlLimitsAsync(owner);
        await VerifyVulkanAsync(owner);
        Console.WriteLine("[GPU-API-CAPABILITY-SUBSCRIPTION] role-2 current including opt-in OpenGL limits and Vulkan cube/array/core limits, no-context/unknown unavailable, cancellation without commit, last-demand release, stop and surviving direct owner passed; no GL or Vulkan native read.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task VerifyOpenGlAsync(DeviceTopologyCaptureOwner owner)
    {
        var target = new WindowsOpenGlAdapterTarget(Adapter, 1);
        var scope = new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.All, [target]);
        var reader = new RecordedOpenGlReader { Mode = OpenGlMode.Current };
        var access = owner.CreateOpenGlCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        Require(!catalog.Fields.Any(item => item.Field.FieldName.StartsWith("gpu_all_opengl_max_", StringComparison.Ordinal)),
            "the legacy OpenGL All scope does not include the opt-in limit fields");
        var version = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_opengl_version"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|node-mask:00000001", StringComparison.Ordinal)).Field;
        var extension = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_opengl_extension"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.Contains("|node-mask:00000001", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|extension:GL_EXT_memory_object_win32", StringComparison.Ordinal)).Field;
        Require(reader.Requests.Single() == WindowsOpenGlCapabilityFields.All, "OpenGL initial discovery uses its complete explicit scope");
        reader.Mode = OpenGlMode.NoCurrentContext;

        using var borrow = access.BorrowSubscriptionCapture();
        AssertOriginalBinding(borrow, [version, extension]);
        var failures = new ConcurrentQueue<string>();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => failures.Enqueue($"{failure.Stage}: {failure.Error.Message}"), TimeSpan.FromMilliseconds(10));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = composition.SubscribeAsync("recorded-opengl-api-capabilities", [version, extension],
            TimeSpan.FromSeconds(1), new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token)
            .GetAsyncEnumerator(timeout.Token))
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token), "OpenGL no-context query returns an unavailable reading");
            var unavailable = stream.Current.ToDictionary(static reading => reading.Field);
            foreach (var field in new[] { version, extension })
                Require(unavailable[field].Status == FieldObservationStatus.Unavailable && unavailable[field].Value is null,
                    "missing current context never becomes a known false capability");
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(unavailable[version], access.Backend.ReadCached(version));

            var beforeCancel = owner.ReadState();
            reader.BlockNextReadUntilCanceled();
            var pending = stream.MoveNextAsync().AsTask();
            await reader.BlockEntered.WaitAsync(timeout.Token);
            await composition.StopAsync().WaitAsync(timeout.Token);
            Require(ReferenceEquals(beforeCancel, owner.ReadState()), "canceled OpenGL role-2 read did not commit current");
            Require(!await pending.WaitAsync(timeout.Token), "stopped OpenGL subscription did not deliver a late value");
        }
        Require(failures.IsEmpty, "OpenGL no-context and cancellation are not worker failures");

        reader.Mode = OpenGlMode.Current;
        await composition.StartAsync();
        using (var publication = borrow.Publications.Subscribe([DeviceTopologyCaptureOwner.InformationCollectionItemId]))
        {
            var beforeDemand = reader.ReadCount;
            using (composition.Acquire("last-recorded-opengl-api-demand", [version, extension], TimeSpan.FromMilliseconds(250)))
            using (var demandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await publication.WaitAsync(demandTimeout.Token);
                Require(reader.ReadCount > beforeDemand, "last OpenGL field demand performs a role-2 source query");
                Require(access.Backend.ReadCached(version).Status == FieldObservationStatus.Current
                    && access.Backend.ReadCached(version).Value?.Text == "4.6.0 subscription-recorded",
                    "OpenGL version is committed to the original owner current");
                Require(access.Backend.ReadCached(extension).Status == FieldObservationStatus.Current
                    && access.Backend.ReadCached(extension).Value?.Text == "GL_EXT_memory_object_win32",
                    "OpenGL extension is committed to the original owner current");
            }
        }
        await AssertLastDemandStoppedAsync(reader, failures, composition);
        var direct = (await access.Backend.CollectAsync([version], CancellationToken.None)).Single();
        Require(direct.Status == FieldObservationStatus.Current && access.Backend.ReadCached(version) == direct,
            "the original OpenGL backend remains directly usable after worker stop");
    }

    [SupportedOSPlatform("windows")]
    private static async Task VerifyOpenGlLimitsAsync(DeviceTopologyCaptureOwner owner)
    {
        var target = new WindowsOpenGlAdapterTarget(Adapter, 1);
        var scope = new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.Limits, [target]);
        var reader = new RecordedOpenGlReader { Mode = OpenGlMode.NoCurrentContext };
        var access = owner.CreateOpenGlCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        Require(catalog.Fields.Length == 13 && reader.Requests.SequenceEqual([WindowsOpenGlCapabilityFields.Limits]),
            "the opt-in OpenGL Limits scope exposes its fixed public field group");
        FieldReference Select(string name) => catalog.Fields.Single(item => item.Field.FieldName == name
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|node-mask:00000001", StringComparison.Ordinal)).Field;
        var textureSize = Select("gpu_all_opengl_max_texture_size");
        var lodBias = Select("gpu_all_opengl_max_texture_lod_bias");
        var storageBlockSize = Select("gpu_all_opengl_max_shader_storage_block_size");
        var selected = WindowsOpenGlCapabilityFields.MaxTextureSize
            | WindowsOpenGlCapabilityFields.MaxTextureLodBias
            | WindowsOpenGlCapabilityFields.MaxShaderStorageBlockSize;
        Require(access.Backend.ReadCached(textureSize).Status == FieldObservationStatus.Unavailable
            && access.Backend.ReadCached(textureSize).Value is null,
            "a missing current context publishes a selected OpenGL limit as unavailable with no value");
        var cachedRequestCount = reader.Requests.Count;
        _ = access.Backend.ReadCached(lodBias);
        _ = access.Backend.ReadCached(storageBlockSize);
        Require(reader.Requests.Count == cachedRequestCount, "cached OpenGL limit reads do not sample");

        using var borrow = access.BorrowSubscriptionCapture();
        AssertOriginalBinding(borrow, [textureSize, lodBias, storageBlockSize]);
        var failures = new ConcurrentQueue<string>();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => failures.Enqueue($"{failure.Stage}: {failure.Error.Message}"), TimeSpan.FromMilliseconds(10));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = composition.SubscribeAsync("recorded-opengl-limit-capabilities",
            [textureSize, lodBias, storageBlockSize], TimeSpan.FromSeconds(1),
            new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token),
                "a fresh OpenGL limit subscription returns the no-context result");
            var unavailable = stream.Current.ToDictionary(static reading => reading.Field);
            foreach (var field in new[] { textureSize, lodBias, storageBlockSize })
                Require(unavailable[field].Status == FieldObservationStatus.Unavailable && unavailable[field].Value is null,
                    "an unavailable selected OpenGL limit is not converted to zero or a default");
            Require(reader.Requests.Last() == selected,
                "a fresh OpenGL limit subscription requests only its selected field bits");

            var beforeCancel = owner.ReadState();
            reader.BlockNextReadUntilCanceled();
            var pending = stream.MoveNextAsync().AsTask();
            await reader.BlockEntered.WaitAsync(timeout.Token);
            await composition.StopAsync().WaitAsync(timeout.Token);
            Require(ReferenceEquals(beforeCancel, owner.ReadState()),
                "a canceled OpenGL limit role-2 read does not commit owner current");
            Require(!await pending.WaitAsync(timeout.Token),
                "a stopped OpenGL limit subscription does not deliver a late value");
        }
        Require(failures.IsEmpty, "OpenGL limit unavailability and cancellation do not fault the worker");

        reader.Mode = OpenGlMode.Current;
        var borrowed = await borrow.CollectAsync([lodBias], new Permit(), CancellationToken.None);
        Require(borrowed.Committed && borrowed.Readings.Single().Status == FieldObservationStatus.Current
            && borrowed.Readings.Single().Value?.Number == 16.25d
            && reader.Requests.Last() == WindowsOpenGlCapabilityFields.MaxTextureLodBias,
            "the original owner borrow returns one selected floating OpenGL limit");

        await composition.StartAsync();
        using (var publication = borrow.Publications.Subscribe([DeviceTopologyCaptureOwner.InformationCollectionItemId]))
        {
            var beforeDemand = reader.ReadCount;
            using (composition.Acquire("last-recorded-opengl-limit-demand",
                [textureSize, lodBias, storageBlockSize], TimeSpan.FromMilliseconds(250)))
            using (var demandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await publication.WaitAsync(demandTimeout.Token);
                Require(reader.ReadCount > beforeDemand && reader.Requests.Last() == selected,
                    "the explicit OpenGL limit demand performs a role-2 query for only its selected fields");
                Require(access.Backend.ReadCached(textureSize).Value?.Integer == MetricInteger.Parse("16384")
                    && access.Backend.ReadCached(lodBias).Value?.Number == 16.25d
                    && access.Backend.ReadCached(storageBlockSize).Value?.Integer == MetricInteger.Parse("4294967296"),
                    "subscription current preserves the exact uint32, floating, and lossless decimal unsigned64 limits");
            }
        }
        await AssertLastDemandStoppedAsync(reader, failures, composition);
        var direct = await access.Backend.CollectAsync([storageBlockSize], CancellationToken.None);
        Require(direct.Single().Status == FieldObservationStatus.Current
            && direct.Single().Value?.Integer == MetricInteger.Parse("4294967296")
            && access.Backend.ReadCached(storageBlockSize) == direct.Single(),
            "the original OpenGL limit backend remains directly usable after subscription stop");
        Console.WriteLine("[OPENGL-CORE-LIMITS-SUBSCRIPTION-PACKAGE] opt-in exact-context limits, unavailable nulls, role-2 current, selected query, original-owner borrow, cache and direct-after-stop passed; no GL native read.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task VerifyVulkanAsync(DeviceTopologyCaptureOwner owner)
    {
        var scope = new WindowsVulkanCapabilityScope(
            WindowsVulkanCapabilityFields.All | WindowsVulkanCapabilityFields.CubeArrayLimits
                | WindowsVulkanCapabilityFields.CoreLimits, [Adapter]);
        var reader = new RecordedVulkanReader { Mode = VulkanMode.Current };
        var access = owner.CreateVulkanCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        var version = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_api_version"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var extension = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_extension"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|extension:VK_KHR_swapchain", StringComparison.Ordinal)).Field;
        var trueFeature = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_feature"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|feature:core-1.0:robustBufferAccess", StringComparison.Ordinal)).Field;
        var falseFeature = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_feature"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|feature:core-1.0:fullDrawIndexUint32", StringComparison.Ordinal)).Field;
        var cube = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_max_image_dimension_cube"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var arrayLayers = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_max_image_array_layers"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var texelBufferElements = catalog.Fields.Single(item =>
            item.Field.FieldName == "gpu_all_vulkan_max_texel_buffer_elements"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var selected = WindowsVulkanCapabilityFields.All | WindowsVulkanCapabilityFields.CubeArrayLimits
            | WindowsVulkanCapabilityFields.CoreLimits;
        Require(reader.Requests.Single() == selected, "Vulkan discovery uses its complete explicitly selected scope");
        reader.Mode = VulkanMode.NoPhysicalDevice;

        using var borrow = access.BorrowSubscriptionCapture();
        AssertOriginalBinding(borrow, [version, extension, trueFeature, falseFeature, cube, arrayLayers, texelBufferElements]);
        var failures = new ConcurrentQueue<string>();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => failures.Enqueue($"{failure.Stage}: {failure.Error.Message}"), TimeSpan.FromMilliseconds(10));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = composition.SubscribeAsync("recorded-vulkan-api-capabilities",
            [version, extension, trueFeature, falseFeature, cube, arrayLayers, texelBufferElements], TimeSpan.FromSeconds(1),
            new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token), "Vulkan no-device query returns unavailable readings");
            var unavailable = stream.Current.ToDictionary(static reading => reading.Field);
            foreach (var field in new[] { version, extension, trueFeature, falseFeature, cube, arrayLayers, texelBufferElements })
                Require(unavailable[field].Status == FieldObservationStatus.Unavailable && unavailable[field].Value is null,
                    "missing physical-device query does not turn unknown support into false");

            var beforeCancel = owner.ReadState();
            reader.BlockNextReadUntilCanceled();
            var pending = stream.MoveNextAsync().AsTask();
            await reader.BlockEntered.WaitAsync(timeout.Token);
            await composition.StopAsync().WaitAsync(timeout.Token);
            Require(ReferenceEquals(beforeCancel, owner.ReadState()), "canceled Vulkan role-2 read did not commit current");
            Require(!await pending.WaitAsync(timeout.Token), "stopped Vulkan subscription did not deliver a late value");
        }
        Require(failures.IsEmpty, "Vulkan no-device and cancellation are not worker failures");

        reader.Mode = VulkanMode.Current;
        await composition.StartAsync();
        using (var publication = borrow.Publications.Subscribe([DeviceTopologyCaptureOwner.InformationCollectionItemId]))
        {
            var beforeDemand = reader.ReadCount;
            using (composition.Acquire("last-recorded-vulkan-api-demand",
                [version, extension, trueFeature, falseFeature, cube, arrayLayers, texelBufferElements],
                TimeSpan.FromMilliseconds(250)))
            using (var demandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await publication.WaitAsync(demandTimeout.Token);
                Require(reader.ReadCount > beforeDemand, "last Vulkan field demand performs a role-2 source query");
                Require(access.Backend.ReadCached(version).Status == FieldObservationStatus.Current
                    && access.Backend.ReadCached(version).Value?.Text == "1.3.280",
                    "Vulkan API version is committed to the original owner current");
                Require(access.Backend.ReadCached(extension).Status == FieldObservationStatus.Current
                    && access.Backend.ReadCached(extension).Value?.Text == "VK_KHR_swapchain",
                    "Vulkan extension is committed to the original owner current");
                Require(access.Backend.ReadCached(trueFeature).Status == FieldObservationStatus.Current
                    && access.Backend.ReadCached(trueFeature).Value?.Boolean is true,
                    "reported true feature remains a known true value");
                Require(access.Backend.ReadCached(falseFeature).Status == FieldObservationStatus.Current
                    && access.Backend.ReadCached(falseFeature).Value?.Boolean is false,
                    "reported false feature remains a known false value");
                Require(access.Backend.ReadCached(cube).Value?.Integer == MetricInteger.Parse("8192"),
                    "Vulkan cube limit is delivered from the original owner current");
                Require(access.Backend.ReadCached(arrayLayers).Value?.Integer == MetricInteger.Parse("0"),
                    "Vulkan array-layer limit preserves a known zero in the original owner current");
                Require(access.Backend.ReadCached(texelBufferElements).Value?.Integer == MetricInteger.Parse("131072"),
                    "Vulkan core limit is delivered from the original owner current");
            }
        }
        await AssertLastDemandStoppedAsync(reader, failures, composition);
        var direct = (await access.Backend.CollectAsync([version], CancellationToken.None)).Single();
        Require(direct.Status == FieldObservationStatus.Current && access.Backend.ReadCached(version) == direct,
            "the original Vulkan backend remains directly usable after worker stop");
    }

    private static void AssertOriginalBinding(IReadWriteCollectionBorrow borrow, ImmutableArray<FieldReference> fields)
    {
        foreach (var binding in borrow.Bind(fields))
            Require(binding.SourceKind == FieldCollectionSourceKind.SourceQuery
                && binding.CollectionItemIds.SequenceEqual([DeviceTopologyCaptureOwner.InformationCollectionItemId]),
                "the public role-2 borrow binds the original queryable topology item");
    }

    private static async Task AssertLastDemandStoppedAsync<TReader>(TReader reader,
        ConcurrentQueue<string> failures, NativeFieldSubscriptionComposition composition)
        where TReader : IRecordedReadCounter
    {
        await Task.Delay(TimeSpan.FromMilliseconds(350));
        var countAfterWithdrawal = reader.ReadCount;
        await Task.Delay(TimeSpan.FromMilliseconds(650));
        Require(reader.ReadCount == countAfterWithdrawal, "withdrawing the last explicit field demand stops worker queries");
        await composition.StopAsync();
        Require(failures.IsEmpty, "role-2 capture completed without worker faults");
    }

    private static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
        Generation = 1,
        MaximumSourceCount = 8,
        MaximumItemCount = 8,
        MaximumMembershipCount = 16,
        MaximumDueItemCount = 8,
        MaximumSourceViewCount = 8,
        MaximumExpiredSourceCount = 8,
        DefaultIntervalMilliseconds = 1000,
        MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3000,
        MaximumFutureSkewMilliseconds = 1000
    };

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"GPU API capability subscription consumer failed: {label}.");
    }

    private static ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead> CreateOpenGlLimits(
        WindowsOpenGlCapabilityFields selectedFields)
    {
        var values = ImmutableDictionary.CreateBuilder<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>();
        foreach (var limit in Enum.GetValues<WindowsOpenGlCoreLimit>())
        {
            if ((selectedFields & OpenGlLimitField(limit)) == 0) continue;
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

    private static ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead> CreateUnavailableOpenGlLimits(
        WindowsOpenGlCapabilityFields selectedFields)
    {
        var values = ImmutableDictionary.CreateBuilder<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>();
        foreach (var limit in Enum.GetValues<WindowsOpenGlCoreLimit>())
            if ((selectedFields & OpenGlLimitField(limit)) != 0)
                values.Add(limit, new(WindowsOpenGlLimitReadStatus.Unavailable));
        return values.ToImmutable();
    }

    private static WindowsOpenGlCapabilityFields OpenGlLimitField(WindowsOpenGlCoreLimit limit) => limit switch
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

    private sealed class Permit : ResourceManager.App.Infrastructure.Monitoring.IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }

    private interface IRecordedReadCounter { int ReadCount { get; } }
    private enum OpenGlMode { Current, NoCurrentContext }
    private enum VulkanMode { Current, NoPhysicalDevice }

    private sealed class RecordedOpenGlReader : IWindowsOpenGlCapabilityReader, IRecordedReadCounter
    {
        private readonly ConcurrentQueue<WindowsOpenGlCapabilityFields> requests = new();
        private int mode;
        private int blockNextRead;
        private TaskCompletionSource blockEntered = CompletedSignal();
        private int readCount;
        public OpenGlMode Mode { get => (OpenGlMode)Volatile.Read(ref mode); set => Volatile.Write(ref mode, (int)value); }
        public IReadOnlyList<WindowsOpenGlCapabilityFields> Requests => requests.ToArray();
        public int ReadCount => Volatile.Read(ref readCount);
        public Task BlockEntered => Volatile.Read(ref blockEntered).Task;

        public void BlockNextReadUntilCanceled()
        {
            Volatile.Write(ref blockEntered, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref blockNextRead, 1);
        }

        public WindowsOpenGlCapabilityReadResult Read(WindowsOpenGlCapabilityScope scope,
            WindowsOpenGlCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            Interlocked.Increment(ref readCount);
            BlockIfRequested(cancellationToken);
            var target = scope.Targets.Single();
            if (Mode == OpenGlMode.NoCurrentContext)
                return new(true, [new(target, null, null, null, null,
                    (selectedFields & WindowsOpenGlCapabilityFields.Limits) != 0
                        ? CreateUnavailableOpenGlLimits(selectedFields) : null)]);
            return new(true, [new(target, target.Adapter.Luid, target.NodeMask,
                (selectedFields & WindowsOpenGlCapabilityFields.Version) != 0 ? "4.6.0 subscription-recorded" : null,
                (selectedFields & WindowsOpenGlCapabilityFields.Extensions) != 0
                    ? ImmutableArray.Create("GL_EXT_memory_object", "GL_EXT_memory_object_win32") : null,
                (selectedFields & WindowsOpenGlCapabilityFields.Limits) != 0
                    ? CreateOpenGlLimits(selectedFields) : null)]);
        }

        private void BlockIfRequested(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref blockNextRead, 0) == 0) return;
            Volatile.Read(ref blockEntered).TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The composition did not cancel its recorded OpenGL read.");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class RecordedVulkanReader : IWindowsVulkanCapabilityReader, IRecordedReadCounter
    {
        private readonly ConcurrentQueue<WindowsVulkanCapabilityFields> requests = new();
        private int mode;
        private int blockNextRead;
        private TaskCompletionSource blockEntered = CompletedSignal();
        private int readCount;
        public VulkanMode Mode { get => (VulkanMode)Volatile.Read(ref mode); set => Volatile.Write(ref mode, (int)value); }
        public IReadOnlyList<WindowsVulkanCapabilityFields> Requests => requests.ToArray();
        public int ReadCount => Volatile.Read(ref readCount);
        public Task BlockEntered => Volatile.Read(ref blockEntered).Task;

        public void BlockNextReadUntilCanceled()
        {
            Volatile.Write(ref blockEntered, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref blockNextRead, 1);
        }

        public WindowsVulkanCapabilityReadResult Read(WindowsVulkanCapabilityScope scope,
            WindowsVulkanCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            Interlocked.Increment(ref readCount);
            BlockIfRequested(cancellationToken);
            if (Mode == VulkanMode.NoPhysicalDevice) return new(false, []);
            ImmutableArray<WindowsVulkanFeatureRead>? features = (selectedFields & WindowsVulkanCapabilityFields.CoreFeatures) != 0
                ? ImmutableArray.CreateRange(CoreFeatureNames.Select(static name =>
                    new WindowsVulkanFeatureRead(name, name == "robustBufferAccess")))
                : null;
            return new(true, [new(scope.Adapters.Single(),
                (selectedFields & WindowsVulkanCapabilityFields.ApiVersion) != 0 ? "1.3.280" : null,
                (selectedFields & WindowsVulkanCapabilityFields.Extensions) != 0
                    ? ImmutableArray.Create("VK_KHR_swapchain") : null, features)
            {
                CoreLimits = (selectedFields & (WindowsVulkanCapabilityFields.ImageDimensionLimits
                        | WindowsVulkanCapabilityFields.CubeArrayLimits | WindowsVulkanCapabilityFields.CoreLimits)) != 0
                    ? (selectedFields & WindowsVulkanCapabilityFields.CoreLimits) != 0
                        ? CreateCoreLimits(8192U)
                        : new WindowsVulkanCoreLimitsRead(
                            (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension1D) != 0 ? 12288U : null,
                            (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension2D) != 0 ? 8192U : null,
                            (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension3D) != 0 ? 4096U : null,
                            (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimensionCube) != 0 ? 8192U : null,
                            (selectedFields & WindowsVulkanCapabilityFields.MaxImageArrayLayers) != 0 ? 0U : null)
                    : null
            }]);
        }

        private void BlockIfRequested(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref blockNextRead, 0) == 0) return;
            Volatile.Read(ref blockEntered).TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The composition did not cancel its recorded Vulkan read.");
            cancellationToken.ThrowIfCancellationRequested();
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

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
