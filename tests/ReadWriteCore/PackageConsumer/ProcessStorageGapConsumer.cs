using System.Globalization;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Platform;

internal static class ProcessStorageGapConsumer
{
    // Recorded physical/OS matcher and public canonical API; opened-query identity is proved by the friend tests separately.
    internal static async Task RunAsync()
    {
        var physical = Row(("DeviceId", 7), ("SerialNumber", "serial"), ("UniqueId", "unique"), ("UniqueIdFormat", 1), ("BusType", 7), ("Size", 100UL));
        var os = new Dictionary<string, object?>(physical) { ["Number"] = 23 };
        var failed = false; var reads = 0;
        var owner = new WindowsStorageOwner(TimeSpan.FromSeconds(15), TimeProvider.System, _ => BackendMessage.Create(0, 0),
            query: (_, query) => new QueryResult([query.Contains("MSFT_PhysicalDisk") ? physical : os], null),
            readTrim: (number, serial, unique, format, bus) =>
            {
                Require(number == 23 && serial == "serial" && unique == "unique" && format == 1 && bus == 7, "exact selected identity reaches property reader");
                reads++; return failed ? null : false;
            });
        var identity = owner.ReadIdentities().Identities.Single().ExactIdentityKey;
        var request = new StorageSensorReadRequest(new Dictionary<int, string> { [7] = identity });
        var current = owner.ReadTrimFields(request);
        var backend = new InformationReadWriteBackend(() => current,
            token => { token.ThrowIfCancellationRequested(); current = owner.ReadTrimFields(request); return ValueTask.FromResult(current); }, _ => new(true, null, []));
        var field = new FieldReference(identity, "storage_all_trim_capability");
        Require(backend.ReadCached(field).Status == FieldObservationStatus.Current && backend.ReadCached(field).Value?.Boolean == false, "reported false is available");
        Require(!backend.Catalog.Require(field).Write.Supported && backend.Catalog.Require(field).Read.Dependencies.IsEmpty, "read-only canonical support");
        failed = true; var unavailable = await backend.CollectAsync([field], default);
        Require(unavailable.Single().Field == field && unavailable.Single().Value is null && reads == 2, "failed selected query clears canonical value");
        Require(ProcessFieldCount() == 34, "appended process directory preserves original20 codes");
        Console.WriteLine("Process/storage gap recorded canonical consumer passed: typed physical matcher/TRIM false and failure; thread-count directory21. Recorded route does not prove opened native query.");
    }

    // Explicit real read-only path; primary decides whether to invoke it. No TRIM/ETW/write command.
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var process = WindowsProcessFieldOwner.CreateDefault();
        await process.CollectAsync(["process_all_thread_count_actual"], timeout.Token);
        var own = process.Backend.Catalog.Fields.Single(item => item.Field.FieldName == "process_all_thread_count_actual"
            && item.Field.DeviceIdentity.StartsWith($"windows-process|{Environment.ProcessId}:", StringComparison.Ordinal)).Field;
        var captured = await process.Backend.CollectAsync([own], timeout.Token);
        var threads = captured.Single();
        Require(threads.Field == own && threads.Value?.Integer is { } text && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0,
            "actual current process identity-verified thread count");
        Require(threads == process.Backend.ReadCached(own), "actual thread current shares original canonical slot");
        var storage = new WindowsStorageOwner(TimeSpan.FromSeconds(15), TimeProvider.System, _ => BackendMessage.Create(0, 0));
        var identities = storage.ReadIdentities();
        Require(identities.Error is null, "actual storage directory query");
        var request = new StorageSensorReadRequest(identities.Identities.ToDictionary(item => item.DisplayIndex, item => item.ExactIdentityKey));
        var trim = storage.ReadTrimFields(request);
        foreach (var row in trim.Devices)
        {
            var value = InformationFieldProjection.Read(row.DeviceIdentity, row.Fields.Single(), trim.ObservedAt);
            Require(value.Field.FieldName == "storage_all_trim_capability" && (value.Value is null || value.Value.Boolean.HasValue), "actual selected trim is bool or unavailable");
        }
        var disks = new WindowsDiskLayoutInformationReader().ReadFields();
        foreach (var row in disks.Devices)
        {
            var field = row.Fields.Single(item => item.Definition.Name == "storage_all_trim_capability");
            var value = InformationFieldProjection.Read(row.DeviceIdentity, field, disks.ObservedAt);
            Require(value.Value is null || value.Value.Boolean.HasValue, "actual opened PnP disk trim route");
        }
        Console.WriteLine($"Process/storage gap actual Windows readonly consumer passed: process thread count {threads.Value!.Integer}; selected physical trim objects {trim.Devices.Count}, PnP disks {disks.Devices.Count}; unavailable trim is not false.");
    }
    private static int ProcessFieldCount() => ResourceManager.ReadWrite.ProcessFields.ProcessMetricFieldRegistry.Definitions.Count;
    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] values) => values.ToDictionary(item => item.Name, item => item.Value);
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
}
