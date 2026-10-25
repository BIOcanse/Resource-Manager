using ResourceManager.App.Domain.Metrics;
using System.Collections.Immutable;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

internal static class DeviceTopologyCaptureOwnerConsumer
{
    // Recorded contract checks. No hardware access; separate from the real Windows route below.
    internal static async Task RunRecordedAsync()
    {
        using var owner = new DeviceTopologyCaptureOwner(new RecordedFirmware());
        using var client = new HttpClient();
        var backend = Backend(owner);
        var core = new ReadWriteCore(backend, new DependencyActions(client, []));
        await core.DiscoverAsync();
        var field = core.Catalog.Fields.Single(f => f.Field.FieldName == "firmware_all_acpi_signature_instance_count_reported").Field;
        Require(core.Query(field).Read.Supported, "canonical support query");
        using var notification = owner.SubscribeAcpiPublications();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = notification.WaitAsync(timeout.Token).AsTask();
        var captured = (await core.CollectAsync([field], timeout.Token)).Single();
        await waiting;
        Require(captured.Value?.Integer == MetricInteger.Parse("2") && core.ReadCached(field).Value?.Integer == MetricInteger.Parse("2"),
            "canonical capture, external publication and cached read share one owner");
        Require(typeof(DeviceTopologySnapshot).Assembly == typeof(ReadWriteCore).Assembly
            && typeof(BackendMessage).Assembly == typeof(ReadWriteCore).Assembly, "passive contracts require no product assembly");
        Console.WriteLine("Device/ACPI owner recorded consumer completed; not a real firmware result.");
    }

    // Invoke explicitly only in the admitted Windows consumer route. This calls the real SDK firmware API.
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var owner = new DeviceTopologyCaptureOwner(new WindowsAcpiFirmwareTableReader());
        using var client = new HttpClient();
        var core = new ReadWriteCore(Backend(owner), new DependencyActions(client, []));
        var catalog = await core.DiscoverAsync();
        if (catalog.Fields.IsEmpty)
        {
            Console.WriteLine("Actual SDK ACPI enumeration produced no canonical fields (empty or unavailable); no available-field claim.");
            return;
        }
        var fields = catalog.Fields.Select(f => f.Field).ToImmutableArray();
        Require(fields.All(f => core.Query(f).Read.Supported), "actual ACPI fields expose read support");
        using var publication = owner.SubscribeAcpiPublications();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = publication.WaitAsync(timeout.Token).AsTask();
        var captured = await core.CollectAsync(fields, timeout.Token);
        await waiting;
        Require(captured.Length == fields.Length, "actual canonical selected-field batch");
        Require(owner.ReadState().InformationFields.Devices.Count == 0, "ACPI-only binding did not manufacture topology");
        Console.WriteLine("Actual independent SDK ACPI canonical consumer completed using the shared capture/current owner.");
    }

    private static InformationReadWriteBackend Backend(DeviceTopologyCaptureOwner owner)
        => new(() => owner.ReadState().AcpiInformationFields, token => owner.CaptureAcpiInformationAsync(token),
            static _ => new(true, null, []));

    private sealed class RecordedFirmware : IAcpiFirmwareTableReader
    {
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => [new(0x50434146, "FACP", 2, null)];
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
