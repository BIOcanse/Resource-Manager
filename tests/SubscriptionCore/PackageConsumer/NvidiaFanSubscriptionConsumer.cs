using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class NvidiaFanSubscriptionConsumer
{
    internal static async Task RunRecordedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = new RecordedNvidiaFanResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.CreateDefault(), fixture.Create(), new(),
            unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Require(core.Catalog.Fields.IsEmpty && fixture.Reads == 0 && fixture.DirectoryReads == 0, "construction is passive");
        var catalog = await core.DiscoverAsync(timeout.Token);
        var fields = RecordedNvidiaFanResources.Fields(catalog);
        Require(fields.Length == 4 && catalog.Devices.Count(device => device.Resource == "gpu_fan") == 2
            && fields.Select(field => field.DeviceIdentity).ToHashSet().SetEquals(fixture.Fans.Select(fan => fan.ExactIdentityKey)),
            "two explicit fan selectors keep the same exact parent and separate public objects");
        Require(fixture.Reads == 0 && owner.ReadCachedSnapshot() is null && fixture.CatalogEngineProbeCount == 1,
            "discovery performs the original single engine directory probe without sampling fan values");
        foreach (var field in fields)
            Require(core.Query(field) is { Read.Supported: true, Write.Supported: false, Definition.ValueKind: "float64" },
                "public fan fields remain read-only numeric values");
        await ObserveOnceAsync(failed: false);
        // The native composition owns only its original borrow, so the caller can still collect directly.
        RecordedNvidiaFanResources.RequireValues(await core.CollectAsync(fields, timeout.Token), fixture.Fans);
        Require(fixture.CatalogEngineProbeCount == 1, "direct fan collection does not repeat the engine directory probe");
        fixture.Fail = true;
        await ObserveOnceAsync(failed: true);
        Require(RecordedNvidiaFanResources.Fields(core.Catalog).Length == 4 && fixture.CatalogEngineProbeCount == 1,
            "failed values retain the original field definitions without another directory probe");
        fixture.Fail = false;
        RecordedNvidiaFanResources.RequireValues(await core.CollectAsync(fields, timeout.Token), fixture.Fans);
        var current = owner.ReadCachedSnapshot();
        var reads = fixture.Reads;
        foreach (var field in fields) _ = core.ReadCached(field);
        Require(ReferenceEquals(current, owner.ReadCachedSnapshot()) && fixture.Reads == reads && fixture.UnselectedReads == 0
            && fixture.CatalogEngineProbeCount == 1,
            "passive original current never captures additional or unrelated sources");
        Console.WriteLine("[NVIDIA-FAN-SUBSCRIPTION-PACKAGE-RECORDED] native-composition; two-selectors; percent=135/0; rpm-optional; same-current; failure-clears; stop-drains; direct-owner-usable; no-live-capture.");

        async Task ObserveOnceAsync(bool failed)
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
            await composition.StartAsync(timeout.Token);
            var subscriptions = new FieldSubscriptionCore(core, composition);
            await using var stream = subscriptions.SubscribeAsync("recorded-nvml-fans", fields,
                TimeSpan.FromMilliseconds(250), timeout.Token).GetAsyncEnumerator();
            ImmutableArray<FieldReading> readings = [];
            for (var attempt = 0; attempt < 8; attempt++)
            {
                Require(await stream.MoveNextAsync(), "native composition delivers exact selected fan fields");
                readings = stream.Current;
                Require(readings.Select(reading => reading.Field).SequenceEqual(fields), "subscription keeps exact requested field order");
                foreach (var reading in readings)
                    FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, core.ReadCached(reading.Field));
                if (failed ? readings.All(reading => reading.Status == FieldObservationStatus.Unavailable)
                    : readings.Any(reading => reading.Field.FieldName == RecordedNvidiaFanResources.PercentName
                        && reading.Status == FieldObservationStatus.Current)) break;
            }
            await composition.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.Count == 0, "Stop drains the native run and closes its old stream");
            if (failed)
                Require(readings.All(reading => reading is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }),
                    "source failure clears all subscribed fan values");
            else RecordedNvidiaFanResources.RequireValues(readings, fixture.Fans);
            Require(fixture.LastTargets.Length == 2 && fixture.LastTargets.All(target => target.IncludePercent && target.IncludeRpm)
                && fixture.LastTargets.Select(target => target.Fan).ToHashSet().SetEquals(fixture.Fans)
                && fixture.UnselectedReads == 0 && fixture.CatalogEngineProbeCount == 1,
                "native borrowing queries only the two exact selected NVML fan targets without repeating the directory probe");
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("NVIDIA fan recorded subscription: " + message); }
}

internal sealed class RecordedNvidiaFanResources
{
    internal const string PercentName = "gpu_nvidia_fan_speed_percent_of_max";
    internal const string RpmName = "gpu_nvidia_fan_speed_rpm";
    private const string Parent = @"PCI\VEN_10DE&DEV_2684\RECORDED-FAN-GPU";
    internal NvidiaNvmlFanIdentity[] Fans { get; } = [new(Parent, 0), new(Parent, 1)];
    internal int Reads { get; private set; }
    internal int DirectoryReads { get; private set; }
    internal int CatalogEngineProbeCount { get; private set; }
    internal int UnselectedReads { get; private set; }
    internal bool Fail { get; set; }
    internal NvidiaNvmlFanReadTarget[] LastTargets { get; private set; } = [];

    internal WindowsHardwareMetricResources Create() => new()
    {
        CpuName = "Recorded CPU", MemoryDescription = "Recorded memory",
        Controls = new WindowsMetricControlResources
        {
            ControlReadCpuIdentity = static () => null,
            ControlReadCpuTopology = static () => new CpuOsTopologyInformation(null, null)
        },
        ReadCpuCounters = () => throw Unexpected(),
        ReadMemory = () => throw Unexpected(), ReadVirtualMemory = () => throw Unexpected(),
        ReadNumaAvailable = (_, _) => throw Unexpected(),
        ReadNumaIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadGpuInventory = Inventory,
        ReadCpuFrequency = () => throw Unexpected(),
        ReadGpuUsage = _ => { CatalogEngineProbeCount++; return PdhGpuEngineUsageRead.NotRequested; },
        ReadNvmlAsync = (_, _, _) => throw Unexpected(), ReadNvapiAsync = (_, _, _) => throw Unexpected(),
        ReadAdlxAsync = (_, _, _) => throw Unexpected(), ReadAmdSmu = _ => throw Unexpected(),
        ReadIntelGpu = (_, _, _) => throw Unexpected(), ReadIntelMsr = (_, _) => throw Unexpected(),
        ReadBatteryIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadBattery = (_, _) => throw Unexpected(),
        ReadNetworkIdentities = static () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadNetworkInterfaces = (_, _) => throw Unexpected(), ReadSystemIo = _ => throw Unexpected(),
        ReadStorageIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadFanIdentities = static _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
        ReadStorageSensors = () => throw Unexpected(), ReadNvmeSensors = _ => throw Unexpected(),
        ReadAtaSensors = _ => throw Unexpected(), ReadHardwareMonitor = () => throw Unexpected(),
        ReadNotebookFans = () => throw Unexpected(), SynchronizeNotebookDemand = static _ => { },
        ReadNvmlFanIdentities = _ => { DirectoryReads++; return Fans; },
        ReadNvmlFansAsync = (targets, _, token) =>
        {
            token.ThrowIfCancellationRequested(); Reads++; LastTargets = targets.ToArray();
            if (targets.Any(target => !Fans.Contains(target.Fan))) throw new InvalidDataException("Unexpected exact fan target.");
            return Task.FromResult<IReadOnlyList<NvidiaNvmlFanReading>>(targets.Select(target => new NvidiaNvmlFanReading(target.Fan,
                Fail || !target.IncludePercent ? null : target.Fan.FanIndex == 0 ? 135u : 0u,
                Fail || !target.IncludeRpm || target.Fan.FanIndex == 1 ? null : 2100u)).ToArray());
        }
    };

    private Exception Unexpected()
    { UnselectedReads++; return new InvalidOperationException("Unselected recorded source was queried."); }
    private static WindowsGpuAdapterInventoryRead Inventory()
    {
        var physical = new WindowsGpuPhysicalIdentity(0, Parent, WindowsGpuPnpKind.Pci, true, true, 0, 1, 0, 0,
            0x10DE, 0x2684, 0x10DE, 1, 1,
            WindowsGpuIdentityEvidence.KmtHardwarePnpKey | WindowsGpuIdentityEvidence.CmExactDevNode,
            WindowsGpuPhysicalIdentityStatus.Bound, 0);
        WindowsGpuAdapter[] adapters = [new(0, "Recorded GPU", 0x10DE, 0x2684, 1,
            new AdapterLuid { LowPart = 1, HighPart = 8 }, false, 8UL * 1024 * 1024 * 1024, WindowsGpuAdapterKind.Dedicated)
            { PhysicalIdentityStatus = SamplingObservationStatus.Current, PhysicalIdentities = [physical] }];
        return new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0,
            WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(adapters), adapters);
    }
    internal static ImmutableArray<FieldReference> Fields(ReadWriteCatalog catalog)
        => catalog.Fields.Where(field => field.Field.FieldName is PercentName or RpmName)
            .Select(field => field.Field).OrderBy(field => field.DeviceIdentity, StringComparer.Ordinal)
            .ThenBy(field => field.FieldName, StringComparer.Ordinal).ToImmutableArray();
    internal static void RequireValues(ImmutableArray<FieldReading> readings, IReadOnlyList<NvidiaNvmlFanIdentity> fans)
    {
        foreach (var reading in readings)
        {
            var fan = fans.Single(fan => fan.ExactIdentityKey == reading.Field.DeviceIdentity);
            double? expected = reading.Field.FieldName == PercentName ? fan.FanIndex == 0 ? 135 : 0
                : fan.FanIndex == 0 ? 2100 : null;
            if (reading.SourceId != NativeMetricSnapshotSources.NvidiaNvml
                || (expected is null ? reading is not { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
                    : reading.Status != FieldObservationStatus.Current || reading.Value?.Number != expected || reading.ObservedAt is null))
                throw new InvalidDataException("Recorded NVML fan intended speed/optional RPM contract failed.");
        }
    }
}

