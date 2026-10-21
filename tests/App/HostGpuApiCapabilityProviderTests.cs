using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class HostGpuApiCapabilityProviderTests
{
    private static readonly WindowsD3dAdapterIdentity Adapter = new(0x0000_0001_0000_002A, 0x10DE, 0x2684);

    [Fact]
    public async Task ExplicitVulkanFactoryUsesTheProviderOriginalOwnerAndCatalog()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var provider = CreateProvider(sampling);
        var scope = new WindowsVulkanCapabilityScope(
            WindowsVulkanCapabilityFields.All | WindowsVulkanCapabilityFields.CubeArrayLimits, [Adapter]);
        var reader = new RecordedVulkanReader();
        var access = provider.CreateVulkanCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);

        var version = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_api_version"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var extension = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_extension"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|extension:VK_KHR_swapchain", StringComparison.Ordinal)).Field;
        var feature = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_feature"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|feature:core-1.0:robustBufferAccess", StringComparison.Ordinal)).Field;
        var cube = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_max_image_dimension_cube"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var arrayLayers = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_vulkan_max_image_array_layers"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)).Field;
        Assert.Equal(FieldObservationStatus.Current, access.Backend.ReadCached(version).Status);
        Assert.Equal("1.3.280", access.Backend.ReadCached(version).Value?.Text);
        Assert.Equal("VK_KHR_swapchain", access.Backend.ReadCached(extension).Value?.Text);
        Assert.Equal(true, access.Backend.ReadCached(feature).Value?.Boolean);
        Assert.Equal("2048", access.Backend.ReadCached(cube).Value?.Integer);
        Assert.Equal("0", access.Backend.ReadCached(arrayLayers).Value?.Integer);
        Assert.Equal("pixels", catalog.Fields.Single(item =>
            item.Field.FieldName == "gpu_all_vulkan_max_image_dimension_cube").Definition.Unit);
        Assert.Equal("layers", catalog.Fields.Single(item =>
            item.Field.FieldName == "gpu_all_vulkan_max_image_array_layers").Definition.Unit);
        Assert.Equal("1.3.280", provider.ReadState().InformationFields.FindField(version)?.Value?.GetString());

        using var borrow = access.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([feature]));
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId, Assert.Single(binding.CollectionItemIds));
        Assert.Equal(FieldCollectionSourceKind.SourceQuery, binding.SourceKind);
        var result = await borrow.CollectAsync([feature], new Permit(), default);
        Assert.True(result.Committed);
        Assert.Contains(feature, result.QueriedFields);
        Assert.Equal(true, Assert.Single(result.Readings).Value?.Boolean);
        var borrowedLayers = await borrow.CollectAsync([arrayLayers], new Permit(), default);
        Assert.True(borrowedLayers.Committed);
        Assert.Equal("0", Assert.Single(borrowedLayers.Readings).Value?.Integer);
        Assert.Equal(borrowedLayers.Readings[0], access.Backend.ReadCached(arrayLayers));
    }

    [Fact]
    public async Task ExplicitOpenGlFactoryUsesCallerContextSourceAndDoesNotAssumeAnAdapter()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var provider = CreateProvider(sampling);
        var target = new WindowsOpenGlAdapterTarget(Adapter, 1);
        var scope = new WindowsOpenGlCapabilityScope(WindowsOpenGlCapabilityFields.All, [target]);
        var contextExecutor = new NeverCalledContextExecutor();
        _ = provider.CreateOpenGlCapabilityInformation(scope, contextExecutor);
        Assert.Equal(0, contextExecutor.Calls);
        var reader = new RecordedOpenGlReader();
        var access = provider.CreateOpenGlCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var version = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_opengl_version"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|node-mask:00000001", StringComparison.Ordinal)).Field;
        var extension = catalog.Fields.Single(item => item.Field.FieldName == "gpu_all_opengl_extension"
            && item.Field.DeviceIdentity.Contains($"luid:{Adapter.Luid:X16}", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.Contains("|node-mask:00000001", StringComparison.Ordinal)
            && item.Field.DeviceIdentity.EndsWith("|extension:GL_EXT_memory_object_win32", StringComparison.Ordinal)).Field;
        Assert.Equal("4.6.0 host-recorded", access.Backend.ReadCached(version).Value?.Text);
        Assert.Equal("GL_EXT_memory_object_win32", access.Backend.ReadCached(extension).Value?.Text);
        Assert.NotNull(provider.ReadState().InformationFields.FindField(version));

        reader.Mode = RecordedMode.NoCurrentContext;
        var unavailable = Assert.Single(await access.Backend.CollectAsync([version], default));
        Assert.Equal(FieldObservationStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Value);
        Assert.Equal(WindowsOpenGlCapabilityFields.Version, reader.Requests.Last());
    }

    [Fact]
    public async Task ExplicitD3d12RaytracingFactoryPublishesToTheOriginalHostInformationWire()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var provider = CreateProvider(sampling);
        var reader = new RecordedRaytracingReader();
        var scope = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.RaytracingTier,
            WindowsD3dApiVersion.Direct3D12, [Adapter]);
        var access = provider.CreateD3dCapabilityInformation(scope, reader);
        Assert.Equal(0, reader.Calls);
        var field = Assert.Single((await access.Backend.DiscoverAsync(default)).Fields).Field;
        Assert.Contains($"d3d12|luid:{Adapter.Luid:X16}|pci:{Adapter.PciVendorId:X4}:{Adapter.PciDeviceId:X4}", field.DeviceIdentity);
        Assert.Equal("0", access.Backend.ReadCached(field).Value?.Integer);
        var calls = reader.Calls;
        // This is the actual snapshot returned by /api/device-topology/fields, serialized with Web defaults.
        var wire = System.Text.Json.JsonSerializer.SerializeToElement(provider.ReadState().InformationFields,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var device = Assert.Single(wire.GetProperty("devices").EnumerateArray());
        Assert.Equal(field.DeviceIdentity, device.GetProperty("deviceIdentity").GetString());
        var wireField = Assert.Single(device.GetProperty("fields").EnumerateArray());
        Assert.Equal("gpu_all_d3d12_raytracing_tier", wireField.GetProperty("definition").GetProperty("name").GetString());
        Assert.Equal("0", wireField.GetProperty("value").GetString());
        Assert.Equal(calls, reader.Calls);
        using var borrow = access.BorrowSubscriptionCapture();
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId,
            Assert.Single(Assert.Single(borrow.Bind([field])).CollectionItemIds));
        reader.Tier = uint.MaxValue;
        var captured = await borrow.CollectAsync([field], new Permit(), default);
        Assert.True(captured.Committed);
        Assert.Equal("4294967295", Assert.Single(captured.Readings).Value?.Integer);
        Assert.Equal(captured.Readings[0], access.Backend.ReadCached(field));
        Assert.Equal("4294967295", provider.ReadState().InformationFields.FindField(field)?.Value?.GetString());
        reader.Tier = null;
        _ = await borrow.CollectAsync([field], new Permit(), default);
        Assert.Null(provider.ReadState().InformationFields.FindField(field)?.Value);
        Assert.Null(access.Backend.ReadCached(field).ObservedAt);
    }

    private sealed class RecordedRaytracingReader : IWindowsD3dCapabilityReader
    {
        public uint? Tier { get; set; } = 0;
        public int Calls { get; private set; }
        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            Assert.Equal(WindowsD3dCapabilityFields.RaytracingTier, selectedFields);
            return new([new(scope.Adapters.Single(), scope.ApiVersion, null, []) { RaytracingTier = Tier }]);
        }
    }

    private static DeviceTopologySnapshotProvider CreateProvider(HostManagerSamplingSubscriptionTestFixture sampling)
        => new(new RecordedTopology(), new RecordedStore(), new DeviceTopologySemanticComparer(), sampling.Owner,
            NullLogger<DeviceTopologySnapshotProvider>.Instance, new RecordedAcpi());

    private sealed class RecordedVulkanReader : IWindowsVulkanCapabilityReader
    {
        public WindowsVulkanCapabilityReadResult Read(WindowsVulkanCapabilityScope scope,
            WindowsVulkanCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<WindowsVulkanFeatureRead>? features = (selectedFields & WindowsVulkanCapabilityFields.CoreFeatures) != 0
                ? ImmutableArray.CreateRange(WindowsVulkanCore10FeatureCatalog.Names.Select(static name =>
                    new WindowsVulkanFeatureRead(name, name == "robustBufferAccess")))
                : null;
            return new(true, [new(Adapter,
                (selectedFields & WindowsVulkanCapabilityFields.ApiVersion) != 0 ? "1.3.280" : null,
                (selectedFields & WindowsVulkanCapabilityFields.Extensions) != 0
                    ? ImmutableArray.Create("VK_KHR_swapchain") : null, features)
            {
                CoreLimits = (selectedFields & (WindowsVulkanCapabilityFields.ImageDimensionLimits
                        | WindowsVulkanCapabilityFields.CubeArrayLimits)) != 0
                    ? new WindowsVulkanCoreLimitsRead(
                        (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension1D) != 0 ? 8192U : null,
                        (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension2D) != 0 ? 4096U : null,
                        (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension3D) != 0 ? 2048U : null,
                        (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimensionCube) != 0 ? 2048U : null,
                        (selectedFields & WindowsVulkanCapabilityFields.MaxImageArrayLayers) != 0 ? 0U : null)
                    : null
            }]);
        }
    }

    private sealed class RecordedOpenGlReader : IWindowsOpenGlCapabilityReader
    {
        public RecordedMode Mode { get; set; }
        public List<WindowsOpenGlCapabilityFields> Requests { get; } = [];

        public WindowsOpenGlCapabilityReadResult Read(WindowsOpenGlCapabilityScope scope,
            WindowsOpenGlCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(selectedFields);
            if (Mode == RecordedMode.NoCurrentContext) return new(false, []);
            return new(true, [new(scope.Targets.Single(), Adapter.Luid, 1,
                (selectedFields & WindowsOpenGlCapabilityFields.Version) != 0 ? "4.6.0 host-recorded" : null,
                (selectedFields & WindowsOpenGlCapabilityFields.Extensions) != 0
                    ? ImmutableArray.Create("GL_EXT_memory_object", "GL_EXT_memory_object_win32") : null)]);
        }
    }

    private sealed class NeverCalledContextExecutor : IWindowsOpenGlCurrentContextExecutor
    {
        public int Calls { get; private set; }

        public T Execute<T>(WindowsOpenGlAdapterTarget target, Func<T> action,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("The native OpenGL reader must remain idle until explicit collection.");
        }
    }

    private enum RecordedMode { Current, NoCurrentContext }

    private sealed class RecordedTopology : IDeviceTopologyReader
    {
        public DeviceTopologySnapshot ReadSnapshot() => throw new InvalidOperationException("Topology capture is not requested.");
    }

    private sealed class RecordedStore : IDeviceTopologySnapshotStore
    {
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot value, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Topology persistence is not requested.");
    }

    private sealed class RecordedAcpi : IAcpiFirmwareTableReader
    {
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables()
            => throw new InvalidOperationException("ACPI capture is not requested.");
    }

    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
}
