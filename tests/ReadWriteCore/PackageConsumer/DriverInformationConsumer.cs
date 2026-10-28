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

internal static class DriverInformationConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var http = new HttpClient();
        await RunRecordedVersionDeclarationAsync(http);
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        if (!core.Catalog.Fields.IsEmpty) throw new InvalidDataException("Driver constructor enumerated devices.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var catalog = await core.DiscoverAsync(timeout.Token);
        if (catalog.Fields.IsEmpty)
        {
            Console.WriteLine($"Actual device-bound driver directory: state={owner.ReadState().State}; reason={owner.ReadState().FailureCode}; no available/value claim.");
            return;
        }
        var selected = catalog.Fields.Where(field => field.Definition.Name is "driver_all_provider_declared"
                or "driver_all_date_declared" or "driver_all_version_declared")
            .Select(field => field.Field).ToImmutableArray();
        if (selected.Length != catalog.Fields.Length) throw new InvalidDataException("Unexpected driver default fields.");
        foreach (var field in selected)
        {
            var capability = core.Query(field);
            if (!field.DeviceIdentity.StartsWith("windows-device-driver|", StringComparison.Ordinal)
                || !capability.Read.Supported || !capability.Read.Dependencies.IsEmpty || capability.Write.Supported)
                throw new InvalidDataException("Driver identity/support contract mismatch.");
        }
        var values = await core.CollectAsync(selected, timeout.Token);
        var actual = 0;
        foreach (var value in values)
        {
            if (core.Catalog.Fields.Any(field => field.Field == value.Field))
            {
                if (core.ReadCached(value.Field) != value) throw new InvalidDataException("Driver cached read recaptured/diverged.");
            }
            else if (value.Status != FieldObservationStatus.Unavailable || value.Value is not null)
                throw new InvalidDataException("Retired driver identity was fabricated as a current declaration.");
            if (value.Status != FieldObservationStatus.Current)
            {
                if (value.Value is not null) throw new InvalidDataException("Unavailable driver declaration fabricated.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(value.Value?.Text)) throw new InvalidDataException("Driver text missing.");
            if (value.Field.FieldName == "driver_all_date_declared"
                && !DateOnly.TryParseExact(value.Value.Text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
                throw new InvalidDataException("Driver declaration date is not an ISO date.");
            actual++;
        }
        Console.WriteLine($"Actual device-bound driver declarations: objects={catalog.Devices.Length}; current={actual}; unavailable={values.Length - actual}; same-current=yes; provider=INF; date=DriverVer-declared; not-install/hardware-time.");
    }

    private static async Task RunRecordedVersionDeclarationAsync(HttpClient http)
    {
        const string deviceId = @"PCI\RM-RECORDED-DRIVER";
        var reader = new RecordedDriverTopologyReader(CreateSnapshot(version: null));
        var policy = new DeviceTopologyCapturePolicy(
            reader,
            static snapshot => Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(snapshot.DriverInformation))),
            static (snapshot, observedAt) => new(snapshot, WindowsInformationFieldProjection.Drivers(
                snapshot?.DriverInformation, observedAt)),
            static (_, _) => Task.CompletedTask,
            static _ => new DeviceTopologyCaptureFailure("recorded-driver-source-failure", []),
            static (_, _, _, _) => { },
            static _ => { },
            static () => { });

        using var owner = new DeviceTopologyCaptureOwner(new RecordedNoAcpiReader(), policy);
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var catalog = await core.DiscoverAsync(timeout.Token);
        var identity = "windows-device-driver|" + deviceId.ToUpperInvariant();
        var fieldNames = catalog.Fields.Where(field => field.Field.DeviceIdentity == identity)
            .Select(static field => field.Field.FieldName).ToHashSet(StringComparer.Ordinal);
        Require(fieldNames.SetEquals([
            "driver_all_provider_declared",
            "driver_all_date_declared",
            "driver_all_version_declared"]),
            "recorded owner publishes exactly provider/date/version for the original device identity");

        var provider = new FieldReference(identity, "driver_all_provider_declared");
        var date = new FieldReference(identity, "driver_all_date_declared");
        var version = new FieldReference(identity, "driver_all_version_declared");
        var versionCapability = core.Query(version);
        var declaredVersion = owner.ReadState().InformationFields.FindField(version);
        Require(versionCapability.Read.Supported && versionCapability.Read.Dependencies.IsEmpty
            && !versionCapability.Write.Supported
            && versionCapability.ReadSource == "windows.cfgmgr32.driver-properties"
            && versionCapability.Definition.ValueKind == "string" && versionCapability.Definition.Unit == "text"
            && declaredVersion is
                { Definition: { ValueKind: "text", Unit: "text", SourceId: "windows.cfgmgr32.driver-properties" }, Value: null },
            "public catalog and original owner current expose nullable read-only declared text");

        var missing = core.ReadCached(version);
        Require(missing.Status == FieldObservationStatus.Unavailable && missing.Value is null
            && missing.ObservedAt is null,
            "missing recorded version stays unavailable rather than becoming empty text or numeric zero");
        Require(core.ReadCached(provider) is { Status: FieldObservationStatus.Current, Value.Text: "Recorded INF provider" }
            && core.ReadCached(date) is { Status: FieldObservationStatus.Current, Value.Text: "2024-02-29" },
            "missing version does not erase provider or DriverVer date");

        reader.SetSnapshot(CreateSnapshot("31.0.101.5534"));
        var exactSuccess = await core.CollectAsync([version], timeout.Token);
        Require(exactSuccess.Length == 1 && exactSuccess[0].Field == version
            && exactSuccess[0].Status == FieldObservationStatus.Current
            && exactSuccess[0].Value?.Text == "31.0.101.5534"
            && core.ReadCached(version) == exactSuccess[0]
            && owner.ReadState().Snapshot?.DriverInformation is { Count: 1 } successRows
            && successRows[0].VersionDeclared == "31.0.101.5534",
            "exact single-field collection updates the original owner current and cached value");

        reader.SetSnapshot(CreateSnapshot(version: null));
        var exactMissing = await core.CollectAsync([version], timeout.Token);
        Require(exactMissing.Length == 1 && exactMissing[0].Field == version
            && exactMissing[0].Status == FieldObservationStatus.Unavailable
            && exactMissing[0].Value is null && exactMissing[0].ObservedAt is null
            && core.ReadCached(version) == exactMissing[0]
            && owner.ReadState().Snapshot?.DriverInformation is { Count: 1 } missingRows
            && missingRows[0].VersionDeclared is null,
            "a missing declaration clears only version and remains unavailable in the same current");
        Require(core.ReadCached(provider) is { Status: FieldObservationStatus.Current, Value.Text: "Recorded INF provider" }
            && core.ReadCached(date) is { Status: FieldObservationStatus.Current, Value.Text: "2024-02-29" },
            "provider/date current values remain intact when version is absent");

        Console.WriteLine("[DRIVER-VERSION-PACKAGE-RECORDED] public-definition=yes; exact-identity=yes; missing=unavailable; collection=one-field; provider-date=retained.");

        DeviceTopologySnapshot CreateSnapshot(string? version)
            => new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], [])
            {
                DriverInformation = [new DeviceTopologyDriverInformation(deviceId, "Recorded INF provider", "2024-02-29")
                    { VersionDeclared = version }]
            };
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidDataException($"Driver package-consumer contract failed: {contract}.");
    }

    private sealed class RecordedDriverTopologyReader(DeviceTopologySnapshot snapshot) : IDeviceTopologyReader
    {
        private DeviceTopologySnapshot current = snapshot;
        public DeviceTopologySnapshot ReadSnapshot() => current;
        internal void SetSnapshot(DeviceTopologySnapshot next) => current = next;
    }

    private sealed class RecordedNoAcpiReader : IAcpiFirmwareTableReader
    {
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => null;
    }
}


