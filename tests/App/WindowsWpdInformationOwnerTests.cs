using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.PortableDevices;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsWpdInformationOwnerTests
{
    private const string ConnectionA = @"WPD\USB\VID_1234&PID_0001#connection-A";
    private const string ConnectionB = @"WPD\USB\VID_1234&PID_0001#connection-B";
    private const string StorageA = "Internal Storage";
    private const string StorageB = "Memory Card";

    [Fact]
    public async Task RecordedDiscoveryBindsExactDeviceAndStorageObjectsAndSelectedReadKeepsOtherCurrent()
    {
        var scope = Scope();
        var reader = new RecordedReader((_, request, _) => request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices
            ? FullRead()
            : SelectedRead(request, batteryA: 91));
        using var owner = EmptyOwner();
        var access = owner.CreatePortableDeviceInformation(scope, reader);

        Assert.Empty(access.Backend.Catalog.Fields);
        Assert.Empty(reader.Requests);
        var catalog = await access.Backend.DiscoverAsync(default);
        Assert.Equal(WindowsWpdInformationReadMode.AllConnectedDevices, reader.Requests.Single().Mode);
        Assert.Equal(WindowsWpdInformationFields.All, reader.Requests.Single().Fields);
        Assert.Equal(8, catalog.Fields.Length);
        Assert.Equal(new[]
        {
            "mobile_all_battery_percent_reported",
            "mobile_all_device_id_declared",
            "mobile_all_firmware_version_declared",
            "mobile_all_storage_capacity_reported"
        }, catalog.Fields.Select(static item => item.Field.FieldName).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(2, catalog.Devices.Count(static device => device.Resource == "mobile_device"));
        Assert.Equal(2, catalog.Devices.Count(static device => device.Resource == "mobile_storage"));
        Assert.Equal(WindowsWpdInformationIdentityForDevice(ConnectionA), catalog.Devices
            .Single(device => device.Resource == "mobile_device" && device.Identity == WindowsWpdInformationIdentityForDevice(ConnectionA)).Identity);
        Assert.Equal(WindowsWpdInformationIdentityForStorage(ConnectionA, StorageA), catalog.Devices
            .Single(device => device.Resource == "mobile_storage" && device.Identity == WindowsWpdInformationIdentityForStorage(ConnectionA, StorageA)).Identity);

        Assert.Equal("text", DeviceInformationFieldRegistry.MobileAllDeviceIdDeclared.ValueKind);
        var batteryA = Field(catalog, ConnectionA, "mobile_all_battery_percent_reported");
        var deviceIdA = Field(catalog, ConnectionA, "mobile_all_device_id_declared");
        var firmwareA = Field(catalog, ConnectionA, "mobile_all_firmware_version_declared");
        var capacityA = Field(catalog, ConnectionA, "mobile_all_storage_capacity_reported", StorageA);
        var capacityB = Field(catalog, ConnectionB, "mobile_all_storage_capacity_reported", StorageB);
        Assert.Equal("float64", catalog.Fields.Single(item => item.Field == batteryA).Definition.ValueKind);
        Assert.Equal("%", catalog.Fields.Single(item => item.Field == batteryA).Definition.Unit);
        Assert.Equal("string", catalog.Fields.Single(item => item.Field == deviceIdA).Definition.ValueKind);
        Assert.Equal("text", catalog.Fields.Single(item => item.Field == deviceIdA).Definition.Unit);
        Assert.Equal("string", catalog.Fields.Single(item => item.Field == firmwareA).Definition.ValueKind);
        Assert.Equal("text", catalog.Fields.Single(item => item.Field == firmwareA).Definition.Unit);
        Assert.Equal("unsigned64", catalog.Fields.Single(item => item.Field == capacityA).Definition.ValueKind);
        Assert.Equal("B", catalog.Fields.Single(item => item.Field == capacityA).Definition.Unit);
        Assert.Equal(87d, access.Backend.ReadCached(batteryA).Value?.Number);
        Assert.Equal("same declared WPD root ID", access.Backend.ReadCached(deviceIdA).Value?.Text);
        Assert.Equal("firmware-A", access.Backend.ReadCached(firmwareA).Value?.Text);
        Assert.Equal("18446744073709551615", access.Backend.ReadCached(capacityA).Value?.Integer);
        Assert.Equal("9223372036854775809", access.Backend.ReadCached(capacityB).Value?.Integer);

        var original = owner.ReadState();
        reader.Set((_, request, _) => SelectedRead(request, batteryA: 91));
        var collected = Assert.Single(await access.Backend.CollectAsync([batteryA], default));
        Assert.Equal(FieldObservationStatus.Current, collected.Status);
        Assert.Equal(91d, collected.Value?.Number);
        var selectedRequest = reader.Requests.Last();
        Assert.Equal(WindowsWpdInformationReadMode.SelectedObjects, selectedRequest.Mode);
        Assert.Equal(WindowsWpdInformationFields.BatteryPercent, selectedRequest.Fields);
        var exactTarget = Assert.Single(selectedRequest.Targets);
        Assert.Equal(ConnectionA, exactTarget.DeviceConnectionId);
        Assert.Equal("DEVICE", exactTarget.ObjectId);
        Assert.Equal(WindowsWpdInformationObjectKind.Device, exactTarget.ObjectKind);
        Assert.Equal(WindowsWpdInformationFields.BatteryPercent, exactTarget.Fields);
        Assert.NotSame(original, owner.ReadState());
        Assert.Equal("same declared WPD root ID", access.Backend.ReadCached(deviceIdA).Value?.Text);
        Assert.Equal("firmware-A", access.Backend.ReadCached(firmwareA).Value?.Text);
        Assert.Equal("18446744073709551615", access.Backend.ReadCached(capacityA).Value?.Integer);
        Assert.Equal(62d, access.Backend.ReadCached(Field(catalog, ConnectionB,
            "mobile_all_battery_percent_reported")).Value?.Number);
        Assert.Equal(collected, access.Backend.ReadCached(batteryA));
    }

    [Fact]
    public async Task DirectoryFailureIsObservableAndSuccessfulEmptyReadRetiresOnlySelectedWpdFields()
    {
        var scope = Scope();
        var reader = new RecordedReader((_, _, _) => FullRead());
        using var owner = EmptyOwner();
        var access = owner.CreatePortableDeviceInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var batteryA = Field(catalog, ConnectionA, "mobile_all_battery_percent_reported");
        var deviceIdA = Field(catalog, ConnectionA, "mobile_all_device_id_declared");
        var capacityA = Field(catalog, ConnectionA, "mobile_all_storage_capacity_reported", StorageA);

        reader.Set((_, _, _) => new(false, [], [], "manager-refresh-denied"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await access.Backend.DiscoverAsync(default));
        Assert.Contains("directorySucceeded=False", error.Message);
        Assert.Contains("manager-refresh-denied", error.Message);
        foreach (var field in new[] { batteryA, deviceIdA, capacityA })
        {
            var reading = access.Backend.ReadCached(field);
            Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
            Assert.Null(reading.Value);
            Assert.Null(reading.ObservedAt);
        }

        reader.Set((_, _, _) => new(true, [], []));
        var empty = await access.Backend.DiscoverAsync(default);
        Assert.Empty(empty.Fields);
        Assert.DoesNotContain(owner.ReadState().InformationFields.Devices,
            static device => device.DeviceIdentity.StartsWith("windows-wpd-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RevokedPermitAndCancellationDuringProjectionKeepOriginalCurrent()
    {
        var scope = Scope();
        CancellationTokenSource? cancelDuringProjection = null;
        var reader = new RecordedReader((_, request, _) =>
        {
            if (request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices)
                return FullRead();
            var result = SelectedRead(request, batteryA: 45);
            return cancelDuringProjection is { } cancellation
                ? result with { Devices = new CancelOnEnumerateList<DeviceTopologyWpdDeviceInformation>(result.Devices, cancellation) }
                : result;
        });
        using var owner = EmptyOwner();
        var access = owner.CreatePortableDeviceInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var batteryA = Field(catalog, ConnectionA, "mobile_all_battery_percent_reported");
        using var borrow = access.BorrowSubscriptionCapture();
        _ = borrow.Bind([batteryA]);
        var original = owner.ReadState();

        var rejected = await borrow.CollectAsync([batteryA], new Permit(active: false));
        Assert.False(rejected.Committed);
        Assert.Same(original, owner.ReadState());

        using var cancellation = new CancellationTokenSource();
        cancelDuringProjection = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await access.Backend.CollectAsync([batteryA], cancellation.Token));
        Assert.Same(original, owner.ReadState());
    }

    [Theory]
    [InlineData(4, 16)]
    [InlineData(8, 24)]
    public void PropVariantLayoutMatchesWindowsSdkPointerWidth(int pointerSize, int expectedSize)
    {
        Assert.Equal(expectedSize, WindowsWpdInformationNativeReader.PropVariantSizeForPointerSize(pointerSize));
        Assert.Equal(8, WindowsWpdInformationNativeReader.PropVariantStringPointerOffset);
        Assert.Equal((ushort)31, WindowsWpdInformationNativeReader.VariantTypeLpwstr);
    }

    [Fact]
    public void RecordedPropVariantRequiresLpwstrAndInvokesCleanupWithoutNativeCalls()
    {
        var cleanupCalls = 0;
        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsWpdInformationNativeReader.ReadStorageObjectId(
                variant =>
                {
                    Marshal.WriteInt16(variant, 0, 19); // VT_UI4 is not a WPD object ID string.
                    return 0;
                },
                maximumCharacters: 32,
                clearPropVariant: variant =>
                {
                    cleanupCalls++;
                    Assert.Equal((ushort)19, unchecked((ushort)Marshal.ReadInt16(variant)));
                    return 0; // Injected cleanup seam; no PropVariantClear P/Invoke.
                }));

        Assert.Contains("unexpected PROPVARIANT type", error.Message);
        Assert.Equal(1, cleanupCalls);
    }

    [Fact]
    public void RecordedLpwstrReturnsExactStorageObjectIdAndClearsItsVariant()
    {
        var cleanupCalls = 0;
        var objectId = WindowsWpdInformationNativeReader.ReadStorageObjectId(
            variant =>
            {
                Marshal.WriteInt16(variant, 0, 31);
                Marshal.WriteIntPtr(variant, 8, Marshal.StringToCoTaskMemUni("STORE-1"));
                return 0;
            },
            maximumCharacters: 32,
            clearPropVariant: variant =>
            {
                cleanupCalls++;
                Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(variant, 8));
                return 0;
            });

        Assert.Equal("STORE-1", objectId);
        Assert.Equal(1, cleanupCalls);
    }

    [Fact]
    public void RecordedPropVariantBoundsLpwstrAndClearsReturnedString()
    {
        var cleanupCalls = 0;
        IntPtr valuePointer = IntPtr.Zero;
        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsWpdInformationNativeReader.ReadStorageObjectId(
                variant =>
                {
                    valuePointer = Marshal.StringToCoTaskMemUni("TOO-LONG");
                    Marshal.WriteInt16(variant, 0, 31);
                    Marshal.WriteIntPtr(variant, 8, valuePointer);
                    return 0;
                },
                maximumCharacters: 3,
                clearPropVariant: variant =>
                {
                    cleanupCalls++;
                    Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(variant, 8));
                    valuePointer = IntPtr.Zero;
                    return 0;
                }));

        Assert.Contains("exceeds the caller's explicit character bound", error.Message);
        Assert.Equal(1, cleanupCalls);
        Assert.Equal(IntPtr.Zero, valuePointer);
    }

    [Fact]
    public void FailedRecordedGetAtStillClearsTheInitializedPropVariant()
    {
        var cleanupCalls = 0;
        var error = Assert.Throws<COMException>(() =>
            WindowsWpdInformationNativeReader.ReadStorageObjectId(
                variant =>
                {
                    Marshal.WriteInt16(variant, 0, 31);
                    Marshal.WriteIntPtr(variant, 8, Marshal.StringToCoTaskMemUni("STORE-1"));
                    return unchecked((int)0x80004005);
                },
                maximumCharacters: 32,
                clearPropVariant: variant =>
                {
                    cleanupCalls++;
                    Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(variant, 8));
                    return 0;
                }));

        Assert.Equal(unchecked((int)0x80004005), error.HResult);
        Assert.Equal(1, cleanupCalls);
    }

    [Fact]
    public async Task OversizedRecordedFailureListIsRejectedBeforeEnumerationOrCommit()
    {
        var scope = new WindowsWpdInformationScope(WindowsWpdInformationFields.DeviceId,
            maximumDevices: 1, maximumStorageObjectsPerDevice: 1,
            maximumStorageObjects: 1, maximumStringCharacters: 32);
        Assert.Equal(5, scope.MaximumFailureRecords);
        var failures = new NonEnumeratingFailureList(scope.MaximumFailureRecords + 1);
        var reader = new RecordedReader((_, _, _) => new(true,
            [new(ConnectionA, "DEVICE", "declared-A", null, null, null, [])], failures));
        using var owner = EmptyOwner();
        var access = owner.CreatePortableDeviceInformation(scope, reader);
        var original = owner.ReadState();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await access.Backend.DiscoverAsync(default));

        Assert.Contains("explicit failure-record bound", error.Message);
        Assert.False(failures.WasEnumerated);
        Assert.Same(original, owner.ReadState());
    }

    private static WindowsWpdInformationScope Scope()
        => new(WindowsWpdInformationFields.All, maximumDevices: 8,
            maximumStorageObjectsPerDevice: 8, maximumStorageObjects: 16, maximumStringCharacters: 512);

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(
            static () => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private static FieldReference Field(ReadWriteCatalog catalog, string connectionId, string name,
        string? storageObjectId = null)
    {
        var identity = storageObjectId is null
            ? WindowsWpdInformationIdentityForDevice(connectionId)
            : WindowsWpdInformationIdentityForStorage(connectionId, storageObjectId);
        return catalog.Fields.Single(item => item.Field.DeviceIdentity == identity
            && item.Field.FieldName == name).Field;
    }

    private static string WindowsWpdInformationIdentityForDevice(string connectionId)
        => "windows-wpd-device|" + Convert.ToBase64String(Encoding.Unicode.GetBytes(connectionId))
            + "|" + Convert.ToBase64String(Encoding.Unicode.GetBytes("DEVICE"));

    private static string WindowsWpdInformationIdentityForStorage(string connectionId, string objectId)
        => "windows-wpd-storage|" + Convert.ToBase64String(Encoding.Unicode.GetBytes(connectionId))
            + "|" + Convert.ToBase64String(Encoding.Unicode.GetBytes(objectId));

    private static WindowsWpdInformationReadResult FullRead()
        => new(true,
        [
            new(ConnectionA, "DEVICE", "same declared WPD root ID", "firmware-A", 87, true,
                [new(ConnectionA, StorageA, ulong.MaxValue)]),
            new(ConnectionB, "DEVICE", "same declared WPD root ID", "firmware-B", 62, true,
                [new(ConnectionB, StorageB, 9_223_372_036_854_775_809UL)])
        ], []);

    private static WindowsWpdInformationReadResult SelectedRead(
        WindowsWpdInformationCaptureRequest request, uint batteryA)
    {
        var target = Assert.Single(request.Targets);
        Assert.Equal(ConnectionA, target.DeviceConnectionId);
        Assert.Equal("DEVICE", target.ObjectId);
        Assert.Equal(WindowsWpdInformationFields.BatteryPercent, target.Fields);
        return new(true,
            [new(ConnectionA, "DEVICE", null, null, batteryA, null, [])], []);
    }

    private sealed class RecordedReader(
        Func<WindowsWpdInformationScope, WindowsWpdInformationCaptureRequest, CancellationToken,
            WindowsWpdInformationReadResult> read) : IWindowsWpdInformationReader
    {
        private Func<WindowsWpdInformationScope, WindowsWpdInformationCaptureRequest, CancellationToken,
            WindowsWpdInformationReadResult> current = read;
        public ConcurrentQueue<WindowsWpdInformationCaptureRequest> Requests { get; } = new();
        public void Set(Func<WindowsWpdInformationScope, WindowsWpdInformationCaptureRequest, CancellationToken,
            WindowsWpdInformationReadResult> next) => Volatile.Write(ref current, next);

        public WindowsWpdInformationReadResult Read(WindowsWpdInformationScope scope,
            WindowsWpdInformationCaptureRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            return Volatile.Read(ref current)(scope, request, cancellationToken);
        }
    }

    private sealed class Permit(bool active = true) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public bool TryPublish(Action commit)
        {
            if (!active) return false;
            commit();
            return true;
        }
    }

    private sealed class CancelOnEnumerateList<T>(IReadOnlyList<T> values,
        CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => values.Count;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator()
        {
            cancellation.Cancel();
            return values.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class NonEnumeratingFailureList(int count)
        : IReadOnlyList<WindowsWpdInformationReadFailure>
    {
        public bool WasEnumerated { get; private set; }
        public int Count => count;
        public WindowsWpdInformationReadFailure this[int index]
            => throw new InvalidOperationException("The bounded failure list must not be indexed.");
        public IEnumerator<WindowsWpdInformationReadFailure> GetEnumerator()
        {
            WasEnumerated = true;
            throw new InvalidOperationException("The bounded failure list must not be enumerated.");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
