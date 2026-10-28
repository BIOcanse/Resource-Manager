using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

internal static class NpuInformationConsumer
{
    private static readonly string[] Names = ["npu_all_device_id_declared", "npu_all_name",
        "npu_all_driver_version_declared", "npu_all_location_information_reported", "npu_all_location_paths_declared"];
    private const string Pnp = @"PCI\VEN_RECORDED&DEV_NPU\INSTANCE_A";
    private const string OtherPnp = @"ACPI\RECORDED_NPU\INSTANCE_B";
    private const string Identity = "windows-npu|" + Pnp;

    internal static async Task RunRecordedAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            using var passive = DeviceTopologyCaptureOwner.CreateNpuInformation();
            Require(passive.CreateBackend().Catalog.Fields.IsEmpty && passive.ReadState().Snapshot is null,
                "default public factory and catalog remain passive");
        }
        var reader = new RecordedReader(Snapshot([
            new(Identity, Pnp, "Recorded NPU", "1.2.3.4", "Reported location", ["PCIROOT(0)#PCI(0100)"]),
            new("windows-npu|" + OtherPnp, OtherPnp, "Recorded NPU", null, null, [])]));
        var policy = new DeviceTopologyCapturePolicy(reader,
            static snapshot => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot.NpuInformation))),
            static (snapshot, time) => new(snapshot, WindowsInformationFieldProjection.Npus(snapshot?.NpuInformation, time)),
            static (_, _) => Task.CompletedTask,
            static _ => new DeviceTopologyCaptureFailure("recorded-npu-failure", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { });
        using var owner = new DeviceTopologyCaptureOwner(new NoAcpiReader(), policy);
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        Require(core.Catalog.Fields.IsEmpty && reader.Reads == 0, "construction performs no capture");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var catalog = await core.DiscoverAsync(timeout.Token);
        Require(catalog.Devices.Length == 2 && catalog.Fields.Length == 10,
            "same-name PCI and non-PCI instances stay separate");
        Require(owner.ReadState().InformationFields.Devices.All(device => device.ObjectType == "npu"), "independent NPU object category");
        foreach (var device in catalog.Devices)
            Require(catalog.Fields.Where(field => field.Field.DeviceIdentity == device.Identity)
                .Select(field => field.Field.FieldName).ToHashSet(StringComparer.Ordinal).SetEquals(Names),
                "exact five canonical fields without telemetry or old aliases");
        var fields = Names.Select(name => new FieldReference(Identity, name)).ToImmutableArray();
        foreach (var field in fields)
        {
            var capability = core.Query(field);
            var declared = owner.ReadState().InformationFields.FindField(field)!;
            Require(capability.Read.Supported && capability.Read.Dependencies.IsEmpty && !capability.Write.Supported
                && capability.Definition.Unit == "text" && capability.ReadSource == declared.Definition.SourceId
                && capability.Definition.ValueKind == (field.FieldName == Names[4] ? "structured" : "string")
                && declared.Definition.ValueKind == (field.FieldName == Names[4] ? "text-list" : "text"),
                "public definitions preserve read-only types and original sources");
        }
        Require(core.ReadCached(fields[0]).Value?.Text == Pnp
            && core.ReadCached(fields[1]).Value?.Text == "Recorded NPU"
            && core.ReadCached(fields[2]).Value?.Text == "1.2.3.4"
            && core.ReadCached(fields[3]).Value?.Text == "Reported location"
            && core.ReadCached(fields[4]).Value?.Structure is { } paths
            && paths.EnumerateArray().Select(value => value.GetString()).SequenceEqual(["PCIROOT(0)#PCI(0100)"]),
            "all five values come from their exact recorded instance");
        var other = new FieldReference("windows-npu|" + OtherPnp, Names[4]);
        Require(core.ReadCached(other) is { Status: FieldObservationStatus.Current, Value.Structure: { } emptyPaths }
            && emptyPaths.GetArrayLength() == 0, "successful empty location paths is a current empty array");
        RequireEmpty(core.ReadCached(new("windows-npu|" + OtherPnp, Names[2])));
        var passiveReads = reader.Reads;
        foreach (var field in fields) _ = core.ReadCached(field);
        Require(reader.Reads == passiveReads, "cache reads never recapture");

        reader.Set(Snapshot([new(Identity, Pnp, "Recorded NPU", null, " ", null)]));
        var missing = await core.CollectAsync(fields, timeout.Token);
        Require(missing.Length == 5 && missing[0].Value?.Text == Pnp && missing[1].Value?.Text == "Recorded NPU",
            "missing optional declarations retain the exact instance");
        foreach (var reading in missing.Skip(2)) RequireEmpty(reading);
        SameCurrent(core, owner, missing);

        reader.Set(Snapshot(null));
        var failedDirectory = await core.CollectAsync(fields, timeout.Token);
        Require(core.Catalog.Fields.Length == 5 && core.Catalog.Devices.Length == 1, "unavailable NPU directory retains known definitions");
        foreach (var reading in failedDirectory) RequireEmpty(reading);
        SameCurrent(core, owner, failedDirectory);

        reader.Set(Snapshot([new(Identity, Pnp, "Recovered NPU", "5.6", null, [])]));
        var recovered = await core.CollectAsync(fields, timeout.Token);
        Require(recovered[1].Value?.Text == "Recovered NPU", "direct collection recovers original owner");
        SameCurrent(core, owner, recovered);
        reader.Throw = true;
        var failedSource = await core.CollectAsync(fields, timeout.Token);
        foreach (var reading in failedSource) RequireEmpty(reading);
        Require(core.Catalog.Fields.Length == 5, "source exception retains directory and clears all values");
        SameCurrent(core, owner, failedSource);

        reader.Throw = false;
        reader.Set(Snapshot([new(Identity, Pnp, "After failure", "7.8", null, [])]));
        await core.CollectAsync(fields, timeout.Token);
        var beforeCancellation = owner.ReadState();
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await core.CollectAsync(fields, canceled.Token); throw new InvalidDataException("Canceled collection completed."); }
            catch (OperationCanceledException) { }
        }
        Require(ReferenceEquals(beforeCancellation, owner.ReadState()), "cancellation does not replace current");
        reader.Set(Snapshot([]));
        var removed = await core.CollectAsync(fields, timeout.Token);
        Require(core.Catalog.Fields.IsEmpty && core.Catalog.Devices.IsEmpty, "successful empty directory removes devices");
        foreach (var reading in removed) RequireEmpty(reading);
        Console.WriteLine("[NPU-PACKAGE-RECORDED] five-fields; exact-instances; same-current; missing-null; failure-clears; empty-removes; no-live-capture.");
    }

    private static void SameCurrent(ReadWriteCore core, DeviceTopologyCaptureOwner owner, ImmutableArray<FieldReading> readings)
    {
        var current = owner.ReadState().InformationFields;
        foreach (var reading in readings)
            Require(Equivalent(core.ReadCached(reading.Field), reading)
                && Equivalent(InformationFieldProjection.Read(reading.Field.DeviceIdentity, current.FindField(reading.Field)!, current.ObservedAt), reading),
                "collect/cache/public owner projection share the same current");
    }

    private static bool Equivalent(FieldReading left, FieldReading right)
        => left.Field == right.Field && left.Status == right.Status && left.ObservedAt == right.ObservedAt
            && left.SourceId == right.SourceId && JsonSerializer.Serialize(left.Value) == JsonSerializer.Serialize(right.Value);

    private static void RequireEmpty(FieldReading reading)
        => Require(reading is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }, "missing or failed declaration stays unavailable");
    private static void Require(bool condition, string contract)
    { if (!condition) throw new InvalidDataException("NPU recorded package contract failed: " + contract); }
    private static DeviceTopologySnapshot Snapshot(IReadOnlyList<DeviceTopologyNpuInformation>? rows)
        => new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []) { NpuInformation = rows };
    private sealed class RecordedReader(DeviceTopologySnapshot initial) : IDeviceTopologyReader
    {
        private DeviceTopologySnapshot snapshot = initial;
        internal int Reads { get; private set; }
        internal bool Throw { get; set; }
        public DeviceTopologySnapshot ReadSnapshot()
        { Reads++; return Throw ? throw new IOException("Recorded NPU source failure") : snapshot; }
        internal void Set(DeviceTopologySnapshot next) => snapshot = next;
    }
    private sealed class NoAcpiReader : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => null; }
}
