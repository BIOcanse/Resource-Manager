using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class NpuInformationSubscriptionConsumer
{
    private const string Pnp = @"ACPI\RECORDED_NPU\INSTANCE_A";
    private const string Identity = "windows-npu|" + Pnp;

    internal static async Task RunRecordedAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            using var passive = DeviceTopologyCaptureOwner.CreateNpuInformation();
            Require(passive.CreateBackend().Catalog.Fields.IsEmpty && passive.ReadState().Snapshot is null,
                "default factory is passive");
        }
        var reader = new RecordedReader(Snapshot([Row("Recorded NPU")]));
        var policy = new DeviceTopologyCapturePolicy(reader,
            static snapshot => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot.NpuInformation))),
            static (snapshot, time) => WindowsInformationFieldProjection.Npus(snapshot?.NpuInformation, time),
            static (_, _) => Task.CompletedTask,
            static _ => new DeviceTopologyCaptureFailure("recorded-npu-failure", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { });
        using var owner = new DeviceTopologyCaptureOwner(new NoAcpiReader(), policy);
        var backend = owner.CreateBackend();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Require(reader.Reads == 0 && backend.Catalog.Fields.IsEmpty, "construction is passive");
        var catalog = await backend.DiscoverAsync(timeout.Token);
        var names = new[] { "npu_all_device_id_declared", "npu_all_name", "npu_all_driver_version_declared",
            "npu_all_location_information_reported", "npu_all_location_paths_declared" };
        var fields = names.Select(name => new FieldReference(Identity, name)).ToImmutableArray();
        Require(catalog.Devices.Length == 1 && catalog.Fields.Length == 5
            && catalog.Fields.Select(field => field.Field).ToHashSet().SetEquals(fields), "exact five canonical field subscription");
        foreach (var capability in catalog.Fields)
            Require(capability.Read.Supported && capability.Read.Dependencies.IsEmpty && !capability.Write.Supported,
                "subscription reuses public read-only capabilities");

        await ObserveOnceAsync(expectCurrent: true);
        // The composition owns its borrow, while the original owner remains available for direct calls.
        reader.Set(Snapshot([Row("Direct after Stop")]));
        var direct = await backend.CollectAsync(fields, timeout.Token);
        Require(direct.Single(reading => reading.Field.FieldName == "npu_all_name").Value?.Text == "Direct after Stop",
            "stopped and disposed subscription leaves direct owner usable");

        reader.Set(Snapshot(null));
        await ObserveOnceAsync(expectCurrent: false);
        Require(backend.Catalog.Fields.Length == 5, "source unavailable retains subscribed directory");
        reader.Set(Snapshot([new(Identity, Pnp, "Recovered", null, null, [])]));
        var missing = await backend.CollectAsync(fields, timeout.Token);
        Require(missing.Where(reading => reading.Field.FieldName is "npu_all_driver_version_declared"
                or "npu_all_location_information_reported")
            .All(reading => reading is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }),
            "direct missing declarations remain unavailable after subscription stop");
        Require(missing.Single(reading => reading.Field.FieldName == "npu_all_location_paths_declared")
            is { Status: FieldObservationStatus.Current, Value.Structure: { } emptyPaths } && emptyPaths.GetArrayLength() == 0,
            "legal empty path list stays current");
        reader.Set(Snapshot([]));
        var removed = await backend.CollectAsync(fields, timeout.Token);
        Require(backend.Catalog.Devices.IsEmpty && backend.Catalog.Fields.IsEmpty
            && removed.All(reading => reading is { Status: FieldObservationStatus.Unavailable, Value: null }),
            "successful empty directory removes the original subscribed identity");
        Console.WriteLine("[NPU-SUBSCRIPTION-PACKAGE-RECORDED] native-composition; five-fields; same-current; failure-clears; stop-drains; direct-owner-usable; no-live-capture.");

        async Task ObserveOnceAsync(bool expectCurrent)
        {
            var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
            var configuration = new NativeSamplingSubscriptionConfiguration
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(), Generation = 1,
                MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
                MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
                DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
                ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
            };
            var failures = new List<SubscriptionWorkerFailure>();
            await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(), settings,
                in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(250));
            Require(reader.Reads >= 1, "composition retains the already discovered original owner");
            await composition.StartAsync(timeout.Token);
            await using var stream = composition.SubscribeAsync("recorded-npu", fields, TimeSpan.FromMilliseconds(250), timeout.Token).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "native composition delivers a complete observation");
            var readings = stream.Current;
            await composition.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.Count == 0, "Stop drains the native run without late observations or worker errors");
            Require(readings.Length == 5, "complete five-field delivery");
            var current = owner.ReadState().InformationFields;
            foreach (var reading in readings)
            {
                var cached = backend.ReadCached(reading.Field);
                var original = InformationFieldProjection.Read(reading.Field.DeviceIdentity, current.FindField(reading.Field)!, current.ObservedAt);
                Require(Equivalent(reading, cached) && Equivalent(reading, original),
                    "native subscription, cache and original public owner share current values and observation times");
                Require(expectCurrent ? reading.Status == FieldObservationStatus.Current && reading.Value is not null
                    : reading is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null },
                    "subscription follows successful capture or clears all five values on unavailable source");
            }
            if (expectCurrent)
                Require(readings.Single(reading => reading.Field.FieldName == "npu_all_device_id_declared").Value?.Text == Pnp
                    && readings.Single(reading => reading.Field.FieldName == "npu_all_name").Value?.Text == "Recorded NPU"
                    && readings.Single(reading => reading.Field.FieldName == "npu_all_driver_version_declared").Value?.Text == "1.2.3"
                    && readings.Single(reading => reading.Field.FieldName == "npu_all_location_information_reported").Value?.Text == "Reported location"
                    && readings.Single(reading => reading.Field.FieldName == "npu_all_location_paths_declared").Value?.Structure is { } paths
                    && paths.EnumerateArray().Select(value => value.GetString()).SequenceEqual(["ACPI(_SB)#ACPI(NPU0)"]),
                    "native composition delivers all five exact recorded declarations");
        }
    }

    private static bool Equivalent(FieldReading left, FieldReading right)
        => left.Field == right.Field && left.Status == right.Status && left.ObservedAt == right.ObservedAt
            && left.SourceId == right.SourceId && JsonSerializer.Serialize(left.Value) == JsonSerializer.Serialize(right.Value);
    private static void Require(bool condition, string contract)
    { if (!condition) throw new InvalidDataException("NPU recorded subscription contract failed: " + contract); }
    private static DeviceTopologyNpuInformation Row(string name) => new(Identity, Pnp, name, "1.2.3", "Reported location", ["ACPI(_SB)#ACPI(NPU0)"]);
    private static DeviceTopologySnapshot Snapshot(IReadOnlyList<DeviceTopologyNpuInformation>? rows)
        => new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []) { NpuInformation = rows };
    private sealed class RecordedReader(DeviceTopologySnapshot initial) : IDeviceTopologyReader
    {
        private DeviceTopologySnapshot snapshot = initial;
        internal int Reads { get; private set; }
        public DeviceTopologySnapshot ReadSnapshot() { Reads++; return snapshot; }
        internal void Set(DeviceTopologySnapshot next) => snapshot = next;
    }
    private sealed class NoAcpiReader : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => null; }
}
