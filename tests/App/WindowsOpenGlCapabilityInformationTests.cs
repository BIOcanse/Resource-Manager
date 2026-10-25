using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsOpenGlCapabilityInformationTests
{
    private static readonly WindowsD3dAdapterIdentity FirstAdapter = new(0x0000_0001_0000_002A, 0x10DE, 0x2684);
    private static readonly WindowsD3dAdapterIdentity SecondAdapter = new(0x0000_0002_0000_003B, 0x8086, 0x46A6);
    private static readonly WindowsOpenGlAdapterTarget FirstNode = new(FirstAdapter, 1);
    private static readonly WindowsOpenGlAdapterTarget SecondNode = new(FirstAdapter, 2);
    private static readonly WindowsOpenGlAdapterTarget OtherAdapterNode = new(SecondAdapter, 1);

    [Fact]
    public async Task CurrentContextCapabilitiesUseExactLuidAndWholeNodeMaskThroughOriginalOwner()
    {
        var scope = new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.All,
            [FirstNode, SecondNode, OtherAdapterNode]);
        var reader = new RecordedReader();
        using var owner = EmptyOwner();
        var access = owner.CreateOpenGlCapabilityInformation(scope, reader);

        Assert.Empty(access.Backend.Catalog.Fields);
        Assert.Empty(reader.Requests);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(WindowsOpenGlCapabilityFields.All, Assert.Single(reader.Requests));

        var firstVersion = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_opengl_version"
            && field.Field.DeviceIdentity.Contains($"luid:{FirstAdapter.Luid:X16}", StringComparison.Ordinal)
            && field.Field.DeviceIdentity.EndsWith("|node-mask:00000001", StringComparison.Ordinal)).Field;
        var secondNodeVersion = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_opengl_version"
            && field.Field.DeviceIdentity.Contains($"luid:{FirstAdapter.Luid:X16}", StringComparison.Ordinal)
            && field.Field.DeviceIdentity.EndsWith("|node-mask:00000002", StringComparison.Ordinal)).Field;
        var otherAdapterVersion = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_opengl_version"
            && field.Field.DeviceIdentity.Contains($"luid:{SecondAdapter.Luid:X16}", StringComparison.Ordinal)
            && field.Field.DeviceIdentity.EndsWith("|node-mask:00000001", StringComparison.Ordinal)).Field;
        Assert.Equal("4.6.0 NVIDIA 561.09", access.Backend.ReadCached(firstVersion).Value?.Text);
        Assert.Equal("4.5.0 Mesa 24.1", access.Backend.ReadCached(secondNodeVersion).Value?.Text);
        Assert.Equal(FieldObservationStatus.Unavailable, access.Backend.ReadCached(otherAdapterVersion).Status);

        var firstExtensions = catalog.Fields.Where(field => field.Field.FieldName == "gpu_all_opengl_extension"
            && field.Field.DeviceIdentity.Contains($"luid:{FirstAdapter.Luid:X16}", StringComparison.Ordinal)
            && field.Field.DeviceIdentity.Contains("|node-mask:00000001|", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, firstExtensions.Length);
        var memoryObjectWin32 = firstExtensions.Single(field => field.Field.DeviceIdentity.EndsWith(
            "|extension:GL_EXT_memory_object_win32", StringComparison.Ordinal)).Field;
        Assert.Equal("GL_EXT_memory_object_win32", access.Backend.ReadCached(memoryObjectWin32).Value?.Text);
        Assert.All(firstExtensions, field => Assert.Contains(catalog.Devices,
            device => device.Identity == field.Field.DeviceIdentity && device.Resource == "gpu_adapter_extension"));

        reader.Version = "4.6.1 NVIDIA 561.09";
        var changed = Assert.Single(await access.Backend.CollectAsync([firstVersion], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Current, changed.Status);
        Assert.Equal("4.6.1 NVIDIA 561.09", changed.Value?.Text);
        Assert.Equal(WindowsOpenGlCapabilityFields.Version, reader.Requests.Last());
        Assert.Equal("GL_EXT_memory_object_win32", access.Backend.ReadCached(memoryObjectWin32).Value?.Text);

        using var borrow = access.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([memoryObjectWin32]));
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId, Assert.Single(binding.CollectionItemIds));
        var borrowed = await borrow.CollectAsync([memoryObjectWin32], new Permit(), CancellationToken.None);
        Assert.True(borrowed.Committed);
        Assert.Equal(FieldObservationStatus.Current, Assert.Single(borrowed.Readings).Status);
        Assert.Equal(WindowsOpenGlCapabilityFields.Extensions, reader.Requests.Last());
        Assert.NotNull(owner.ReadState().InformationFields.FindField(memoryObjectWin32));

        await owner.SampleTopologyAsync(null, CancellationToken.None, requireDemand: false);
        Assert.Equal("4.6.1 NVIDIA 561.09", access.Backend.ReadCached(firstVersion).Value?.Text);
        Assert.Equal("GL_EXT_memory_object_win32", access.Backend.ReadCached(memoryObjectWin32).Value?.Text);
    }

    [Fact]
    public async Task MissingAssociationOrDifferentLuidOrNodeMaskReturnsUnavailableAndOnlyClearsSelectedFields()
    {
        var scope = new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.All, [FirstNode]);
        var reader = new RecordedReader();
        using var owner = EmptyOwner();
        var access = owner.CreateOpenGlCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        var version = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_opengl_version"
            && field.Field.DeviceIdentity.Contains($"luid:{FirstAdapter.Luid:X16}", StringComparison.Ordinal)
            && field.Field.DeviceIdentity.EndsWith("|node-mask:00000001", StringComparison.Ordinal)).Field;
        var extension = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_opengl_extension"
            && field.Field.DeviceIdentity.Contains($"luid:{FirstAdapter.Luid:X16}", StringComparison.Ordinal)
            && field.Field.DeviceIdentity.Contains("|node-mask:00000001", StringComparison.Ordinal)
            && field.Field.DeviceIdentity.EndsWith("|extension:GL_EXT_memory_object_win32", StringComparison.Ordinal)).Field;
        var extensionBefore = access.Backend.ReadCached(extension);

        reader.Mode = RecordedMode.LuidMismatch;
        var wrongLuid = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, wrongLuid.Status);
        Assert.Null(wrongLuid.Value);
        Assert.Equal(extensionBefore, access.Backend.ReadCached(extension));
        var versionAfterLuidMismatch = access.Backend.ReadCached(version);

        reader.Mode = RecordedMode.NodeMaskMismatch;
        var wrongNode = Assert.Single(await access.Backend.CollectAsync([extension], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, wrongNode.Status);
        Assert.Null(wrongNode.Value);
        Assert.Equal(versionAfterLuidMismatch, access.Backend.ReadCached(version));

        reader.Mode = RecordedMode.DuplicateTarget;
        var duplicate = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, duplicate.Status);
        Assert.Null(duplicate.Value);

        reader.Mode = RecordedMode.ForeignTarget;
        var foreign = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, foreign.Status);
        Assert.Null(foreign.Value);

        reader.Mode = RecordedMode.IdentityUnavailable;
        var noProof = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, noProof.Status);
        Assert.Null(noProof.Value);

        reader.Mode = RecordedMode.Current;
        var readCount = reader.Requests.Count;
        Assert.Throws<ArgumentException>(() => access.Backend.ReadCached(extension));
        Assert.Equal(readCount, reader.Requests.Count);

        using var borrow = access.BorrowSubscriptionCapture();
        var beforeRejected = owner.ReadState();
        var rejected = await borrow.CollectAsync([version], new Permit(accept: false), CancellationToken.None);
        Assert.False(rejected.Committed);
        Assert.Same(beforeRejected, owner.ReadState());

        using var canceled = new CancellationTokenSource();
        reader.AfterRead = canceled.Cancel;
        beforeRejected = owner.ReadState();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await access.Backend.CollectAsync([version], canceled.Token));
        Assert.Same(beforeRejected, owner.ReadState());
        reader.AfterRead = null;
    }

    [Fact]
    public void ScopeBoundsAndContextTargetIdentityRejectAmbiguousMappings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsOpenGlCapabilityScope(
            WindowsOpenGlCapabilityFields.None, [FirstNode]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsOpenGlCapabilityScope(
            (WindowsOpenGlCapabilityFields)32768, [FirstNode]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsOpenGlCapabilityScope(
            WindowsOpenGlCapabilityFields.Version, [FirstNode], maximumExtensionCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsOpenGlCapabilityScope(
            WindowsOpenGlCapabilityFields.Version, [FirstNode], maximumAggregateTextBytes: 0));
        Assert.Throws<ArgumentException>(() => new WindowsOpenGlCapabilityScope(
            WindowsOpenGlCapabilityFields.Version, [FirstNode, FirstNode]));
        Assert.Throws<ArgumentException>(() => new WindowsOpenGlCapabilityScope(
            WindowsOpenGlCapabilityFields.Version,
            [FirstNode, new(new WindowsD3dAdapterIdentity(FirstAdapter.Luid, 0x8086, 0x1234), 1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsOpenGlAdapterTarget(FirstAdapter, 0));

        var scope = new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.Extensions,
            [FirstNode, SecondNode], maximumTargetCount: 2, maximumExtensionCount: 8192);
        Assert.Equal(2, scope.Targets.Length);
        Assert.Equal(8192, scope.MaximumExtensionCount);
    }

    [Fact]
    public async Task ExplicitCoreLimitsAreStaticOptInAndRemainBoundToTheExactContext()
    {
        using var owner = EmptyOwner();
        var versionAndExtensions = owner.CreateOpenGlCapabilityInformation(
            new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.All, [FirstNode]), new RecordedReader());
        var legacyCatalog = await versionAndExtensions.Backend.DiscoverAsync(CancellationToken.None);
        Assert.DoesNotContain(legacyCatalog.Fields, field => field.Field.FieldName.StartsWith(
            "gpu_all_opengl_max_", StringComparison.Ordinal));

        var reader = new RecordedReader();
        var scope = new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.Limits, [FirstNode]);
        var access = owner.CreateOpenGlCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        var fields = catalog.Fields.OrderBy(static field => field.Field.FieldName, StringComparer.Ordinal).ToArray();
        Assert.Equal(13, fields.Length);
        Assert.Equal(WindowsOpenGlCapabilityFields.Limits, Assert.Single(reader.Requests));
        Assert.Equal(MetricInteger.Parse("16384"), access.Backend.ReadCached(fields.Single(field =>
            field.Field.FieldName == "gpu_all_opengl_max_texture_size").Field).Value?.Integer);
        Assert.Equal(MetricInteger.Parse("4294967296"), access.Backend.ReadCached(fields.Single(field =>
            field.Field.FieldName == "gpu_all_opengl_max_shader_storage_block_size").Field).Value?.Integer);

        var textureSize = fields.Single(field => field.Field.FieldName == "gpu_all_opengl_max_texture_size").Field;
        var cachedReadCount = reader.Requests.Count;
        var unchanged = access.Backend.ReadCached(textureSize);
        Assert.Equal(cachedReadCount, reader.Requests.Count);
        using (var borrow = access.BorrowSubscriptionCapture())
        {
            var binding = Assert.Single(borrow.Bind([textureSize]));
            Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId,
                Assert.Single(binding.CollectionItemIds));
            var borrowed = await borrow.CollectAsync([textureSize], new Permit(), CancellationToken.None);
            Assert.True(borrowed.Committed);
            Assert.Equal(FieldObservationStatus.Current, Assert.Single(borrowed.Readings).Status);
            Assert.Equal(WindowsOpenGlCapabilityFields.MaxTextureSize, reader.Requests.Last());
        }
        var beforeTopologyRefresh = access.Backend.ReadCached(textureSize);
        await owner.SampleTopologyAsync(null, CancellationToken.None, requireDemand: false);
        Assert.Equal(beforeTopologyRefresh, access.Backend.ReadCached(textureSize));

        reader.LimitOverrides[WindowsOpenGlCoreLimit.Max3DTextureSize]
            = new(WindowsOpenGlLimitReadStatus.Unsupported);
        var unsupportedField = fields.Single(field => field.Field.FieldName == "gpu_all_opengl_max_3d_texture_size").Field;
        var unsupported = Assert.Single(await access.Backend.CollectAsync([unsupportedField], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, unsupported.Status);
        Assert.Equal(WindowsOpenGlCapabilityFields.Max3DTextureSize, reader.Requests.Last());
        Assert.Equal(beforeTopologyRefresh, access.Backend.ReadCached(textureSize));

        reader.Mode = RecordedMode.LuidMismatch;
        var unavailable = Assert.Single(await access.Backend.CollectAsync([textureSize], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Value);
    }

    [Fact]
    public async Task SelectedLimitsRemainDiscoverableAsUnavailableWithoutAnExactCurrentContext()
    {
        using var owner = EmptyOwner();
        var reader = new RecordedReader { Mode = RecordedMode.LuidMismatch };
        var access = owner.CreateOpenGlCapabilityInformation(
            new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.Limits, [FirstNode]), reader);

        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(WindowsOpenGlCapabilityFields.Limits, Assert.Single(reader.Requests));
        Assert.Equal(13, catalog.Fields.Length);
        Assert.All(catalog.Fields, capability =>
        {
            var reading = access.Backend.ReadCached(capability.Field);
            Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
            Assert.Null(reading.Value);
        });
    }

    [Fact]
    public void CoreLimitPrerequisitesAreFiniteAndUnsupportedIsDifferentFromUnavailable()
    {
        Assert.Equal(WindowsOpenGlLimitReadStatus.Unsupported,
            new WindowsOpenGlLimitRead(WindowsOpenGlLimitReadStatus.Unsupported).Status);
        Assert.Equal(WindowsOpenGlLimitReadStatus.Unavailable,
            new WindowsOpenGlLimitRead(WindowsOpenGlLimitReadStatus.Unavailable).Status);
        Assert.True(WindowsOpenGlCoreLimitsCatalog.IsSupported(WindowsOpenGlCoreLimit.MaxUniformBlockSize,
            3, 1, []));
        Assert.True(WindowsOpenGlCoreLimitsCatalog.IsSupported(WindowsOpenGlCoreLimit.MaxUniformBlockSize,
            2, 1, ["GL_ARB_uniform_buffer_object"]));
        Assert.False(WindowsOpenGlCoreLimitsCatalog.IsSupported(WindowsOpenGlCoreLimit.MaxShaderStorageBlockSize,
            4, 2, []));
        Assert.True(WindowsOpenGlCoreLimitsCatalog.IsSupported(WindowsOpenGlCoreLimit.MaxShaderStorageBlockSize,
            4, 2, ["GL_ARB_shader_storage_buffer_object"]));
        Assert.True(WindowsOpenGlCoreLimitsCatalog.IsValidRead(WindowsOpenGlCoreLimit.MaxTextureSize,
            new(WindowsOpenGlLimitReadStatus.Unsupported)));
        Assert.True(WindowsOpenGlCoreLimitsCatalog.IsValidRead(WindowsOpenGlCoreLimit.MaxTextureSize,
            new(WindowsOpenGlLimitReadStatus.Unavailable)));
        Assert.False(WindowsOpenGlCoreLimitsCatalog.IsValidRead(WindowsOpenGlCoreLimit.MaxTextureSize,
            new(WindowsOpenGlLimitReadStatus.Unsupported, Unsigned32Value: 1)));
        Assert.False(WindowsOpenGlCoreLimitsCatalog.IsValidRead(WindowsOpenGlCoreLimit.MaxShaderStorageBlockSize,
            new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 42)));
        Assert.False(WindowsOpenGlCoreLimitsCatalog.IsValidRead(WindowsOpenGlCoreLimit.MaxTextureLodBias,
            new(WindowsOpenGlLimitReadStatus.Queried, NumberValue: double.NaN)));
        Assert.False(WindowsOpenGlCoreLimitsCatalog.IsValidRead(WindowsOpenGlCoreLimit.MaxTextureLodBias,
            new(WindowsOpenGlLimitReadStatus.Queried, NumberValue: -1)));
        Assert.True(WindowsOpenGlCoreLimitsCatalog.IsValidRead(WindowsOpenGlCoreLimit.MaxTextureSize,
            new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 0)));
        Assert.True(WindowsOpenGlCoreLimitsCatalog.IsValidRead(WindowsOpenGlCoreLimit.MaxShaderStorageBlockSize,
            new(WindowsOpenGlLimitReadStatus.Queried, Unsigned64Value: 0)));
        Assert.True(WindowsOpenGlCoreLimitsCatalog.IsValidRead(WindowsOpenGlCoreLimit.MaxTextureLodBias,
            new(WindowsOpenGlLimitReadStatus.Queried, NumberValue: 0)));
    }

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(static ()
            => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private static WindowsOpenGlCapabilityReadResult ReadResult(WindowsOpenGlCapabilityScope scope,
        WindowsOpenGlCapabilityFields selected)
        => new(true, [.. scope.Targets.Select((target, index) =>
        {
            var matches = index < 2;
            var version = (selected & WindowsOpenGlCapabilityFields.Version) != 0
                ? index == 1 ? "4.5.0 Mesa 24.1" : "4.6.0 NVIDIA 561.09"
                : null;
            ImmutableArray<string>? extensions = (selected & WindowsOpenGlCapabilityFields.Extensions) != 0
                ? index == 0 ? ImmutableArray.Create("GL_EXT_memory_object", "GL_EXT_memory_object_win32", "GL_EXT_texture_filter_anisotropic")
                    : ImmutableArray<string>.Empty
                : null;
            var limits = matches && (selected & WindowsOpenGlCapabilityFields.Limits) != 0
                ? CreateLimits(selected)
                : null;
            return new WindowsOpenGlAdapterCapabilityRead(target,
                matches ? target.Adapter.Luid : null, matches ? target.NodeMask : null,
                matches ? version : null, matches ? extensions : null, limits);
        })]);

    private static ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead> CreateLimits(
        WindowsOpenGlCapabilityFields selected)
    {
        var values = ImmutableDictionary.CreateBuilder<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>();
        foreach (var limit in Enum.GetValues<WindowsOpenGlCoreLimit>())
        {
            if ((selected & LimitField(limit)) == 0) continue;
            values.Add(limit, limit switch
            {
                WindowsOpenGlCoreLimit.MaxTextureSize => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 16384),
                WindowsOpenGlCoreLimit.Max3DTextureSize => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 2048),
                WindowsOpenGlCoreLimit.MaxCubeMapTextureSize => new(WindowsOpenGlLimitReadStatus.Queried, Unsigned32Value: 16384),
                WindowsOpenGlCoreLimit.MaxTextureLodBias => new(WindowsOpenGlLimitReadStatus.Queried, NumberValue: 16),
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

    private static ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>? UnavailableLimits(
        ImmutableDictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead>? limits)
        => limits?.Keys.ToImmutableDictionary(static limit => limit,
            static _ => new WindowsOpenGlLimitRead(WindowsOpenGlLimitReadStatus.Unavailable));

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

    private sealed class RecordedReader : IWindowsOpenGlCapabilityReader
    {
        private readonly ConcurrentQueue<WindowsOpenGlCapabilityFields> requests = new();
        public IReadOnlyList<WindowsOpenGlCapabilityFields> Requests => requests.ToArray();
        public string Version { get; set; } = "4.6.0 NVIDIA 561.09";
        public RecordedMode Mode { get; set; }
        public Action? AfterRead { get; set; }
        public Dictionary<WindowsOpenGlCoreLimit, WindowsOpenGlLimitRead> LimitOverrides { get; } = [];

        public WindowsOpenGlCapabilityReadResult Read(WindowsOpenGlCapabilityScope scope,
            WindowsOpenGlCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            if (Mode == RecordedMode.DirectoryUnavailable) return WindowsOpenGlCapabilityReadResult.Unavailable;
            var result = ReadResult(scope, selectedFields);
            if ((selectedFields & WindowsOpenGlCapabilityFields.Limits) != 0 && LimitOverrides.Count > 0)
                result = result with { Targets = [.. result.Targets.Select(row => row.Limits is { } limits
                    ? row with { Limits = limits.SetItems(LimitOverrides.Where(pair => limits.ContainsKey(pair.Key))) } : row)] };
            if ((selectedFields & WindowsOpenGlCapabilityFields.Version) != 0 && Mode == RecordedMode.Current)
                result = result with { Targets = [.. result.Targets.Select(row => row.Target == FirstNode
                    && row.ObservedLuid == FirstNode.Adapter.Luid && row.ObservedNodeMask == FirstNode.NodeMask
                        ? row with { Version = Version } : row)] };
            if (scope.Targets.Contains(FirstNode))
            {
                var index = scope.Targets.IndexOf(FirstNode);
                var row = result.Targets[index];
                if (Mode == RecordedMode.LuidMismatch)
                    result = result with { Targets = result.Targets.SetItem(index, row with
                    { ObservedLuid = SecondAdapter.Luid, Version = null, Extensions = null, Limits = UnavailableLimits(row.Limits) }) };
                else if (Mode == RecordedMode.NodeMaskMismatch)
                    result = result with { Targets = result.Targets.SetItem(index, row with
                    { ObservedNodeMask = 2, Version = null, Extensions = null, Limits = UnavailableLimits(row.Limits) }) };
                else if (Mode == RecordedMode.IdentityUnavailable)
                    result = result with { Targets = result.Targets.SetItem(index, row with
                    { ObservedLuid = null, ObservedNodeMask = null, Version = null, Extensions = null, Limits = UnavailableLimits(row.Limits) }) };
                else if (Mode == RecordedMode.DuplicateTarget)
                    result = result with { Targets = [.. result.Targets, row] };
                else if (Mode == RecordedMode.ForeignTarget)
                    result = result with { Targets = [new(OtherAdapterNode, SecondAdapter.Luid, 1,
                        null, null)] };
            }
            AfterRead?.Invoke();
            return result;
        }
    }

    private enum RecordedMode { Current, DirectoryUnavailable, LuidMismatch, NodeMaskMismatch,
        IdentityUnavailable, DuplicateTarget, ForeignTarget }

    private sealed class Permit(bool accept = true) : IMetricPublicationPermit
    {
        private int active = 1;
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => Volatile.Read(ref active) != 0;
        public void Retire() => Volatile.Write(ref active, 0);
        public bool TryPublish(Action commit)
        {
            if (!accept || !IsActive) return false;
            commit();
            return true;
        }
    }
}
