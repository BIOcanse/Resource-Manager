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

public sealed class WindowsOpenClCapabilityInformationTests
{
    private static readonly WindowsOpenClAdapterTarget FirstTarget = new(0x0000_0001_0000_002A, 1);
    private static readonly WindowsOpenClAdapterTarget SecondTarget = new(0x0000_0002_0000_003B, 2);
    private static readonly WindowsOpenClDeviceIdentity FirstDevice = new(FirstTarget,
        "00112233445566778899AABBCCDDEEFF", "FFEEDDCCBBAA99887766554433221100");
    private static readonly WindowsOpenClDeviceIdentity SecondDevice = new(SecondTarget,
        "102132435465768798A9BACBDCEDFE0F", "00FFEEDDCCBBAA998877665544332211");

    [Fact]
    public async Task ExactLuidNodeDeviceAndDriverIdentityFlowsThroughTheOriginalCurrent()
    {
        var scope = new WindowsOpenClCapabilityScope(WindowsOpenClCapabilityFields.All, [FirstTarget, SecondTarget]);
        var reader = new RecordedReader();
        using var owner = EmptyOwner();
        var access = owner.CreateOpenClCapabilityInformation(scope, reader);

        Assert.Empty(access.Backend.Catalog.Fields);
        Assert.Empty(reader.Requests);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(WindowsOpenClCapabilityFields.All, Assert.Single(reader.Requests));
        var firstVersion = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_opencl_device_version"
            && field.Field.DeviceIdentity.Contains("luid:000000010000002A|node:00000001", StringComparison.Ordinal)).Field;
        var firstExtensions = catalog.Fields.Where(field => field.Field.FieldName == "gpu_all_opencl_extension"
            && field.Field.DeviceIdentity.Contains("luid:000000010000002A|node:00000001", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, firstExtensions.Length);
        Assert.Contains("device:00112233445566778899AABBCCDDEEFF|driver:FFEEDDCCBBAA99887766554433221100",
            firstVersion.DeviceIdentity, StringComparison.Ordinal);
        Assert.Contains(catalog.Devices, device => device.Identity == firstVersion.DeviceIdentity
            && device.Resource == "gpu_adapter");
        Assert.All(firstExtensions, field => Assert.Contains(catalog.Devices,
            device => device.Identity == field.Field.DeviceIdentity && device.Resource == "gpu_adapter_extension"));
        Assert.Equal("OpenCL 3.0 recorded", access.Backend.ReadCached(firstVersion).Value?.Text);

        var extensionField = firstExtensions.Single(field => field.Field.DeviceIdentity.EndsWith(
            "token-utf8-hex:636C5F6B68725F6465766963655F75756964", StringComparison.Ordinal)).Field;
        Assert.Equal("cl_khr_device_uuid", access.Backend.ReadCached(extensionField).Value?.Text);
        var readCount = reader.Requests.Count;
        _ = access.Backend.ReadCached(extensionField);
        Assert.Equal(readCount, reader.Requests.Count);

        var selectedVersion = Assert.Single(await access.Backend.CollectAsync([firstVersion], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Current, selectedVersion.Status);
        Assert.Equal("OpenCL 3.0 recorded", selectedVersion.Value?.Text);
        Assert.Equal(WindowsOpenClCapabilityFields.DeviceVersion, reader.Requests.Last());
        Assert.Equal("cl_khr_device_uuid", access.Backend.ReadCached(extensionField).Value?.Text);

        using var borrow = access.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([extensionField]));
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId, Assert.Single(binding.CollectionItemIds));
        var borrowed = await borrow.CollectAsync([extensionField], new Permit(), CancellationToken.None);
        Assert.True(borrowed.Committed);
        Assert.Equal(FieldObservationStatus.Current, Assert.Single(borrowed.Readings).Status);
        Assert.Equal(WindowsOpenClCapabilityFields.Extensions, reader.Requests.Last());
        Assert.Equal(borrowed.Readings[0], access.Backend.ReadCached(extensionField));
        Assert.NotNull(owner.ReadState().InformationFields.FindField(extensionField));

        await owner.SampleTopologyAsync(null, CancellationToken.None, requireDemand: false);
        Assert.Equal("OpenCL 3.0 recorded", access.Backend.ReadCached(firstVersion).Value?.Text);
        Assert.Equal("cl_khr_device_uuid", access.Backend.ReadCached(extensionField).Value?.Text);
    }

    [Fact]
    public async Task SelectedFailuresClearKnownValuesWhileSuccessEmptyRetiresAndCancellationDoesNotCommit()
    {
        var scope = new WindowsOpenClCapabilityScope(WindowsOpenClCapabilityFields.All, [FirstTarget]);
        var reader = new RecordedReader();
        using var owner = EmptyOwner();
        var access = owner.CreateOpenClCapabilityInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        var version = catalog.Fields.Single(field => field.Field.FieldName == "gpu_all_opencl_device_version").Field;
        var extension = catalog.Fields.First(field => field.Field.FieldName == "gpu_all_opencl_extension").Field;
        var extensionBefore = access.Backend.ReadCached(extension);
        var versionBefore = access.Backend.ReadCached(version);

        reader.Mode = RecordedMode.DirectoryUnavailable;
        var failed = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status);
        Assert.Null(failed.Value);
        Assert.Null(failed.ObservedAt);
        Assert.Equal(extensionBefore, access.Backend.ReadCached(extension));

        reader.Mode = RecordedMode.VersionUnavailable;
        var fieldFailed = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, fieldFailed.Status);
        Assert.Null(fieldFailed.Value);
        Assert.Equal(extensionBefore, access.Backend.ReadCached(extension));

        reader.Mode = RecordedMode.ForeignIdentity;
        var malformedState = owner.ReadState();
        var malformed = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, malformed.Status);
        Assert.Null(malformed.Value);
        Assert.NotSame(malformedState, owner.ReadState());
        Assert.Equal(extensionBefore, access.Backend.ReadCached(extension));

        reader.Mode = RecordedMode.DuplicateIdentity;
        var duplicateState = owner.ReadState();
        var duplicate = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, duplicate.Status);
        Assert.Null(duplicate.Value);
        Assert.NotSame(duplicateState, owner.ReadState());
        Assert.Equal(extensionBefore, access.Backend.ReadCached(extension));

        reader.Mode = RecordedMode.Current;
        _ = await access.Backend.CollectAsync([version], CancellationToken.None);
        var restoredVersion = access.Backend.ReadCached(version);
        Assert.Equal(FieldObservationStatus.Current, restoredVersion.Status);
        Assert.Equal("OpenCL 3.0 recorded", restoredVersion.Value?.Text);

        using var borrow = access.BorrowSubscriptionCapture();
        var beforeRejected = owner.ReadState();
        var rejected = await borrow.CollectAsync([version], new Permit(accept: false), CancellationToken.None);
        Assert.False(rejected.Committed);
        Assert.Same(beforeRejected, owner.ReadState());

        using var canceled = new CancellationTokenSource();
        reader.AfterRead = canceled.Cancel;
        var beforeCancel = owner.ReadState();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await access.Backend.CollectAsync([version], canceled.Token));
        Assert.Same(beforeCancel, owner.ReadState());
        reader.AfterRead = null;

        reader.Mode = RecordedMode.SuccessEmptyDirectory;
        var beforeRetire = owner.ReadState();
        var emptyDirectory = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, emptyDirectory.Status);
        Assert.NotSame(beforeRetire, owner.ReadState());
        Assert.DoesNotContain(owner.ReadState().InformationFields.Devices,
            device => device.Fields.Any(field => field.Definition.Name == "gpu_all_opencl_device_version"));
        Assert.Equal(extensionBefore, access.Backend.ReadCached(extension));

        reader.Mode = RecordedMode.Current;
        _ = await access.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal("OpenCL 3.0 recorded", access.Backend.ReadCached(version).Value?.Text);

        reader.Mode = RecordedMode.EmptyExtensions;
        var emptyExtensions = Assert.Single(await access.Backend.CollectAsync([extension], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, emptyExtensions.Status);
        Assert.Equal("OpenCL 3.0 recorded", access.Backend.ReadCached(version).Value?.Text);
        Assert.DoesNotContain(owner.ReadState().InformationFields.Devices,
            device => device.DeviceIdentity == extension.DeviceIdentity);
    }

    [Fact]
    public async Task InitialDirectoryFailureIsReportedAndSameOwnerRetryCanDiscoverADevice()
    {
        var scope = new WindowsOpenClCapabilityScope(WindowsOpenClCapabilityFields.DeviceVersion, [FirstTarget]);
        var reader = new RecordedReader { Mode = RecordedMode.DirectoryUnavailable };
        using var owner = EmptyOwner();
        var access = owner.CreateOpenClCapabilityInformation(scope, reader);
        var beforeFailure = owner.ReadState();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await access.Backend.DiscoverAsync(CancellationToken.None));
        Assert.NotSame(beforeFailure, owner.ReadState());
        Assert.Empty(owner.ReadState().InformationFields.Devices);

        reader.Mode = RecordedMode.Current;
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        var version = Assert.Single(catalog.Fields).Field;
        Assert.Equal("gpu_all_opencl_device_version", version.FieldName);
        Assert.Equal("OpenCL 3.0 recorded", access.Backend.ReadCached(version).Value?.Text);

        reader.Mode = RecordedMode.DirectoryUnavailable;
        var selectedFailure = Assert.Single(await access.Backend.CollectAsync([version], CancellationToken.None));
        Assert.Equal(FieldObservationStatus.Unavailable, selectedFailure.Status);
        Assert.Null(selectedFailure.Value);
        Assert.Null(selectedFailure.ObservedAt);

        var emptyReader = new RecordedReader { Mode = RecordedMode.SuccessEmptyDirectory };
        using var emptyOwner = EmptyOwner();
        var emptyAccess = emptyOwner.CreateOpenClCapabilityInformation(scope, emptyReader);
        var emptyCatalog = await emptyAccess.Backend.DiscoverAsync(CancellationToken.None);
        Assert.Empty(emptyCatalog.Fields);
    }

    [Fact]
    public void ScopeBoundsAreFiniteConfigurableAndRejectNonSingleBitNodes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsOpenClAdapterTarget(1, 3));
        Assert.Throws<ArgumentException>(() => new WindowsOpenClCapabilityScope(
            WindowsOpenClCapabilityFields.All, [FirstTarget, FirstTarget]));

        var targets = Enumerable.Range(1, 65).Select(index => new WindowsOpenClAdapterTarget((ulong)index, 1)).ToArray();
        var expanded = new WindowsOpenClCapabilityScope(WindowsOpenClCapabilityFields.DeviceVersion, targets,
            maximumTargetCount: 65, maximumPlatformCount: 65, maximumDeviceCount: 2048,
            maximumExtensionCount: 8192, maximumTextBytes: 131072);
        Assert.Equal(65, expanded.Targets.Length);
        Assert.Equal(65, expanded.MaximumPlatformCount);
        Assert.Equal(2048, expanded.MaximumDeviceCount);
        Assert.Equal(8192, expanded.MaximumExtensionCount);
        Assert.Equal(131072, expanded.MaximumTextBytes);

        var consumed = 0;
        Assert.Throws<ArgumentException>(() => new WindowsOpenClCapabilityScope(
            WindowsOpenClCapabilityFields.DeviceVersion, InfiniteTargets(), maximumTargetCount: 2));
        Assert.Equal(3, consumed);

        IEnumerable<WindowsOpenClAdapterTarget> InfiniteTargets()
        {
            for (ulong index = 1; ; index++)
            {
                consumed++;
                yield return new(index, 1);
            }
        }
    }

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(static ()
            => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private sealed class RecordedReader : IWindowsOpenClCapabilityReader
    {
        private readonly ConcurrentQueue<WindowsOpenClCapabilityFields> requests = new();
        public IReadOnlyList<WindowsOpenClCapabilityFields> Requests => requests.ToArray();
        public RecordedMode Mode { get; set; }
        public Action? AfterRead { get; set; }

        public WindowsOpenClCapabilityReadResult Read(WindowsOpenClCapabilityScope scope,
            WindowsOpenClCapabilityFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            if (Mode == RecordedMode.DirectoryUnavailable)
                return WindowsOpenClCapabilityReadResult.Unavailable;
            if (Mode == RecordedMode.SuccessEmptyDirectory)
                return new(true, []);

            var rows = ImmutableArray.CreateBuilder<WindowsOpenClDeviceCapabilityRead>();
            foreach (var target in scope.Targets)
            {
                var identity = Mode == RecordedMode.ForeignIdentity
                    ? new WindowsOpenClDeviceIdentity(new(target.DxgiLuid + 1, target.NodeMask),
                        FirstDevice.DeviceUuidHex, FirstDevice.DriverUuidHex)
                    : target == FirstTarget ? FirstDevice : SecondDevice;
                var version = (selectedFields & WindowsOpenClCapabilityFields.DeviceVersion) != 0
                    && Mode != RecordedMode.VersionUnavailable ? "OpenCL 3.0 recorded" : null;
                ImmutableArray<string>? extensions = null;
                if ((selectedFields & WindowsOpenClCapabilityFields.Extensions) != 0)
                    extensions = Mode == RecordedMode.EmptyExtensions
                        ? ImmutableArray<string>.Empty
                        : ImmutableArray.Create("cl_khr_device_uuid", "cl_khr_fp64", "cl_ext_recorded");
                rows.Add(new(identity, version, extensions));
            }
            AfterRead?.Invoke();
            var resultRows = rows.ToImmutable();
            if (Mode == RecordedMode.DuplicateIdentity && !resultRows.IsEmpty)
                resultRows = resultRows.Add(resultRows[0]);
            return new(true, resultRows);
        }
    }

    private enum RecordedMode
    {
        Current,
        DirectoryUnavailable,
        VersionUnavailable,
        EmptyExtensions,
        SuccessEmptyDirectory,
        ForeignIdentity,
        DuplicateIdentity
    }

    private sealed class Permit(bool accept = true) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit)
        {
            if (!accept) return false;
            commit();
            return true;
        }
    }
}
