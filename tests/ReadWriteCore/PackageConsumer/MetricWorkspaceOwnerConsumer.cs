using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Fields;
using ResourceManager.ReadWrite.Dependencies;

internal static class MetricWorkspaceOwnerConsumer
{
    // Uses the packaged real native owner and original managed current with recorded raw inputs.
    // The default Windows numeric sampler is verified separately from this recorded contract.
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Require(typeof(NativeMetricSnapshotWorkspace).Assembly == typeof(ReadWriteCore).Assembly, "workspace package owner");
        var configuration = Configuration();
        using var owner = new NativeMetricSnapshotWorkspace(configuration, Catalog());
        var catalog = NativeMetricSnapshotCatalogProjection.Create(Catalog(), [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        owner.ReplaceCatalog(catalog, at);
        var handles = NativeMetricSnapshotRequestProjection.CreateMetricHandles(MetricSampleRequest.ForIds(["memory.usage"]), catalog);
        Require(handles.Length == 2, "used-field request includes total support field");
        var modes = catalog.Sources.Select(row => new NativeMetricSnapshotSourceRuntimeMode(row.SourceHandle, 1,
            NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var plan = owner.PlanCollection(handles, modes, false, at);
        var observations = plan.MetricsForSource(1).Select(metric => NativeMetricSnapshotObservationProjection.CreateUnsignedObservation(
            metric, catalog.RuleBindings[metric.RuleHandle], new(1, 1, 1), at,
            metric.MetricKind == (uint)NativeMetricSnapshotMetricKind.MemoryUsed ? 512UL : 1024UL)).ToImmutableArray();
        var before = owner.ReadHeader();
        owner.CompleteSource(plan, new(1, 1, 1, at, NativeMetricSnapshotSourceStatus.Complete, observations, null, [], 0, 0), at);
        var frame = owner.ReadCommitted();
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame, "Recorded CPU", "Recorded RAM", 1);
        Require(snapshot.Memory.UsedBytes == 512 && snapshot.Memory.TotalBytes == 1024, "original committed canonical memory projection");
        Require(snapshot.VirtualMemory.Detail.Length == 0 && snapshot.Items["memory.usage"].Label == "memory.usage",
            "independent numeric projection does not supply product language copy");
        Require(frame.Header.CommittedGeneration > before.CommittedGeneration, "completion advances committed owner");
        var current = owner.ReadHeader();
        _ = owner.ReadCommitted();
        Require(owner.ReadHeader().StateRevision == current.StateRevision, "committed read does not collect or advance current");
        var image = owner.ExportAndPersist(at);
        Require(image.Catalog.CatalogIdentitySha256 == catalog.CatalogIdentitySha256, "export preserves exact catalog identity");
        // The native counter state and original merged publication serve their existing distinct roles.
        // The third object below is catalog metadata; it does not cache another set of readings.
        var mergedCurrent = new LastSuccessfulHardwareMetricSnapshot();
        Require(mergedCurrent.GetType().Assembly == typeof(ReadWriteCore).Assembly, "merged current package owner");
        mergedCurrent.Publish(snapshot, MetricSampleRequest.ForIds(["memory.usage"]));
        HardwareMetricSnapshot publishedCatalog = snapshot;
        ulong sequence = 1;
        HardwareMetricSnapshot ReadOwner() => mergedCurrent.Read() ?? throw new InvalidOperationException("No committed current.");
        ValueTask<HardwareMetricSnapshot> Capture(MetricSampleRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var selected = NativeMetricSnapshotRequestProjection.CreateMetricHandles(request, owner.ReadCatalog());
            if (!selected.IsEmpty)
            {
                var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(++sequence * 1_000)));
                var selectedPlan = owner.PlanCollection(selected, modes, false, capturedAt);
                var raw = selectedPlan.MetricsForSource(1).Select(metric => NativeMetricSnapshotObservationProjection.CreateUnsignedObservation(
                    metric, catalog.RuleBindings[metric.RuleHandle], new(1, 1, sequence), capturedAt,
                    metric.MetricKind == (uint)NativeMetricSnapshotMetricKind.MemoryUsed ? 512UL : 1024UL)).ToImmutableArray();
                owner.CompleteSource(selectedPlan, new(1, 1, sequence, capturedAt, NativeMetricSnapshotSourceStatus.Complete,
                    raw, null, [], 0, 0), capturedAt);
            }
            var committed = NativeMetricSnapshotCommittedProjection.Create(owner.ReadCommitted(), "Recorded CPU", "Recorded RAM", 1);
            mergedCurrent.Publish(committed, request);
            return ValueTask.FromResult(ReadOwner());
        }
        var backend = new MetricReadWriteBackend(() => publishedCatalog, _ => ReadOwner(), Capture,
            value => publishedCatalog = value,
            static (_, binding) => new(true, null, MetricReadWriteBackend.DependenciesOf(binding.SourceId)),
            static _ => OperationSupport.Unsupported("recorded-read-only-source"),
            static (field, _, _, _) => ValueTask.FromResult(new FieldWriteResult(field, "unsupported", "recorded-read-only-source")));
        using var http = new HttpClient();
        var core = new ReadWriteCore(backend, new DependencyActions(http, []));
        var discovered = await core.DiscoverAsync();
        var memoryField = discovered.Fields.Single(item => item.Field.FieldName == "memory_all_physical_used_bytes").Field;
        var address = snapshot.FieldBindings.Values.Single(binding => binding.DeviceIdentity == memoryField.DeviceIdentity
            && binding.Field.Name == memoryField.FieldName).RequestAddress;
        var beforeLegacyCachedRead = owner.ReadHeader();
        var initialCurrent = mergedCurrent.Read()!;
        var fromLegacy = core.ReadCached(memoryField);
        Require(fromLegacy.Status == FieldObservationStatus.Current && fromLegacy.Value?.Integer == "512"
            && owner.ReadHeader().StateRevision == beforeLegacyCachedRead.StateRevision
            && ReferenceEquals(initialCurrent, mergedCurrent.Read()),
            "legacy publication reaches canonical cached API without collection");
        mergedCurrent.ConfigureHistory([new HistoryRequirement(address, 3)]);
        Require(core.Query(memoryField).Read.Supported && !core.Query(memoryField).Write.Supported
            && core.Query(memoryField).Read.Dependencies.IsEmpty, "canonical read/write/dependency query");
        var reads = await core.CollectAsync([memoryField]);
        Require(reads[0].Status == FieldObservationStatus.Current && reads[0].Value?.Integer == "512", "native recorded current reaches public core");
        var afterCollect = owner.ReadHeader();
        var afterPublication = mergedCurrent.Read()!;
        var cached = core.ReadCached(memoryField);
        Require(cached.Value?.Integer == "512" && owner.ReadHeader().StateRevision == afterCollect.StateRevision
            && ReferenceEquals(afterPublication, mergedCurrent.Read()), "public cached read shares original current and does not recapture");
        Require(afterPublication.History[address].Length == 2
            && afterPublication.History[address][1].ObservedAt == reads[0].ObservedAt
            && afterPublication.History[address][1].Value is null,
            "original nullable-double history advances at the completed unsigned publication without fabricating a double value");
        mergedCurrent.RecordFailure(MetricSampleRequest.ForIds([address]), DateTimeOffset.FromUnixTimeMilliseconds(5_000), "recorded-source-failure");
        Require(core.ReadCached(memoryField).Status == FieldObservationStatus.Unavailable && core.ReadCached(memoryField).Value is null,
            "canonical failure clears requested current without recollecting");
        Require(mergedCurrent.Read()!.History[address].Length == 3 && mergedCurrent.Read()!.History[address][2].Value is null,
            "failed publication appends null to the original history");
        Console.WriteLine("Packaged native workspace, original merged current/history and canonical core completed recorded input contract; no default Windows collection claim.");
    }

    private static NativeMetricSnapshotCatalogConfiguration Catalog() => new(
        1, new string('A', 64), 8, 64, 32,
        [new(NativeMetricSnapshotSources.WindowsMemory, 1, 1, 100, true, 1, 11),
         new(NativeMetricSnapshotSources.WindowsGpuAdapterOrder, 2, 2, 100, true, 1, 12)],
        [Rule("memory.total", "memory.total", NativeMetricSnapshotMetricKind.MemoryTotal, NativeMetricSnapshotMetricFlags.TotalValue),
         Rule("memory.used", "memory.usage", NativeMetricSnapshotMetricKind.MemoryUsed, NativeMetricSnapshotMetricFlags.UsedValue)]);

    private static NativeMetricSnapshotRuleTemplate Rule(string id, string metric,
        NativeMetricSnapshotMetricKind kind, NativeMetricSnapshotMetricFlags flags) => new(
        id, metric, NativeMetricSnapshotSources.WindowsMemory, 1, 100, 3, (uint)kind,
        (uint)NativeMetricSnapshotScopeKind.Memory, (uint)NativeMetricSnapshotValueKind.Unsigned64,
        1, (uint)(flags | NativeMetricSnapshotMetricFlags.Nonnegative),
        0, ulong.MaxValue, 0, 0, 1, 22);

    private static NativeMetricSnapshotConfiguration Configuration() => new()
    {
        AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotConfiguration>(), Generation = 1,
        MaximumSourceCount = 8, MaximumMetricCount = 32, MaximumRuleCount = 64,
        MaximumRequestedCount = 32, MaximumObservationCount = 64, MaximumGpuAdapterCount = 8,
        MaximumPersistenceSourceCount = 8, MaximumPersistenceRuleCount = 64, MaximumPersistenceGpuCount = 8,
        SourceIndexCapacity = 16, MetricIndexCapacity = 64, RuleIndexCapacity = 128, GpuIndexCapacity = 16,
        MaximumFutureSkewMilliseconds = 30_000, ResidentByteBudget = 4 * 1024 * 1024,
        CatalogContractVersion = NativeMetricSnapshotAbi.CatalogContractVersion,
        ValueContractVersion = NativeMetricSnapshotAbi.ValueContractVersion,
        ObservationContractVersion = NativeMetricSnapshotAbi.ObservationContractVersion,
        InventoryContractVersion = NativeMetricSnapshotAbi.InventoryContractVersion,
        PersistenceContractVersion = NativeMetricSnapshotAbi.PersistenceContractVersion,
        MaximumPlanMetricCount = 32, MaximumSourceModeCount = 8, MaximumSourcePlanCount = 8, MaximumMetricPlanCount = 64,
        GpuLuidIndexCapacity = 16, GpuKeyIndexCapacity = 16
    };
    private static uint Size<T>() where T : unmanaged => checked((uint)Unsafe.SizeOf<T>());
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
