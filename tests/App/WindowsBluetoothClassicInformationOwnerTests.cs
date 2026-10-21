using System.Collections;
using System.Collections.Concurrent;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Bluetooth;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsBluetoothClassicInformationOwnerTests
{
    private const string RadioA = "00:11:22:33:44:01";
    private const string RadioB = "00:11:22:33:44:02";
    private const string Remote = "AA:BB:CC:DD:EE:01";

    [Fact]
    public async Task ExplicitDiscoveryUsesCanonicalFieldsAndKeepsTheSameRemoteSeparatePerRadio()
    {
        var scope = Scope();
        var source = new RecordedReader((_, selected, _) => Result(scope, selected,
            [RadioA, RadioB],
            [new(RadioA), new(RadioB)],
            [new(RadioA, Remote, true, false), new(RadioB, Remote, false, true)]));
        using var owner = EmptyOwner();
        var access = owner.CreateBluetoothClassicInformation(scope, source);

        Assert.Empty(access.Backend.Catalog.Fields);
        Assert.Empty(source.Requests);
        var catalog = await access.Backend.DiscoverAsync(default);
        Assert.Equal(WindowsBluetoothClassicInformationFields.All, Assert.Single(source.Requests));
        Assert.Equal(6, catalog.Fields.Length);
        Assert.Equal(new[]
        {
            "bluetooth_all_controller_id_declared",
            "bluetooth_all_device_connected_actual",
            "bluetooth_all_device_paired_actual"
        }, catalog.Fields.Select(static item => item.Field.FieldName).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal("bluetooth_controller", catalog.Devices.Single(device => device.Identity == ControllerIdentity(RadioA)).Resource);
        Assert.Equal(2, catalog.Devices.Count(static device => device.Resource == "bluetooth_device"));
        Assert.Equal("text", DeviceInformationFieldRegistry.BluetoothAllControllerIdDeclared.ValueKind);
        Assert.Equal("string", catalog.Fields.Single(static item => item.Field.DeviceIdentity == ControllerIdentity(RadioA)
            && item.Field.FieldName == "bluetooth_all_controller_id_declared").Definition.ValueKind);
        Assert.Equal("text", catalog.Fields.Single(static item => item.Field.DeviceIdentity == ControllerIdentity(RadioA)
            && item.Field.FieldName == "bluetooth_all_controller_id_declared").Definition.Unit);
        Assert.Equal("boolean", catalog.Fields.Single(static item => item.Field.DeviceIdentity == DeviceIdentity(RadioA, Remote)
            && item.Field.FieldName == "bluetooth_all_device_connected_actual").Definition.ValueKind);
        Assert.Equal("state", catalog.Fields.Single(static item => item.Field.DeviceIdentity == DeviceIdentity(RadioA, Remote)
            && item.Field.FieldName == "bluetooth_all_device_connected_actual").Definition.Unit);
        Assert.Equal("boolean", catalog.Fields.Single(static item => item.Field.DeviceIdentity == DeviceIdentity(RadioA, Remote)
            && item.Field.FieldName == "bluetooth_all_device_paired_actual").Definition.ValueKind);
        Assert.Equal("state", catalog.Fields.Single(static item => item.Field.DeviceIdentity == DeviceIdentity(RadioA, Remote)
            && item.Field.FieldName == "bluetooth_all_device_paired_actual").Definition.Unit);

        var connectedA = new FieldReference(DeviceIdentity(RadioA, Remote), "bluetooth_all_device_connected_actual");
        var pairedA = new FieldReference(DeviceIdentity(RadioA, Remote), "bluetooth_all_device_paired_actual");
        var connectedB = new FieldReference(DeviceIdentity(RadioB, Remote), "bluetooth_all_device_connected_actual");
        var pairedB = new FieldReference(DeviceIdentity(RadioB, Remote), "bluetooth_all_device_paired_actual");
        Assert.True(access.Backend.ReadCached(connectedA).Value?.Boolean == true);
        Assert.True(access.Backend.ReadCached(pairedA).Value?.Boolean == false);
        Assert.True(access.Backend.ReadCached(connectedB).Value?.Boolean == false);
        Assert.True(access.Backend.ReadCached(pairedB).Value?.Boolean == true);
        Assert.Equal(RadioA, access.Backend.ReadCached(new(ControllerIdentity(RadioA),
            "bluetooth_all_controller_id_declared")).Value?.Text);

        var previous = owner.ReadState();
        source.Set((_, selected, _) => Result(scope, selected, [RadioA, RadioB],
            [new(RadioA), new(RadioB)],
            [new(RadioA, Remote, false, false), new(RadioB, Remote, true, false)]));
        var selected = Assert.Single(await access.Backend.CollectAsync([connectedA], default));
        Assert.True(selected.Value?.Boolean == false);
        Assert.Equal(WindowsBluetoothClassicInformationFields.DeviceConnected, source.Requests.Last());
        Assert.True(access.Backend.ReadCached(pairedA).Value?.Boolean == false);
        // The explicit scope reads connected state for both radios; paired state is unselected.
        Assert.True(access.Backend.ReadCached(connectedB).Value?.Boolean == true);
        Assert.True(access.Backend.ReadCached(pairedB).Value?.Boolean == true);
        Assert.NotSame(previous, owner.ReadState());
        Assert.Equal(selected, InformationReadWriteBackend.Read(
            [new(connectedA, DeviceInformationFieldRegistry.BluetoothAllDeviceConnectedActual)],
            owner.ReadState().InformationFields).Single());
    }

    [Fact]
    public async Task FailedDiscoveryIsObservableAndSuccessfulEmptySelectionRetiresOnlySelectedFields()
    {
        var scope = Scope();
        var source = new RecordedReader((_, selected, _) => Failed(scope, selected, "recorded radio failure"));
        using var owner = EmptyOwner();
        var access = owner.CreateBluetoothClassicInformation(scope, source);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await access.Backend.DiscoverAsync(default));
        Assert.Contains("windows-bluetooth-controller|", error.Message);
        Assert.Contains("recorded radio failure", error.Message);
        Assert.Empty(access.Backend.Catalog.Fields);

        source.Set((_, selected, _) => Result(scope, selected, [RadioA], [new(RadioA)],
            [new(RadioA, Remote, true, true)]));
        var catalog = await access.Backend.DiscoverAsync(default);
        var connected = new FieldReference(DeviceIdentity(RadioA, Remote), "bluetooth_all_device_connected_actual");
        var paired = new FieldReference(DeviceIdentity(RadioA, Remote), "bluetooth_all_device_paired_actual");
        Assert.True(access.Backend.ReadCached(connected).Value?.Boolean == true);
        Assert.True(access.Backend.ReadCached(paired).Value?.Boolean == true);

        source.Set((_, selected, _) => new([RadioA], Devices:
            new WindowsBluetoothClassicDirectoryRead<DeviceTopologyBluetoothClassicDeviceInformation>(true, [])));
        var retired = Assert.Single(await access.Backend.CollectAsync([connected], default));
        Assert.Equal(FieldObservationStatus.Unavailable, retired.Status);
        Assert.Null(retired.Value);
        Assert.True(access.Backend.ReadCached(paired).Value?.Boolean == true);
        Assert.Equal(RadioA, access.Backend.ReadCached(new(ControllerIdentity(RadioA),
            "bluetooth_all_controller_id_declared")).Value?.Text);

        source.Set((_, selected, _) => new([RadioA], Devices:
            new WindowsBluetoothClassicDirectoryRead<DeviceTopologyBluetoothClassicDeviceInformation>(false, [], "device directory unavailable")));
        var failed = Assert.Single(await access.Backend.CollectAsync([paired], default));
        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status);
        Assert.Null(failed.Value);
        Assert.Contains(owner.ReadState().InformationFields.Devices,
            device => device.DeviceIdentity == DeviceIdentity(RadioA, Remote)
                && device.Fields.Any(field => field.Definition.Name == "bluetooth_all_device_paired_actual"
                    && field.Value is null));
    }

    [Fact]
    public async Task RevokedPermitAndCancellationDuringProjectionKeepTheOriginalCurrent()
    {
        var scope = Scope(WindowsBluetoothClassicInformationFields.DeviceConnected);
        var source = new RecordedReader((_, selected, _) => Result(scope, selected, [RadioA], [],
            [new(RadioA, Remote, true, false)]));
        using var owner = EmptyOwner();
        var access = owner.CreateBluetoothClassicInformation(scope, source);
        var catalog = await access.Backend.DiscoverAsync(default);
        var field = Assert.Single(catalog.Fields).Field;
        using var borrow = access.BorrowSubscriptionCapture();
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId,
            Assert.Single(Assert.Single(borrow.Bind([field])).CollectionItemIds));

        var original = owner.ReadState();
        var rejected = await borrow.CollectAsync([field], new Permit(active: false));
        Assert.False(rejected.Committed);
        Assert.Same(original, owner.ReadState());

        using var cancellation = new CancellationTokenSource();
        source.Set((_, selected, _) => Result(scope, selected, [RadioA], [],
            new CancelOnSecondEnumerateList<DeviceTopologyBluetoothClassicDeviceInformation>(
                [new(RadioA, Remote, false, false)], cancellation)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await access.Backend.CollectAsync([field], cancellation.Token));
        Assert.Same(original, owner.ReadState());
    }

    private static WindowsBluetoothClassicInformationScope Scope(
        WindowsBluetoothClassicInformationFields fields = WindowsBluetoothClassicInformationFields.All)
        => new(fields, maximumControllers: 8, maximumDevices: 64);

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(
            static () => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private static string ControllerIdentity(string address) => "windows-bluetooth-controller|" + address;
    private static string DeviceIdentity(string controller, string device) => "windows-bluetooth-device|" + controller + "|" + device;

    private static WindowsBluetoothClassicInformationReadResult Result(
        WindowsBluetoothClassicInformationScope scope,
        WindowsBluetoothClassicInformationFields selected,
        IReadOnlyList<string> addresses,
        IReadOnlyList<DeviceTopologyBluetoothClassicControllerInformation> controllers,
        IReadOnlyList<DeviceTopologyBluetoothClassicDeviceInformation> devices)
        => new(addresses,
            (selected & WindowsBluetoothClassicInformationFields.ControllerId) != 0
                ? new(true, controllers) : null,
            (selected & WindowsBluetoothClassicInformationFields.DeviceState) != 0
                ? new(true, devices) : null);

    private static WindowsBluetoothClassicInformationReadResult Failed(
        WindowsBluetoothClassicInformationScope scope,
        WindowsBluetoothClassicInformationFields selected,
        string reason)
        => new([], (selected & WindowsBluetoothClassicInformationFields.ControllerId) != 0
                ? new(false, [], reason) : null,
            (selected & WindowsBluetoothClassicInformationFields.DeviceState) != 0
                ? new(false, [], reason) : null);

    private sealed class RecordedReader(
        Func<WindowsBluetoothClassicInformationScope, WindowsBluetoothClassicInformationFields, CancellationToken,
            WindowsBluetoothClassicInformationReadResult> read) : IWindowsBluetoothClassicInformationReader
    {
        private Func<WindowsBluetoothClassicInformationScope, WindowsBluetoothClassicInformationFields, CancellationToken,
            WindowsBluetoothClassicInformationReadResult> current = read;
        public ConcurrentQueue<WindowsBluetoothClassicInformationFields> Requests { get; } = new();

        public void Set(Func<WindowsBluetoothClassicInformationScope, WindowsBluetoothClassicInformationFields, CancellationToken,
            WindowsBluetoothClassicInformationReadResult> next) => Volatile.Write(ref current, next);

        public WindowsBluetoothClassicInformationReadResult Read(WindowsBluetoothClassicInformationScope scope,
            WindowsBluetoothClassicInformationFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(selectedFields);
            var result = Volatile.Read(ref current)(scope, selectedFields, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    private sealed class Permit(bool active = true) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public bool TryPublish(Action commit) { if (!active) return false; commit(); return true; }
    }

    private sealed class CancelOnSecondEnumerateList<T>(IReadOnlyList<T> values, CancellationTokenSource cancellation)
        : IReadOnlyList<T>
    {
        private int enumerations;
        public int Count => values.Count;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator()
        { if (Interlocked.Increment(ref enumerations) == 2) cancellation.Cancel(); return values.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
