using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Usb;

namespace Resource_Manager_APP.Tests;

public sealed class UsbSpeedInformationProjectionTests
{
    private const string NegotiatedName = "usb_all_link_speed_negotiated";
    private const string CapabilityName = "usb_all_link_speed_capability";

    [Fact]
    public void BothOriginalProjectionsBindExactRatesToTheUniqueAttachedPnpDevice()
    {
        var port = Port();
        var device = NativeDevice(UsbParentPortInformationCollectorTests.DriverKey);
        var keyIndex = WindowsUsbHubIoctlReader.CreateDriverKeyIndex([port]);
        Assert.Same(port, WindowsDeviceTopologyReader.FindUsbPort(device, keyIndex));

        var capture = new DeviceTopologyUsbPortSnapshot([port], keyIndex, null);
        var direct = WindowsUsbHubIoctlReader.ProjectFields(capture, [device], DateTimeOffset.UtcNow);
        Assert.Equal<double?>(5000d, Number(FindField(direct, NegotiatedName)));
        Assert.Equal<double?>(5000d, Number(FindField(direct, CapabilityName)));

        var connection = WindowsDeviceTopologyReader.CreateUsbConnection(port, publishActualParentPort: true)!;
        var topology = DeviceTopologyInformationProjection.Create(
            UsbParentPortInformationCollectorTests.Snapshot(connection), DateTimeOffset.UtcNow);
        Assert.Equal<double?>(5000d, Number(FindField(topology, NegotiatedName)));
        Assert.Equal<double?>(5000d, Number(FindField(topology, CapabilityName)));

        foreach (var field in direct.Devices.SelectMany(static item => item.Fields)
            .Where(static item => item.Definition.Name is NegotiatedName or CapabilityName))
        {
            Assert.Equal("number", field.Definition.ValueKind);
            Assert.Equal("Mbit/s", field.Definition.Unit);
            Assert.Equal("windows.usb-hub-connection", field.Definition.SourceId);
            Assert.Equal(field.Definition.Name == NegotiatedName
                ? "usb.attached_device_current_negotiated_link_rate"
                : "usb.attached_device_bos_declared_maximum_link_rate", field.Definition.Quantity);
        }
    }

    [Fact]
    public void DuplicateOrDisconnectedDriverKeyDoesNotAttachRateValues()
    {
        var first = Port();
        var duplicate = first with
        {
            HubDevicePath = "another hub",
            PortNumber = 9,
            DriverKeyName = " " + UsbParentPortInformationCollectorTests.DriverKey.ToLowerInvariant() + " "
        };
        var device = NativeDevice(UsbParentPortInformationCollectorTests.DriverKey);
        var ambiguousIndex = WindowsUsbHubIoctlReader.CreateDriverKeyIndex([first, duplicate]);
        Assert.Empty(ambiguousIndex);
        Assert.Null(WindowsDeviceTopologyReader.FindUsbPort(device, ambiguousIndex));
        var ambiguous = WindowsUsbHubIoctlReader.ProjectFields(
            new([first, duplicate], ambiguousIndex, null), [device], DateTimeOffset.UtcNow);
        Assert.Empty(ambiguous.Devices);

        var disconnected = first with { DeviceConnected = false };
        var disconnectedIndex = WindowsUsbHubIoctlReader.CreateDriverKeyIndex([disconnected]);
        Assert.Empty(disconnectedIndex);
        Assert.Empty(WindowsUsbHubIoctlReader.ProjectFields(
            new([disconnected], disconnectedIndex, null), [device], DateTimeOffset.UtcNow).Devices);
    }

    [Fact]
    public async Task InformationBackendReadsSameCurrentValuesAndDoesNotKeepRetiredFields()
    {
        var usb = DeviceTopologyUsbInformationFor(Port());
        var observedAt = DateTimeOffset.UtcNow;
        var current = WindowsInformationFieldProjection.UsbDevices([usb], observedAt);
        var captureCount = 0;
        var backend = new InformationReadWriteBackend(
            () => current,
            _ =>
            {
                captureCount++;
                return ValueTask.FromResult(current);
            },
            _ => new OperationSupport(true, null, []),
            (_, _) =>
            {
                captureCount++;
                return ValueTask.FromResult(current);
            });
        var core = new ReadWriteCore(backend, new NoDependencies());
        var field = new FieldReference(UsbParentPortInformationCollectorTests.Identity, NegotiatedName);

        Assert.Equal<double?>(5000d, core.ReadCached(field).Value!.Number);
        Assert.Equal(0, captureCount);

        var reading = Assert.Single(await core.CollectAsync(ImmutableArray.Create(field)));
        Assert.Equal(FieldObservationStatus.Current, reading.Status);
        Assert.Equal<double?>(5000d, reading.Value!.Number);
        Assert.Equal(1, captureCount);

        current = DeviceInformationSnapshot.Empty;
        Assert.Throws<ArgumentException>(() => core.ReadCached(field));
    }

    [Fact]
    public async Task SelectedSourceFailureAndCancellationDoNotExposeStaleRates()
    {
        var current = WindowsInformationFieldProjection.UsbDevices(
            [DeviceTopologyUsbInformationFor(Port())], DateTimeOffset.UtcNow);
        var field = new FieldReference(UsbParentPortInformationCollectorTests.Identity, NegotiatedName);
        var failingBackend = new InformationReadWriteBackend(
            () => current,
            _ => ValueTask.FromResult(current),
            _ => new OperationSupport(true, null, []),
            (_, _) => ValueTask.FromResult(DeviceInformationSnapshot.Empty));
        var failingCore = new ReadWriteCore(failingBackend, new NoDependencies());
        var failed = Assert.Single(await failingCore.CollectAsync(ImmutableArray.Create(field)));
        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status);
        Assert.Null(failed.Value);

        var captureCount = 0;
        var cancelBackend = new InformationReadWriteBackend(
            () => current,
            _ =>
            {
                captureCount++;
                return ValueTask.FromResult(current);
            },
            _ => new OperationSupport(true, null, []));
        var cancelCore = new ReadWriteCore(cancelBackend, new NoDependencies());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelCore.CollectAsync(ImmutableArray.Create(field), cancellation.Token));
        Assert.Equal(0, captureCount);
    }

    [Fact]
    public void MissingEvidenceProducesNullValuesAndDisconnectedDeviceIsRetired()
    {
        var noEvidence = Port() with
        {
            NegotiatedLinkSpeedMegabitsPerSecond = null,
            Descriptor = Port().Descriptor with { DeclaredMaximumLinkSpeedMegabitsPerSecond = null }
        };
        var noEvidenceFields = DeviceTopologyInformationProjection.Create(
            UsbParentPortInformationCollectorTests.Snapshot(
                WindowsDeviceTopologyReader.CreateUsbConnection(noEvidence, true)!), DateTimeOffset.UtcNow);
        Assert.Null(FindField(noEvidenceFields, NegotiatedName).Value);
        Assert.Null(FindField(noEvidenceFields, CapabilityName).Value);

        var disconnected = WindowsDeviceTopologyReader.CreateUsbConnection(Port(), true)! with { DeviceConnected = false };
        var retired = DeviceTopologyInformationProjection.Create(
            UsbParentPortInformationCollectorTests.Snapshot(disconnected), DateTimeOffset.UtcNow);
        Assert.DoesNotContain(retired.Devices.SelectMany(static item => item.Fields),
            static item => item.Definition.Name is NegotiatedName or CapabilityName);

        var connectorRepresentative = WindowsDeviceTopologyReader.CreateUsbConnection(
            Port(), publishActualParentPort: false)!;
        var representativeFields = DeviceTopologyInformationProjection.Create(
            UsbParentPortInformationCollectorTests.Snapshot(connectorRepresentative), DateTimeOffset.UtcNow);
        Assert.DoesNotContain(representativeFields.Devices.SelectMany(static item => item.Fields),
            static item => item.Definition.Name is NegotiatedName or CapabilityName);
    }

    private static DeviceTopologyUsbPort Port()
        => UsbParentPortInformationCollectorTests.Raw() with
        {
            NegotiatedLinkSpeedMegabitsPerSecond = 5000d,
            Descriptor = UsbParentPortInformationCollectorTests.Raw().Descriptor with
            {
                DeclaredMaximumLinkSpeedMegabitsPerSecond = 5000d
            }
        };

    private static DeviceTopologyUsbInformation DeviceTopologyUsbInformationFor(DeviceTopologyUsbPort port)
        => new(UsbParentPortInformationCollectorTests.Identity, "USB#ROOT_HUB#INSTANCE|7",
            port.Descriptor.ManufacturerName, port.Descriptor.ProductName, port.Descriptor.SerialNumber,
            NegotiatedLinkSpeedMegabitsPerSecond: port.NegotiatedLinkSpeedMegabitsPerSecond,
            DeclaredMaximumLinkSpeedMegabitsPerSecond: port.DeclaredMaximumLinkSpeedMegabitsPerSecond);

    private static DeviceTopologyNativeDevice NativeDevice(string driverKey)
        => new(@"USB\DEVICE", null, null, null, null, driverKey, null, null, null, null, [], [], [], null, null, null);

    private static DeviceInformationField FindField(DeviceInformationSnapshot snapshot, string fieldName)
        => DeviceInformationTestData.Field(snapshot, UsbParentPortInformationCollectorTests.Identity, fieldName);

    private static double? Number(DeviceInformationField field)
        => field.Value is { } value && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private sealed class NoDependencies : IReadWriteDependencies
    {
        public DependencyStatus Query(string id) => new(id, DependencyAvailability.Missing, "not used by a read");
        public ValueTask<DependencyPackage> FetchAsync(string id, Uri source, string destination, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public ValueTask<DependencyActionResult> InstallAsync(DependencyPackage package, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
