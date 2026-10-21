using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Printers;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsPrinterInformationOwnerTests
{
    private const string PrinterName = "Office Queue";
    private const string ServerName = "srv";
    private const string PrinterIdentity = "windows-printer|local-and-connections|3:srv|12:Office Queue";

    [Fact]
    public async Task ExactFieldsAndSelectedDirectAndBorrowReadsUseTheOriginalCurrent()
    {
        var flags = WindowsPrinterQueueStatusFlags.Printing
            | WindowsPrinterQueueStatusFlags.Offline
            | (WindowsPrinterQueueStatusFlags)0x80000000;
        var initial = new DeviceTopologyPrinterInformation(PrinterName, ServerName,
            "USB001:,FILE:", flags, ulong.MaxValue);
        var reader = new RecordedReader(Successful(initial));
        using var owner = EmptyOwner();
        var access = owner.CreatePrinterInformation(Scope(), reader);

        Assert.Empty(access.Backend.Catalog.Fields);
        Assert.Empty(reader.Requests);
        var catalog = await access.Backend.DiscoverAsync(default);

        Assert.Equal(WindowsPrinterInformationFields.All, reader.Requests.Single());
        Assert.Equal(new[]
        {
            "printer_all_driver_version_declared",
            "printer_all_port_declared",
            "printer_all_queue_state_actual"
        }, catalog.Fields.Select(static field => field.Field.FieldName).Order(StringComparer.Ordinal));
        Assert.All(catalog.Fields, field => Assert.Equal(PrinterIdentity, field.Field.DeviceIdentity));
        var device = Assert.Single(catalog.Devices);
        Assert.Equal(PrinterIdentity, device.Identity);
        Assert.Equal("printer", device.Resource);
        Assert.Equal(new[]
        {
            "windows.print-spooler.driver-info-6",
            "windows.print-spooler.get-printer",
            "windows.print-spooler.get-printer"
        }, catalog.Fields.Select(static field => field.ReadSource).Order(StringComparer.Ordinal));
        Assert.Equal("text", catalog.Fields.Single(field => field.Field.FieldName == "printer_all_driver_version_declared").Definition.Unit);
        Assert.Equal("text", catalog.Fields.Single(field => field.Field.FieldName == "printer_all_port_declared").Definition.Unit);
        Assert.Equal("enum", catalog.Fields.Single(field => field.Field.FieldName == "printer_all_queue_state_actual").Definition.Unit);
        Assert.Equal("string", catalog.Fields.Single(field => field.Field.FieldName == "printer_all_driver_version_declared").Definition.ValueKind);
        Assert.Equal("string", catalog.Fields.Single(field => field.Field.FieldName == "printer_all_port_declared").Definition.ValueKind);
        Assert.Equal("unsigned128", catalog.Fields.Single(field => field.Field.FieldName == "printer_all_queue_state_actual").Definition.ValueKind);

        var version = new FieldReference(PrinterIdentity, "printer_all_driver_version_declared");
        var port = new FieldReference(PrinterIdentity, "printer_all_port_declared");
        var state = new FieldReference(PrinterIdentity, "printer_all_queue_state_actual");
        var ownerBackend = owner.CreateBackend();
        Assert.Equal("18446744073709551615", access.Backend.ReadCached(version).Value!.Text);
        Assert.Equal("USB001:,FILE:", access.Backend.ReadCached(port).Value!.Text);
        Assert.Equal(((uint)flags).ToString(CultureInfo.InvariantCulture), access.Backend.ReadCached(state).Value!.Integer);
        Assert.Equal(access.Backend.ReadCached(port), ownerBackend.ReadCached(port));
        Assert.Equal(1, reader.ReadCount);

        reader.Set(Successful(initial with
        {
            PortNameDeclared = "USB002:",
            QueueStateActual = WindowsPrinterQueueStatusFlags.None,
            DriverVersionRaw = 44
        }));
        var selected = Assert.Single(await access.Backend.CollectAsync([state], default));
        Assert.Equal(FieldObservationStatus.Current, selected.Status);
        Assert.Equal("0", selected.Value!.Integer);
        Assert.NotNull(selected.ObservedAt);
        Assert.Equal("USB001:,FILE:", ownerBackend.ReadCached(port).Value!.Text);
        Assert.Equal("USB001:,FILE:", access.Backend.ReadCached(port).Value!.Text);
        Assert.Equal(WindowsPrinterInformationFields.QueueStateActual, reader.Requests.Last());

        using var borrow = access.BorrowSubscriptionCapture();
        var binding = Assert.Single(borrow.Bind([port]));
        Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId,
            Assert.Single(binding.CollectionItemIds));
        var beforeBorrow = owner.ReadState();
        var result = await borrow.CollectAsync([port], new Permit());
        Assert.True(result.Committed);
        Assert.NotSame(beforeBorrow, owner.ReadState());
        Assert.Equal("USB002:", Assert.Single(result.Readings).Value!.Text);
        Assert.Equal("USB002:", ownerBackend.ReadCached(port).Value!.Text);
        Assert.Equal(ownerBackend.ReadCached(port), access.Backend.ReadCached(port));
        Assert.Equal(WindowsPrinterInformationFields.PortDeclared, reader.Requests.Last());
    }

    [Fact]
    public async Task FailedDirectoryAndKnownPrinterReadClearSelectedValuesWhileEmptySuccessRetiresIdentity()
    {
        var row = new DeviceTopologyPrinterInformation(PrinterName, ServerName, "USB001:",
            WindowsPrinterQueueStatusFlags.Printing, 77);
        var reader = new RecordedReader(Successful(row));
        using var owner = EmptyOwner();
        var access = owner.CreatePrinterInformation(Scope(), reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var version = catalog.Fields.Single(field => field.Field.FieldName == "printer_all_driver_version_declared").Field;
        var port = catalog.Fields.Single(field => field.Field.FieldName == "printer_all_port_declared").Field;
        var state = catalog.Fields.Single(field => field.Field.FieldName == "printer_all_queue_state_actual").Field;

        reader.Set(new(false, [],
            [new(null, null, WindowsPrinterInformationFields.QueueStateActual, "directory-unavailable")]));
        var beforeDirectoryFailure = owner.ReadState();
        var failedSelected = Assert.Single(await access.Backend.CollectAsync([state], default));
        Assert.NotSame(beforeDirectoryFailure, owner.ReadState());
        Assert.Equal(FieldObservationStatus.Unavailable, failedSelected.Status);
        Assert.Null(failedSelected.Value);
        Assert.Null(failedSelected.ObservedAt);
        Assert.Contains(owner.ReadState().InformationFields.Devices,
            device => device.DeviceIdentity == PrinterIdentity);
        Assert.Equal("USB001:", access.Backend.ReadCached(port).Value!.Text);
        Assert.Equal("77", access.Backend.ReadCached(version).Value!.Text);

        var allFailedRow = new DeviceTopologyPrinterInformation(PrinterName, ServerName,
            null, null, null);
        reader.Set(new(true, [allFailedRow],
            [new(PrinterName, ServerName, WindowsPrinterInformationFields.All, "printer-read-failed")]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await access.Backend.DiscoverAsync(default));
        Assert.Contains(owner.ReadState().InformationFields.Devices,
            device => device.DeviceIdentity == PrinterIdentity);
        Assert.All(new[] { version, port, state }, field =>
        {
            var reading = access.Backend.ReadCached(field);
            Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
            Assert.Null(reading.Value);
            Assert.Null(reading.ObservedAt);
        });

        reader.Set(Successful());
        var empty = await access.Backend.DiscoverAsync(default);
        Assert.Empty(empty.Fields);
        Assert.DoesNotContain(owner.ReadState().InformationFields.Devices,
            device => device.DeviceIdentity == PrinterIdentity);
    }

    [Fact]
    public async Task CancellationDuringProjectionAndBorrowRetirementDoNotCommit()
    {
        var row = new DeviceTopologyPrinterInformation(PrinterName, ServerName, "USB001:",
            WindowsPrinterQueueStatusFlags.None, 3);
        CancellationTokenSource? cancelDuringProjection = null;
        var reader = new RecordedReader((scope, selected, token) =>
        {
            var result = Successful(row);
            return cancelDuringProjection is { } cancellation
                ? result with { Printers = new CancelOnEnumerateList(result.Printers, cancellation) }
                : result;
        });
        using var owner = EmptyOwner();
        var access = owner.CreatePrinterInformation(Scope(), reader);
        var catalog = await access.Backend.DiscoverAsync(default);
        var state = catalog.Fields.Single(field => field.Field.FieldName == "printer_all_queue_state_actual").Field;

        using var borrow = access.BorrowSubscriptionCapture();
        _ = borrow.Bind([state]);
        var original = owner.ReadState();
        var rejected = await borrow.CollectAsync([state], new Permit(active: false));
        Assert.False(rejected.Committed);
        Assert.Same(original, owner.ReadState());

        using var cancellation = new CancellationTokenSource();
        cancelDuringProjection = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await access.Backend.CollectAsync([state], cancellation.Token));
        Assert.Same(original, owner.ReadState());

        borrow.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await borrow.CollectAsync([state], new Permit()));
        owner.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await access.Backend.CollectAsync([state], default));
    }

    private static WindowsPrinterInformationScope Scope() => new(
        WindowsPrinterInformationFields.All, WindowsPrinterEnumerationScope.LocalAndConnections,
        maximumPrinters: 16, maximumDriverEntriesPerServer: 64,
        maximumNativeBufferBytes: 256 * 1024, maximumStringCharacters: 256);

    private static DeviceTopologyCaptureOwner EmptyOwner()
        => DeviceTopologyCaptureOwner.CreateDriverInformation(
            static () => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null));

    private static WindowsPrinterInformationReadResult Successful(params DeviceTopologyPrinterInformation[] printers)
        => new(true, printers, []);

    private sealed class RecordedReader : IWindowsPrinterInformationReader
    {
        private readonly Func<WindowsPrinterInformationScope, WindowsPrinterInformationFields, CancellationToken,
            WindowsPrinterInformationReadResult>? transform;
        private readonly ConcurrentQueue<WindowsPrinterInformationFields> requests = new();
        private WindowsPrinterInformationReadResult current;

        public RecordedReader(WindowsPrinterInformationReadResult initial) => current = initial;

        public RecordedReader(Func<WindowsPrinterInformationScope, WindowsPrinterInformationFields, CancellationToken,
            WindowsPrinterInformationReadResult> transform)
        {
            this.transform = transform;
            current = Successful();
        }

        public IReadOnlyCollection<WindowsPrinterInformationFields> Requests => requests.ToArray();
        public int ReadCount => requests.Count;

        public void Set(WindowsPrinterInformationReadResult result) => Volatile.Write(ref current, result);

        public WindowsPrinterInformationReadResult Read(WindowsPrinterInformationScope scope,
            WindowsPrinterInformationFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            return transform?.Invoke(scope, selectedFields, cancellationToken) ?? Volatile.Read(ref current);
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

    private sealed class CancelOnEnumerateList(IReadOnlyList<DeviceTopologyPrinterInformation> items,
        CancellationTokenSource cancellation) : IReadOnlyList<DeviceTopologyPrinterInformation>
    {
        public int Count => items.Count;
        public DeviceTopologyPrinterInformation this[int index] => items[index];
        public IEnumerator<DeviceTopologyPrinterInformation> GetEnumerator()
        {
            cancellation.Cancel();
            return items.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
