using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsVulkanCapabilityInformationTests
{
    private static readonly WindowsD3dAdapterIdentity FirstAdapter = new(0x0000_0001_0000_002A, 0x10DE, 0x2684);
    private static readonly WindowsD3dAdapterIdentity SecondAdapter = new(0x0000_0002_0000_003B, 0x8086, 0x46A6);

    [Fact]
    public async Task ExactLuidAndPciPhysicalDeviceCapabilitiesUseOriginalCurrentAndSelectedBorrow()
    {
        var scope = new WindowsVulkanCapabilityScope(WindowsVulkanCapabilityFields.All,
            [FirstAdapter, SecondAdapter]);
        var reader = new RecordedReader();
        using var owner = EmptyOwner();
        var access = owner.CreateVulkanCapabilityInformation(scope, reader);

        Assert.Empty(access.Backend.Catalog.Fields);
        Assert.Empty(reader.Requests);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(WindowsVulkanCapabilityFields.All, Assert.Single(reader.Requests));
        Assert.DoesNotContain(catalog.Fields, field => field.Field.FieldName == "gpu_all_vulkan_max_texel_buffer_elements");

        var firstVersion = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_vulkan_api_version"
            && field.Field.DeviceIdentity.Contains("luid:000000010000002A", StringComparison.Ordinal)).Field;
        var firstExtensions = catalog.Fields.Where(field => field.Field.FieldName == "gpu_all_vulkan_extension"
            && field.Field.DeviceIdentity.Contains("luid:000000010000002A", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, firstExtensions.Length);
        Assert.Equal(110, catalog.Fields.Count(field => field.Field.FieldName == "gpu_all_vulkan_feature"));
        Assert.Equal("1.3.280", access.Backend.ReadCached(firstVersion).Value?.Text);
        Assert.Contains(catalog.Devices, device => device.Identity == firstVersion.DeviceIdentity
            && device.Resource == "gpu_adapter");
        Assert.All(firstExtensions, field => Assert.Contains(catalog.Devices,
            device => device.Identity == field.Field.DeviceIdentity && device.Resource == "gpu_adapter_extension"));

        var extension = firstExtensions.Single(field => field.Field.DeviceIdentity.EndsWith(
            "|extension:VK_KHR_swapchain", StringComparison.Ordinal)).Field;
        Assert.Equal("VK_KHR_swapchain", access.Backend.ReadCached(extension).Value?.Text);
        var feature = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_vulkan_feature"
            && field.Field.DeviceIdentity.EndsWith("|feature:core-1.0:robustBufferAccess", StringComparison.Ordinal)
            && field.Field.DeviceIdentity.Contains("luid:000000010000002A", StringComparison.Ordinal)).Field;
        Assert.True(access.Backend.ReadCached(feature).Value!.Boolean);
        var falseFeature = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_vulkan_feature"
            && field.Field.DeviceIdentity.EndsWith("|feature:core-1.0:fullDrawIndexUint32", StringComparison.Ordinal)
            && field.Field.DeviceIdentity.Contains("luid:000000010000002A", StringComparison.Ordinal)).Field;
        Assert.False(access.Backend.ReadCached(falseFeature).Value!.Boolean);

        reader.Mode = RecordedMode.FeaturesUnavailable;
        var failedFeature = Assert.Single(await access.Backend.CollectAsync([feature], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, failedFeature.Status);
        Assert.Null(failedFeature.Value);
        Assert.Null(failedFeature.ObservedAt);
        reader.Mode = RecordedMode.Current;

        var readCount = reader.Requests.Count;
        _ = access.Backend.ReadCached(extension);
        Assert.Equal(readCount, reader.Requests.Count);

        reader.ApiVersion = "1.3.281";
        var selected = Assert.Single(await access.Backend.CollectAsync([firstVersion], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Current, selected.Status);
        Assert.Equal("1.3.281", selected.Value?.Text);
        Assert.Equal(WindowsVulkanCapabilityFields.ApiVersion, reader.Requests.Last());
        Assert.Equal("VK_KHR_swapchain", access.Backend.ReadCached(extension).Value?.Text);

        using var borrow = access.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([extension]));
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId, Assert.Single(binding.CollectionItemIds));
        var borrowed = await borrow.CollectAsync([extension], new Permit(), CancellationToken.None);
        Assert.True(borrowed.Committed);
        Assert.Equal(FieldObservationStatus.Current, Assert.Single(borrowed.Readings).Status);
        Assert.Equal(WindowsVulkanCapabilityFields.Extensions, reader.Requests.Last());
        Assert.Equal(borrowed.Readings[0], access.Backend.ReadCached(extension));
        Assert.NotNull(owner.ReadState().InformationFields.FindField(extension));

        await owner.SampleTopologyAsync(null, CancellationToken.None, requireDemand: false);
        Assert.Equal("1.3.281", access.Backend.ReadCached(firstVersion).Value?.Text);
        Assert.Equal("VK_KHR_swapchain", access.Backend.ReadCached(extension).Value?.Text);
    }

    [Fact]
    public async Task CoreImageDimensionLimitsUseExactSelectedFieldsAndOriginalOwnerBorrow()
    {
        var selectedDimensions = WindowsVulkanCapabilityFields.MaxImageDimension1D
            | WindowsVulkanCapabilityFields.MaxImageDimension2D
            | WindowsVulkanCapabilityFields.MaxImageDimension3D;
        var scope = new WindowsVulkanCapabilityScope(selectedDimensions, [FirstAdapter]);
        var reader = new RecordedReader();
        using var owner = EmptyOwner();
        var access = owner.CreateVulkanCapabilityInformation(scope, reader);

        Assert.Empty(access.Backend.Catalog.Fields);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(selectedDimensions, Assert.Single(reader.Requests));
        Assert.Equal(3, catalog.Fields.Length);

        var dimension1D = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_vulkan_max_image_dimension_1d");
        var dimension2D = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_vulkan_max_image_dimension_2d");
        var dimension3D = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_vulkan_max_image_dimension_3d");
        foreach (var capability in new[] { dimension1D, dimension2D, dimension3D })
        {
            Assert.Equal("unsigned32", capability.Definition.ValueKind);
            Assert.Equal("pixels", capability.Definition.Unit);
            Assert.True(capability.Read.Supported);
            Assert.False(capability.Write.Supported);
            Assert.Equal("uint32", owner.ReadState().InformationFields.FindField(capability.Field)?.Definition.ValueKind);
            Assert.Contains(catalog.Devices, device => device.Identity == capability.Field.DeviceIdentity
                && device.Resource == "gpu_adapter");
            Assert.Contains($"luid:{FirstAdapter.Luid:X16}", capability.Field.DeviceIdentity, StringComparison.Ordinal);
            Assert.Contains($"pci:{FirstAdapter.PciVendorId:X4}:{FirstAdapter.PciDeviceId:X4}",
                capability.Field.DeviceIdentity, StringComparison.Ordinal);
        }
        Assert.Equal("16384", access.Backend.ReadCached(dimension1D.Field).Value?.Integer);
        Assert.Equal("8192", access.Backend.ReadCached(dimension2D.Field).Value?.Integer);
        Assert.Equal("2048", access.Backend.ReadCached(dimension3D.Field).Value?.Integer);

        var callsBeforeCache = reader.Requests.Count;
        _ = access.Backend.ReadCached(dimension2D.Field);
        Assert.Equal(callsBeforeCache, reader.Requests.Count);

        var selected1D = Assert.Single(await access.Backend.CollectAsync([dimension1D.Field], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Current, selected1D.Status);
        Assert.Equal("16384", selected1D.Value?.Integer);
        Assert.Equal(WindowsVulkanCapabilityFields.MaxImageDimension1D, reader.Requests.Last());
        Assert.Equal("8192", access.Backend.ReadCached(dimension2D.Field).Value?.Integer);
        Assert.Equal("2048", access.Backend.ReadCached(dimension3D.Field).Value?.Integer);

        reader.MaxImageDimension2D = uint.MaxValue;
        var unsigned32Boundary = Assert.Single(await access.Backend.CollectAsync([dimension2D.Field], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Current, unsigned32Boundary.Status);
        Assert.Equal("4294967295", unsigned32Boundary.Value?.Integer);
        Assert.Equal(WindowsVulkanCapabilityFields.MaxImageDimension2D, reader.Requests.Last());

        reader.MaxImageDimension2D = 8193;
        var refreshed = Assert.Single(await access.Backend.CollectAsync([dimension2D.Field], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Current, refreshed.Status);
        Assert.Equal("8193", refreshed.Value?.Integer);
        Assert.Equal(WindowsVulkanCapabilityFields.MaxImageDimension2D, reader.Requests.Last());
        Assert.Equal("16384", access.Backend.ReadCached(dimension1D.Field).Value?.Integer);
        Assert.Equal("2048", access.Backend.ReadCached(dimension3D.Field).Value?.Integer);

        using var borrow = access.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([dimension3D.Field]));
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId, Assert.Single(binding.CollectionItemIds));
        var borrowed = await borrow.CollectAsync([dimension3D.Field], new Permit(), CancellationToken.None);
        Assert.True(borrowed.Committed);
        Assert.Equal(FieldObservationStatus.Current, Assert.Single(borrowed.Readings).Status);
        Assert.Equal("2048", borrowed.Readings[0].Value?.Integer);
        Assert.Equal(WindowsVulkanCapabilityFields.MaxImageDimension3D, reader.Requests.Last());
        Assert.Equal(borrowed.Readings[0], access.Backend.ReadCached(dimension3D.Field));

        reader.Mode = RecordedMode.LimitsUnavailable;
        var unavailable = Assert.Single(await access.Backend.CollectAsync([dimension1D.Field], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Value);
        Assert.Null(unavailable.ObservedAt);
        Assert.Equal("8193", access.Backend.ReadCached(dimension2D.Field).Value?.Integer);
        Assert.Equal("2048", access.Backend.ReadCached(dimension3D.Field).Value?.Integer);

        reader.Mode = RecordedMode.Current;
        using var canceled = new CancellationTokenSource();
        reader.AfterRead = canceled.Cancel;
        var beforeCanceledCapture = owner.ReadState();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await access.Backend.CollectAsync([dimension2D.Field], canceled.Token));
        Assert.Same(beforeCanceledCapture, owner.ReadState());
        reader.AfterRead = null;

        var narrowerScope = new WindowsVulkanCapabilityScope(
            WindowsVulkanCapabilityFields.MaxImageDimension2D, [FirstAdapter]);
        var narrowReader = new RecordedReader();
        var narrowerAccess = owner.CreateVulkanCapabilityInformation(narrowerScope, narrowReader);
        var narrowCatalog = await narrowerAccess.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal("gpu_all_vulkan_max_image_dimension_2d", Assert.Single(narrowCatalog.Fields).Field.FieldName);
        Assert.Equal(WindowsVulkanCapabilityFields.MaxImageDimension2D, Assert.Single(narrowReader.Requests));
    }

    [Fact]
    public async Task CubeAndArrayLimitsUseExplicitFieldsAndTheOriginalOwnerCurrent()
    {
        const WindowsVulkanCapabilityFields selected = WindowsVulkanCapabilityFields.CubeArrayLimits;
        var scope = new WindowsVulkanCapabilityScope(selected, [FirstAdapter]);
        var reader = new RecordedReader { MaxImageDimensionCube = uint.MaxValue, MaxImageArrayLayers = 0 };
        using var owner = EmptyOwner();
        var access = owner.CreateVulkanCapabilityInformation(scope, reader);

        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(selected, Assert.Single(reader.Requests));
        Assert.Equal(2, catalog.Fields.Length);
        var cube = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_vulkan_max_image_dimension_cube");
        var arrayLayers = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_vulkan_max_image_array_layers");
        Assert.Equal("unsigned32", cube.Definition.ValueKind);
        Assert.Equal("pixels", cube.Definition.Unit);
        Assert.Equal("unsigned32", arrayLayers.Definition.ValueKind);
        Assert.Equal("layers", arrayLayers.Definition.Unit);
        foreach (var capability in new[] { cube, arrayLayers })
        {
            Assert.True(capability.Read.Supported);
            Assert.False(capability.Write.Supported);
            Assert.Equal("uint32", owner.ReadState().InformationFields.FindField(capability.Field)?.Definition.ValueKind);
            Assert.Contains($"luid:{FirstAdapter.Luid:X16}", capability.Field.DeviceIdentity, StringComparison.Ordinal);
            Assert.Contains($"pci:{FirstAdapter.PciVendorId:X4}:{FirstAdapter.PciDeviceId:X4}",
                capability.Field.DeviceIdentity, StringComparison.Ordinal);
        }
        Assert.Equal("4294967295", access.Backend.ReadCached(cube.Field).Value?.Integer);
        Assert.Equal("0", access.Backend.ReadCached(arrayLayers.Field).Value?.Integer);
        Assert.DoesNotContain(catalog.Fields, field => field.Field.FieldName is
            "gpu_all_vulkan_max_image_dimension_1d" or "gpu_all_vulkan_max_image_dimension_2d"
                or "gpu_all_vulkan_max_image_dimension_3d");

        var requestsBeforeCache = reader.Requests.Count;
        _ = access.Backend.ReadCached(arrayLayers.Field);
        Assert.Equal(requestsBeforeCache, reader.Requests.Count);

        var selectedCube = Assert.Single(await access.Backend.CollectAsync([cube.Field], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Current, selectedCube.Status);
        Assert.Equal("4294967295", selectedCube.Value?.Integer);
        Assert.Equal(WindowsVulkanCapabilityFields.MaxImageDimensionCube, reader.Requests.Last());
        Assert.Equal("0", access.Backend.ReadCached(arrayLayers.Field).Value?.Integer);

        using var borrow = access.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([arrayLayers.Field]));
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId, Assert.Single(binding.CollectionItemIds));
        var borrowed = await borrow.CollectAsync([arrayLayers.Field], new Permit(), CancellationToken.None);
        Assert.True(borrowed.Committed);
        Assert.Equal(FieldObservationStatus.Current, Assert.Single(borrowed.Readings).Status);
        Assert.Equal("0", borrowed.Readings[0].Value?.Integer);
        Assert.Equal(WindowsVulkanCapabilityFields.MaxImageArrayLayers, reader.Requests.Last());
        Assert.Equal(borrowed.Readings[0], access.Backend.ReadCached(arrayLayers.Field));

        reader.Mode = RecordedMode.LimitsUnavailable;
        var unavailableCube = Assert.Single(await access.Backend.CollectAsync([cube.Field], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, unavailableCube.Status);
        Assert.Null(unavailableCube.Value);
        Assert.Equal("0", access.Backend.ReadCached(arrayLayers.Field).Value?.Integer);
    }

    [Fact]
    public async Task CoreLimitsGroupProjectsAllMembersAndSelectionTouchesOnlyTheRequestedAdapterField()
    {
        var scope = new WindowsVulkanCapabilityScope(WindowsVulkanCapabilityFields.CoreLimits,
            [FirstAdapter, SecondAdapter]);
        var reader = new RecordedReader();
        using var owner = EmptyOwner();
        var access = owner.CreateVulkanCapabilityInformation(scope, reader);

        Assert.Empty(access.Backend.Catalog.Fields);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(WindowsVulkanCapabilityFields.CoreLimits, Assert.Single(reader.Requests));
        Assert.Equal(202, catalog.Fields.Length);
        Assert.DoesNotContain(catalog.Fields, field => field.Field.FieldName is
            "gpu_all_vulkan_max_image_dimension_1d" or "gpu_all_vulkan_max_image_dimension_2d"
                or "gpu_all_vulkan_max_image_dimension_3d" or "gpu_all_vulkan_max_image_dimension_cube"
                or "gpu_all_vulkan_max_image_array_layers");

        var firstAdapterIdentity = WindowsInformationFieldProjection.VulkanAdapterIdentity(FirstAdapter);
        var firstAdapterFields = catalog.Fields.Where(field => field.Field.DeviceIdentity == firstAdapterIdentity).ToArray();
        var firstOffset = firstAdapterFields.Single(field =>
            field.Field.FieldName == "gpu_all_vulkan_min_texel_offset");
        var secondOffset = catalog.Fields.Single(field => field.Field.DeviceIdentity ==
            WindowsInformationFieldProjection.VulkanAdapterIdentity(SecondAdapter)
            && field.Field.FieldName == "gpu_all_vulkan_min_texel_offset");
        var bufferGranularityEntry = firstAdapterFields.Single(field => field.Field.FieldName ==
            "gpu_all_vulkan_buffer_image_granularity");
        var memoryMapAlignmentEntry = firstAdapterFields.Single(field => field.Field.FieldName ==
            "gpu_all_vulkan_min_memory_map_alignment");
        var workGroupCountEntry = firstAdapterFields.Single(field => field.Field.FieldName ==
            "gpu_all_vulkan_max_compute_work_group_count");
        var sampleCountsEntry = firstAdapterFields.Single(field => field.Field.FieldName ==
            "gpu_all_vulkan_framebuffer_color_sample_counts");
        var texelMaximum = firstAdapterFields.Single(field => field.Field.FieldName ==
            "gpu_all_vulkan_max_texel_offset").Field;
        var timestampSupport = firstAdapterFields.Single(field => field.Field.FieldName ==
            "gpu_all_vulkan_timestamp_compute_and_graphics").Field;

        Assert.Equal("signed32", firstOffset.Definition.ValueKind);
        Assert.Equal("texels", firstOffset.Definition.Unit);
        Assert.Equal("-17", access.Backend.ReadCached(firstOffset.Field).Value?.Integer);
        Assert.Equal("-18", access.Backend.ReadCached(secondOffset.Field).Value?.Integer);
        Assert.Equal("unsigned64", bufferGranularityEntry.Definition.ValueKind);
        Assert.Equal(ulong.MaxValue - 1, ulong.Parse(access.Backend.ReadCached(bufferGranularityEntry.Field).Value!.Integer!));
        Assert.Equal("unsigned128", memoryMapAlignmentEntry.Definition.ValueKind);
        Assert.Equal(ulong.MaxValue.ToString(), access.Backend.ReadCached(memoryMapAlignmentEntry.Field).Value?.Integer);
        Assert.Equal("0", access.Backend.ReadCached(texelMaximum).Value?.Integer);
        Assert.Equal(true, access.Backend.ReadCached(timestampSupport).Value?.Boolean);
        Assert.Equal("2147483653", access.Backend.ReadCached(sampleCountsEntry.Field).Value?.Integer);
        var workGroupValue = access.Backend.ReadCached(workGroupCountEntry.Field).Value?.Structure;
        Assert.True(workGroupValue.HasValue);
        Assert.Equal(new uint[] { 4, 5, 6 }, workGroupValue.Value.EnumerateArray()
            .Select(static value => value.GetUInt32()).ToArray());

        var unselectedBefore = access.Backend.ReadCached(bufferGranularityEntry.Field);
        reader.MinTexelOffset = -29;
        var updated = Assert.Single(await access.Backend.CollectAsync([firstOffset.Field], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Current, updated.Status);
        Assert.Equal("-29", updated.Value?.Integer);
        Assert.Equal(WindowsVulkanCapabilityFields.CoreLimits, reader.Requests.Last());
        Assert.Equal("-18", access.Backend.ReadCached(secondOffset.Field).Value?.Integer);
        Assert.Equal(unselectedBefore, access.Backend.ReadCached(bufferGranularityEntry.Field));

        reader.Mode = RecordedMode.LimitsUnavailable;
        var failed = Assert.Single(await access.Backend.CollectAsync([firstOffset.Field], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status);
        Assert.Null(failed.Value);
        Assert.Equal(202, access.Backend.Catalog.Fields.Length);
        Assert.True(access.Backend.Catalog.Fields.Single(field => field.Field == firstOffset.Field).Read.Supported);
        Assert.Equal(FieldObservationStatus.Unavailable, access.Backend.ReadCached(firstOffset.Field).Status);
        Assert.Equal("-18", access.Backend.ReadCached(secondOffset.Field).Value?.Integer);
        Assert.Equal(unselectedBefore, access.Backend.ReadCached(bufferGranularityEntry.Field));

        reader.Mode = RecordedMode.Current;
        reader.MinTexelOffset = -31;
        using var borrow = access.BorrowSubscriptionCapture();
        var borrowed = await borrow.CollectAsync([firstOffset.Field], new Permit(), CancellationToken.None);
        Assert.True(borrowed.Committed);
        Assert.Equal(FieldObservationStatus.Current, Assert.Single(borrowed.Readings).Status);
        Assert.Equal("-31", borrowed.Readings[0].Value?.Integer);
        Assert.Equal(borrowed.Readings[0], access.Backend.ReadCached(firstOffset.Field));
    }

    [Fact]
    public async Task FailedOrAmbiguousIdentityClearsOnlySelectedValuesAndRejectedOrCanceledCaptureDoesNotCommit()
    {
        var scope = new WindowsVulkanCapabilityScope(WindowsVulkanCapabilityFields.All, [FirstAdapter]);
        var reader = new RecordedReader();
        using var owner = EmptyOwner();
        var access = owner.CreateVulkanCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        var version = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_vulkan_api_version"
            && field.Field.DeviceIdentity.Contains($"luid:{FirstAdapter.Luid:X16}", StringComparison.Ordinal)).Field;
        var extension = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_vulkan_extension"
            && field.Field.DeviceIdentity.Contains($"luid:{FirstAdapter.Luid:X16}", StringComparison.Ordinal)
            && field.Field.DeviceIdentity.EndsWith("|extension:VK_KHR_swapchain", StringComparison.Ordinal)).Field;
        var extensionBefore = access.Backend.ReadCached(extension);

        reader.Mode = RecordedMode.ForeignAdapter;
        var unavailable = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Value);
        Assert.Equal(extensionBefore, access.Backend.ReadCached(extension));

        reader.Mode = RecordedMode.DuplicateAdapter;
        var duplicate = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, duplicate.Status);
        Assert.Null(duplicate.Value);
        Assert.Equal(extensionBefore, access.Backend.ReadCached(extension));

        reader.Mode = RecordedMode.Current;
        _ = await access.Backend.CollectAsync([version], CancellationToken.None);
        Assert.Equal("1.3.280", access.Backend.ReadCached(version).Value?.Text);

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
    public void ScopeUsesFiniteCallerConfigurableBoundsAndRejectsDuplicateOrInvalidTargets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsVulkanCapabilityScope(
            WindowsVulkanCapabilityFields.None, [FirstAdapter]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsVulkanCapabilityScope(
            (WindowsVulkanCapabilityFields)512, [FirstAdapter]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsVulkanCapabilityScope(
            WindowsVulkanCapabilityFields.ApiVersion, [FirstAdapter], maximumPhysicalDeviceCount: 0));
        Assert.Throws<ArgumentException>(() => new WindowsVulkanCapabilityScope(
            WindowsVulkanCapabilityFields.ApiVersion, [FirstAdapter, FirstAdapter]));

        var larger = new WindowsVulkanCapabilityScope(WindowsVulkanCapabilityFields.Extensions,
            AdapterIdentities(65), maximumSelectedAdapterCount: 65, maximumPhysicalDeviceCount: 513);
        Assert.Equal(65, larger.Adapters.Length);
        Assert.Equal(513, larger.MaximumPhysicalDeviceCount);
    }

    [Fact]
    public void Vulkan11IdentityPropertiesAndCoreFeatureBuffersUseReviewedAbiOffsets()
    {
        var layout = WindowsVulkanCapabilityNativeReader.AbiLayout;
        Assert.Equal(IntPtr.Size == 8 ? 48 : 40, layout.IdLuidOffset);
        Assert.Equal(IntPtr.Size == 8 ? 60 : 52, layout.IdLuidValidOffset);
        Assert.Equal(IntPtr.Size == 8 ? 16 : 8, layout.PropertiesDataOffset);
        Assert.Equal(IntPtr.Size == 8 ? 16 : 8, layout.FeaturesDataOffset);
        Assert.Equal(260, layout.ExtensionPropertiesSize);
        Assert.Equal(55 * sizeof(uint), layout.CoreFeatureBytes);
    }

    [Fact]
    public void VulkanCoreImageDimensionPropertiesUseReviewedWindowsX64Offsets()
    {
        var layout = WindowsVulkanCapabilityNativeReader.AbiLayout;
        Assert.Equal(296, layout.PropertiesLimitsOffset);
        Assert.Equal(504, layout.LimitsSize);
        Assert.Equal(800, layout.PropertiesPrefixSize);
        Assert.Equal(0, layout.MaxImageDimension1DOffset);
        Assert.Equal(4, layout.MaxImageDimension2DOffset);
        Assert.Equal(8, layout.MaxImageDimension3DOffset);
        Assert.Equal(12, layout.MaxImageDimensionCubeOffset);
        Assert.Equal(16, layout.MaxImageArrayLayersOffset);
        Assert.Equal(312, layout.MaxImageDimension1DAbsoluteOffset);
        Assert.Equal(316, layout.MaxImageDimension2DAbsoluteOffset);
        Assert.Equal(320, layout.MaxImageDimension3DAbsoluteOffset);
        Assert.Equal(324, layout.MaxImageDimensionCubeAbsoluteOffset);
        Assert.Equal(328, layout.MaxImageArrayLayersAbsoluteOffset);
    }

    [Fact]
    public void VulkanCoreLimitsUseReviewedWin64MemberOffsetsAndDecodeExactNativeTypes()
    {
        Assert.Equal(8, IntPtr.Size);
        var layout = WindowsVulkanCapabilityNativeReader.AbiLayout;
        Assert.Equal(296, layout.PropertiesLimitsOffset);
        Assert.Equal(504, layout.LimitsSize);
        Assert.Equal(800, layout.PropertiesPrefixSize);
        Assert.Equal(101, WindowsVulkanCapabilityNativeReader.CoreLimitsMemberOffsets.Length);
        Assert.Equal(new int[] { 20, 24, 28, 32, 36, 40, 48, 56, 64, 68, 72, 76, 80, 84, 88, 92, 96, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 148, 152, 156, 160, 164, 168, 172, 176, 180, 184, 188, 192, 196, 200, 204, 208, 212, 216, 220, 232, 236, 248, 252, 256, 260, 264, 268, 272, 276, 280, 288, 296, 304, 312, 320, 328, 336, 340, 344, 348, 352, 356, 360, 364, 368, 372, 376, 380, 384, 388, 392, 396, 400, 404, 408, 412, 416, 420, 424, 428, 432, 436, 440, 444, 452, 460, 464, 468, 472, 480, 488, 496 },
            WindowsVulkanCapabilityNativeReader.CoreLimitsMemberOffsets);
        Assert.Equal(101, WindowsVulkanCoreLimitsCatalog.Bindings.Length);

        const int payloadOffset = 16;
        const int limitsOffset = 312;
        var properties = new byte[4112];
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(0, 4), 1000059001);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(payloadOffset, 4), (1U << 22) | (3U << 12) | 280U);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(payloadOffset + 8, 4), 0x10DE);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(payloadOffset + 12, 4), 0x2684);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(limitsOffset + 20, 4), 0xF1234567);
        BinaryPrimitives.WriteUInt64LittleEndian(properties.AsSpan(limitsOffset + 48, 8), ulong.MaxValue - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(limitsOffset + 220, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(limitsOffset + 224, 4), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(limitsOffset + 228, 4), 6);
        BinaryPrimitives.WriteSingleLittleEndian(properties.AsSpan(limitsOffset + 288, 4), -2.5F);
        BinaryPrimitives.WriteSingleLittleEndian(properties.AsSpan(limitsOffset + 292, 4), 2.5F);
        BinaryPrimitives.WriteUInt64LittleEndian(properties.AsSpan(limitsOffset + 304, 8), ulong.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(limitsOffset + 336, 4), -23);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(limitsOffset + 376, 4), 0x80000005);
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(limitsOffset + 420, 4), 1);
        BinaryPrimitives.WriteSingleLittleEndian(properties.AsSpan(limitsOffset + 424, 4), 0.125F);
        BinaryPrimitives.WriteSingleLittleEndian(properties.AsSpan(limitsOffset + 444, 4), 1F);
        BinaryPrimitives.WriteSingleLittleEndian(properties.AsSpan(limitsOffset + 448, 4), 64F);
        BinaryPrimitives.WriteUInt64LittleEndian(properties.AsSpan(limitsOffset + 496, 8), ulong.MaxValue);

        var idProperties = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(idProperties.AsSpan(0, 4), 1000071004);
        BinaryPrimitives.WriteUInt64LittleEndian(idProperties.AsSpan(48, 8), FirstAdapter.Luid);
        BinaryPrimitives.WriteUInt32LittleEndian(idProperties.AsSpan(56, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(idProperties.AsSpan(60, 4), 1);

        var scope = new WindowsVulkanCapabilityScope(WindowsVulkanCapabilityFields.CoreLimits, [FirstAdapter]);
        var decoded = WindowsVulkanCapabilityNativeReader.DecodeIdentityResponse(
            properties, idProperties, scope.Adapters, WindowsVulkanCapabilityFields.CoreLimits);
        Assert.True(decoded.HasValue);
        var limits = Assert.IsType<WindowsVulkanCoreLimitsRead>(decoded.Value.CoreLimits);
        Assert.Equal(0xF1234567U, limits.MaxTexelBufferElements);
        Assert.Equal(ulong.MaxValue - 1, limits.BufferImageGranularity);
        Assert.Equal(ulong.MaxValue, (ulong)limits.MinMemoryMapAlignment!.Value);
        Assert.Equal(-23, limits.MinTexelOffset);
        Assert.Equal(0x80000005U, limits.FramebufferColorSampleCounts);
        Assert.True(limits.TimestampComputeAndGraphics);
        Assert.Equal(0.125F, limits.TimestampPeriod);
        Assert.Equal(new uint[] { 4, 5, 6 }, limits.MaxComputeWorkGroupCount!.Value.ToArray());
        Assert.Equal(new float[] { -2.5F, 2.5F }, limits.ViewportBoundsRange!.Value.ToArray());
        Assert.Equal(new float[] { 1F, 64F }, limits.PointSizeRange!.Value.ToArray());
        Assert.Equal(ulong.MaxValue, limits.NonCoherentAtomSize);
        Assert.All(WindowsVulkanCoreLimitsCatalog.Bindings,
            binding => Assert.True(WindowsVulkanCoreLimitsCatalog.IsValidValue(binding, limits)));

        var invalidBool = (byte[])properties.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(invalidBool.AsSpan(limitsOffset + 420, 4), 2);
        Assert.ThrowsAny<InvalidOperationException>(() =>
            WindowsVulkanCapabilityNativeReader.DecodeIdentityResponse(
                invalidBool, idProperties, scope.Adapters, WindowsVulkanCapabilityFields.CoreLimits));
        Assert.Throws<ArgumentException>(() =>
            WindowsVulkanCapabilityNativeReader.DecodeIdentityResponse(
                properties[..815], idProperties, scope.Adapters, WindowsVulkanCapabilityFields.CoreLimits));
    }

    [Fact]
    public void SyntheticVkPhysicalDeviceProperties2ResponseUsesProductionIdentityAndLimitDecoder()
    {
        Assert.Equal(8, IntPtr.Size);
        // Literal Windows x64 offsets from the pinned Khronos C layout, independent of AbiLayout/Marshal.OffsetOf.
        const int propertiesPayloadOffset = 16;
        const int properties2ResponseLength = propertiesPayloadOffset + 4096;
        const int propertiesApiVersionOffset = propertiesPayloadOffset;
        const int propertiesVendorIdOffset = propertiesPayloadOffset + 8;
        const int propertiesDeviceIdOffset = propertiesPayloadOffset + 12;
        const int maxImageDimension1DOffset = 312;
        const int maxImageDimension2DOffset = 316;
        const int maxImageDimension3DOffset = 320;
        const int maxImageDimensionCubeOffset = 324;
        const int maxImageArrayLayersOffset = 328;
        const int idDeviceLuidOffset = 48;
        const int idDeviceNodeMaskOffset = 56;
        const int idDeviceLuidValidOffset = 60;

        var properties2Response = new byte[properties2ResponseLength];
        BinaryPrimitives.WriteUInt32LittleEndian(properties2Response.AsSpan(0, 4), 1000059001);
        BinaryPrimitives.WriteUInt32LittleEndian(properties2Response.AsSpan(propertiesApiVersionOffset, 4),
            (1U << 22) | (3U << 12) | 280U);
        BinaryPrimitives.WriteUInt32LittleEndian(properties2Response.AsSpan(propertiesVendorIdOffset, 4), 0x10DE);
        BinaryPrimitives.WriteUInt32LittleEndian(properties2Response.AsSpan(propertiesDeviceIdOffset, 4), 0x2684);
        BinaryPrimitives.WriteUInt32LittleEndian(properties2Response.AsSpan(maxImageDimension1DOffset, 4), 0x01020304);
        BinaryPrimitives.WriteUInt32LittleEndian(properties2Response.AsSpan(maxImageDimension2DOffset, 4), 0x11223344);
        BinaryPrimitives.WriteUInt32LittleEndian(properties2Response.AsSpan(maxImageDimension3DOffset, 4), 0xA1B2C3D4);
        BinaryPrimitives.WriteUInt32LittleEndian(properties2Response.AsSpan(maxImageDimensionCubeOffset, 4), 0x55667788);
        BinaryPrimitives.WriteUInt32LittleEndian(properties2Response.AsSpan(maxImageArrayLayersOffset, 4), 0x10203040);

        var idPropertiesResponse = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(idPropertiesResponse.AsSpan(0, 4), 1000071004);
        BinaryPrimitives.WriteUInt64LittleEndian(idPropertiesResponse.AsSpan(idDeviceLuidOffset, 8), FirstAdapter.Luid);
        BinaryPrimitives.WriteUInt32LittleEndian(idPropertiesResponse.AsSpan(idDeviceNodeMaskOffset, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(idPropertiesResponse.AsSpan(idDeviceLuidValidOffset, 4), 1);

        Assert.Equal(4112, properties2Response.Length);
        Assert.Equal(64, idPropertiesResponse.Length);
        var allLimits = WindowsVulkanCapabilityFields.ImageDimensionLimits
            | WindowsVulkanCapabilityFields.CubeArrayLimits;
        var scope = new WindowsVulkanCapabilityScope(allLimits, [FirstAdapter]);
        var decoded = WindowsVulkanCapabilityNativeReader.DecodeIdentityResponse(
            properties2Response, idPropertiesResponse, scope.Adapters, allLimits);

        Assert.True(decoded.HasValue);
        Assert.Equal(FirstAdapter.Luid, decoded.Value.Luid);
        Assert.Equal(1U, decoded.Value.DeviceNodeMask);
        Assert.Equal((1U << 22) | (3U << 12) | 280U, decoded.Value.ApiVersion);
        Assert.Equal(0x10DEU, decoded.Value.VendorId);
        Assert.Equal(0x2684U, decoded.Value.DeviceId);
        Assert.Equal(FirstAdapter, decoded.Value.MatchedAdapter);
        Assert.NotNull(decoded.Value.CoreLimits);
        Assert.Equal(new WindowsVulkanCoreLimitsRead(0x01020304, 0x11223344, 0xA1B2C3D4, 0x55667788, 0x10203040),
            decoded.Value.CoreLimits);

        var selectedOneAndThree = WindowsVulkanCapabilityFields.MaxImageDimension1D
            | WindowsVulkanCapabilityFields.MaxImageDimension3D;
        var omitted = WindowsVulkanCapabilityNativeReader.DecodeIdentityResponse(
            properties2Response, idPropertiesResponse, scope.Adapters, selectedOneAndThree);
        Assert.True(omitted.HasValue);
        Assert.NotNull(omitted.Value.CoreLimits);
        Assert.Equal(new WindowsVulkanCoreLimitsRead(0x01020304, null, 0xA1B2C3D4, null, null),
            omitted.Value.CoreLimits);

        var oldLimitsOnly = WindowsVulkanCapabilityFields.ImageDimensionLimits;
        var oldScope = new WindowsVulkanCapabilityScope(oldLimitsOnly, [FirstAdapter]);
        var oldOnlyDecoded = WindowsVulkanCapabilityNativeReader.DecodeIdentityResponse(
            properties2Response, idPropertiesResponse, oldScope.Adapters, oldLimitsOnly);
        Assert.True(oldOnlyDecoded.HasValue);
        Assert.Equal(new WindowsVulkanCoreLimitsRead(0x01020304, 0x11223344, 0xA1B2C3D4, null, null),
            oldOnlyDecoded.Value.CoreLimits);

        var wrongPciProperties = (byte[])properties2Response.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongPciProperties.AsSpan(propertiesVendorIdOffset, 4), 0x8086);
        BinaryPrimitives.WriteUInt32LittleEndian(wrongPciProperties.AsSpan(propertiesDeviceIdOffset, 4), 0x46A6);
        var wrongPci = WindowsVulkanCapabilityNativeReader.DecodeIdentityResponse(
            wrongPciProperties, idPropertiesResponse, scope.Adapters, allLimits);
        Assert.True(wrongPci.HasValue);
        Assert.Null(wrongPci.Value.MatchedAdapter);
        Assert.Null(wrongPci.Value.CoreLimits);

        var wrongLuidIdProperties = (byte[])idPropertiesResponse.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(wrongLuidIdProperties.AsSpan(idDeviceLuidOffset, 8),
            FirstAdapter.Luid + 1);
        var wrongLuid = WindowsVulkanCapabilityNativeReader.DecodeIdentityResponse(
            properties2Response, wrongLuidIdProperties, scope.Adapters, allLimits);
        Assert.True(wrongLuid.HasValue);
        Assert.Null(wrongLuid.Value.MatchedAdapter);
        Assert.Null(wrongLuid.Value.CoreLimits);

        Assert.Throws<ArgumentException>(() => WindowsVulkanCapabilityNativeReader.DecodeIdentityResponse(
            properties2Response[..367], idPropertiesResponse, scope.Adapters, allLimits));
        Assert.Throws<ArgumentException>(() => WindowsVulkanCapabilityNativeReader.DecodeIdentityResponse(
            properties2Response, idPropertiesResponse[..63], scope.Adapters, allLimits));
    }

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(static ()
            => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private static IEnumerable<WindowsD3dAdapterIdentity> AdapterIdentities(int count)
    {
        for (var index = 1; index <= count; index++)
            yield return new((ulong)index, 0x1234, checked((ushort)(0x1000 + index)));
    }

    private static WindowsVulkanCapabilityReadResult ReadResult(WindowsVulkanCapabilityScope scope,
        WindowsVulkanCapabilityFields selected, int minTexelOffset)
        => new(true, [.. scope.Adapters.Select((adapter, index) =>
        {
            ImmutableArray<string>? extensions = (selected & WindowsVulkanCapabilityFields.Extensions) != 0
                ? index == 0 ? ImmutableArray.Create("VK_KHR_swapchain", "VK_EXT_descriptor_indexing")
                    : ImmutableArray<string>.Empty
                : null;
            ImmutableArray<WindowsVulkanFeatureRead>? features = (selected & WindowsVulkanCapabilityFields.CoreFeatures) != 0
                ? ImmutableArray.CreateRange(WindowsVulkanCore10FeatureCatalog.Names.Select((name, featureIndex)
                    => new WindowsVulkanFeatureRead(name, index == 0 && featureIndex == 0)))
                : null;
            var imageLimitFields = WindowsVulkanCapabilityFields.ImageDimensionLimits
                | WindowsVulkanCapabilityFields.CubeArrayLimits
                | WindowsVulkanCapabilityFields.CoreLimits;
            WindowsVulkanCoreLimitsRead? limits = (selected & imageLimitFields) != 0
                ? CreateCoreLimitsRead(selected, index, minTexelOffset)
                : null;
            var version = (selected & WindowsVulkanCapabilityFields.ApiVersion) != 0
                ? index == 0 ? "1.3.280" : "1.2.198"
                : null;
            return new WindowsVulkanAdapterCapabilityRead(adapter, version, extensions, features)
            { CoreLimits = limits };
        })]);

    private static WindowsVulkanCoreLimitsRead CreateCoreLimitsRead(
        WindowsVulkanCapabilityFields selected, int adapterIndex, int minTexelOffset)
    {
        var limits = new WindowsVulkanCoreLimitsRead(
            (selected & WindowsVulkanCapabilityFields.MaxImageDimension1D) != 0 ? adapterIndex == 0 ? 16384U : 8192U : null,
            (selected & WindowsVulkanCapabilityFields.MaxImageDimension2D) != 0 ? adapterIndex == 0 ? 8192U : 4096U : null,
            (selected & WindowsVulkanCapabilityFields.MaxImageDimension3D) != 0 ? adapterIndex == 0 ? 2048U : 1024U : null,
            (selected & WindowsVulkanCapabilityFields.MaxImageDimensionCube) != 0 ? adapterIndex == 0 ? 4096U : 2048U : null,
            (selected & WindowsVulkanCapabilityFields.MaxImageArrayLayers) != 0 ? adapterIndex == 0 ? 256U : 128U : null);
        if ((selected & WindowsVulkanCapabilityFields.CoreLimits) == 0) return limits;
        return limits with
        {
            MaxTexelBufferElements = 0xF1234567U,
            MaxUniformBufferRange = 0U,
            MaxStorageBufferRange = 0U,
            MaxPushConstantsSize = 0U,
            MaxMemoryAllocationCount = 0U,
            MaxSamplerAllocationCount = 0U,
            BufferImageGranularity = ulong.MaxValue - 1,
            SparseAddressSpaceSize = ulong.MaxValue,
            MaxBoundDescriptorSets = 0U,
            MaxPerStageDescriptorSamplers = 0U,
            MaxPerStageDescriptorUniformBuffers = 0U,
            MaxPerStageDescriptorStorageBuffers = 0U,
            MaxPerStageDescriptorSampledImages = 0U,
            MaxPerStageDescriptorStorageImages = 0U,
            MaxPerStageDescriptorInputAttachments = 0U,
            MaxPerStageResources = 0U,
            MaxDescriptorSetSamplers = 0U,
            MaxDescriptorSetUniformBuffers = 0U,
            MaxDescriptorSetUniformBuffersDynamic = 0U,
            MaxDescriptorSetStorageBuffers = 0U,
            MaxDescriptorSetStorageBuffersDynamic = 0U,
            MaxDescriptorSetSampledImages = 0U,
            MaxDescriptorSetStorageImages = 0U,
            MaxDescriptorSetInputAttachments = 0U,
            MaxVertexInputAttributes = 0U,
            MaxVertexInputBindings = 0U,
            MaxVertexInputAttributeOffset = 0U,
            MaxVertexInputBindingStride = 0U,
            MaxVertexOutputComponents = 0U,
            MaxTessellationGenerationLevel = 0U,
            MaxTessellationPatchSize = 0U,
            MaxTessellationControlPerVertexInputComponents = 0U,
            MaxTessellationControlPerVertexOutputComponents = 0U,
            MaxTessellationControlPerPatchOutputComponents = 0U,
            MaxTessellationControlTotalOutputComponents = 0U,
            MaxTessellationEvaluationInputComponents = 0U,
            MaxTessellationEvaluationOutputComponents = 0U,
            MaxGeometryShaderInvocations = 0U,
            MaxGeometryInputComponents = 0U,
            MaxGeometryOutputComponents = 0U,
            MaxGeometryOutputVertices = 0U,
            MaxGeometryTotalOutputComponents = 0U,
            MaxFragmentInputComponents = 0U,
            MaxFragmentOutputAttachments = 0U,
            MaxFragmentDualSrcAttachments = 0U,
            MaxFragmentCombinedOutputResources = 0U,
            MaxComputeSharedMemorySize = 0U,
            MaxComputeWorkGroupCount = ImmutableArray.Create(4U, 5U, 6U),
            MaxComputeWorkGroupInvocations = 0U,
            MaxComputeWorkGroupSize = ImmutableArray.Create(8U, 9U, 10U),
            SubPixelPrecisionBits = 0U,
            SubTexelPrecisionBits = 0U,
            MipmapPrecisionBits = 0U,
            MaxDrawIndexedIndexValue = 0U,
            MaxDrawIndirectCount = 0U,
            MaxSamplerLodBias = 3.5F,
            MaxSamplerAnisotropy = 0F,
            MaxViewports = 0U,
            MaxViewportDimensions = ImmutableArray.Create(1920U, 1080U),
            ViewportBoundsRange = ImmutableArray.Create(-2.5F, 2.5F),
            ViewportSubPixelBits = 0U,
            MinMemoryMapAlignment = unchecked((nuint)ulong.MaxValue),
            MinTexelBufferOffsetAlignment = ulong.MaxValue,
            MinUniformBufferOffsetAlignment = ulong.MaxValue,
            MinStorageBufferOffsetAlignment = ulong.MaxValue,
            MinTexelOffset = checked(minTexelOffset - adapterIndex),
            MaxTexelOffset = 0U,
            MinTexelGatherOffset = -7,
            MaxTexelGatherOffset = 0U,
            MinInterpolationOffset = 0F,
            MaxInterpolationOffset = 0F,
            SubPixelInterpolationOffsetBits = 0U,
            MaxFramebufferWidth = 0U,
            MaxFramebufferHeight = 0U,
            MaxFramebufferLayers = 0U,
            FramebufferColorSampleCounts = 0x80000005U,
            FramebufferDepthSampleCounts = 0U,
            FramebufferStencilSampleCounts = 0U,
            FramebufferNoAttachmentsSampleCounts = 0U,
            MaxColorAttachments = 0U,
            SampledImageColorSampleCounts = 0U,
            SampledImageIntegerSampleCounts = 0U,
            SampledImageDepthSampleCounts = 0U,
            SampledImageStencilSampleCounts = 0U,
            StorageImageSampleCounts = 0U,
            MaxSampleMaskWords = 0U,
            TimestampComputeAndGraphics = true,
            TimestampPeriod = 0.125F,
            MaxClipDistances = 0U,
            MaxCullDistances = 0U,
            MaxCombinedClipAndCullDistances = 0U,
            DiscreteQueuePriorities = 0U,
            PointSizeRange = ImmutableArray.Create(1F, 64F),
            LineWidthRange = ImmutableArray.Create(1F, 8F),
            PointSizeGranularity = 0F,
            LineWidthGranularity = 0F,
            StrictLines = false,
            StandardSampleLocations = true,
            OptimalBufferCopyOffsetAlignment = ulong.MaxValue,
            OptimalBufferCopyRowPitchAlignment = ulong.MaxValue,
            NonCoherentAtomSize = ulong.MaxValue
        };
    }
    private sealed class RecordedReader : IWindowsVulkanCapabilityReader
    {
        private readonly ConcurrentQueue<WindowsVulkanCapabilityFields> requests = new();
        public IReadOnlyList<WindowsVulkanCapabilityFields> Requests => requests.ToArray();
        public string ApiVersion { get; set; } = "1.3.280";
        public uint MaxImageDimension2D { get; set; } = 8192;
        public uint MaxImageDimensionCube { get; set; } = 4096;
        public uint MaxImageArrayLayers { get; set; }
        public int MinTexelOffset { get; set; } = -17;
        public RecordedMode Mode { get; set; }
        public Action? AfterRead { get; set; }

        public WindowsVulkanCapabilityReadResult Read(WindowsVulkanCapabilityScope scope,
            WindowsVulkanCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            if (Mode == RecordedMode.DirectoryUnavailable)
                return WindowsVulkanCapabilityReadResult.Unavailable;
            if (Mode == RecordedMode.ForeignAdapter)
                return new(true, [new(SecondAdapter, ApiVersion, null, null)]);

            var result = ReadResult(scope, selectedFields, MinTexelOffset);
            if ((selectedFields & WindowsVulkanCapabilityFields.ApiVersion) != 0 && Mode == RecordedMode.Current)
                result = result with { Adapters = [.. result.Adapters.Select(row => row with { ApiVersion = ApiVersion })] };
            if ((selectedFields & WindowsVulkanCapabilityFields.CoreFeatures) != 0
                && Mode == RecordedMode.FeaturesUnavailable)
                result = result with { Adapters = [.. result.Adapters.Select(row => row with { CoreFeatures = null })] };
            if ((selectedFields & (WindowsVulkanCapabilityFields.MaxImageDimension2D
                    | WindowsVulkanCapabilityFields.MaxImageDimensionCube
                    | WindowsVulkanCapabilityFields.MaxImageArrayLayers)) != 0)
                result = result with { Adapters = [.. result.Adapters.Select(row => row with
                {
                    CoreLimits = row.CoreLimits is { } limits
                        ? limits with
                        {
                            MaxImageDimension2D = (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimension2D) != 0
                                ? MaxImageDimension2D : limits.MaxImageDimension2D,
                            MaxImageDimensionCube = (selectedFields & WindowsVulkanCapabilityFields.MaxImageDimensionCube) != 0
                                ? MaxImageDimensionCube : limits.MaxImageDimensionCube,
                            MaxImageArrayLayers = (selectedFields & WindowsVulkanCapabilityFields.MaxImageArrayLayers) != 0
                                ? MaxImageArrayLayers : limits.MaxImageArrayLayers
                        }
                        : null
                })] };
            if ((selectedFields & (WindowsVulkanCapabilityFields.ImageDimensionLimits
                    | WindowsVulkanCapabilityFields.CubeArrayLimits
                    | WindowsVulkanCapabilityFields.CoreLimits)) != 0
                && Mode == RecordedMode.LimitsUnavailable)
                result = result with { Adapters = [.. result.Adapters.Select(row => row with { CoreLimits = null })] };
            if (Mode == RecordedMode.DuplicateAdapter)
                result = result with { Adapters = [.. result.Adapters, result.Adapters[0]] };
            AfterRead?.Invoke();
            return result;
        }
    }

    private enum RecordedMode { Current, DirectoryUnavailable, ForeignAdapter, DuplicateAdapter,
        FeaturesUnavailable, LimitsUnavailable }

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
