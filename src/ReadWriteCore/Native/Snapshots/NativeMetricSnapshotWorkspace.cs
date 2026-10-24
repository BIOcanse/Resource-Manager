using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class NativeMetricSnapshotWorkspace : IDisposable
{
    private readonly object gate = new();
    private readonly NativeMetricSnapshotSession session;
    private readonly NativeMetricSnapshotPersistenceCallbacks? persistence;
    private NativeMetricSnapshotCatalogPersistenceState? pendingPersistence;
    private NativeMetricSnapshotCatalogPersistenceState? activeCatalog;
    private NativeMetricSnapshotCatalogProjection? activeProjection;
    private ulong operationEpoch;
    private ulong planEpoch;
    private ulong persistedCatalogGeneration;
    private bool disposed;

    public NativeMetricSnapshotWorkspace(
        NativeMetricSnapshotConfiguration configuration,
        NativeMetricSnapshotCatalogConfiguration catalogConfiguration,
        NativeMetricSnapshotPersistenceCallbacks? persistence = null)
        : this(configuration, catalogConfiguration, persistence, NativeMetricSnapshotScratch.CreateOrdinary) { }

    internal NativeMetricSnapshotWorkspace(
        NativeMetricSnapshotConfiguration configuration,
        NativeMetricSnapshotCatalogConfiguration catalogConfiguration,
        NativeMetricSnapshotPersistenceCallbacks? persistence,
        Func<ulong, uint, INativeMetricSnapshotScratch> createScratch)
    {
        ArgumentNullException.ThrowIfNull(catalogConfiguration);
        if (!catalogConfiguration.IsPublished)
            throw new ArgumentException("The canonical metric catalog configuration is incomplete.", nameof(catalogConfiguration));
        Configuration = configuration;
        CatalogConfiguration = catalogConfiguration;
        this.persistence = persistence;
        session = new NativeMetricSnapshotSession(in configuration, createScratch);
        try
        {
            pendingPersistence = persistence?.Load(configuration.Generation,
                catalogConfiguration.CatalogManifestSha256, session.Capacity);
            if (pendingPersistence is not null)
            {
                persistedCatalogGeneration = pendingPersistence.CatalogGeneration;
            }
        }
        catch (Exception exception) when (IsInvalidCacheImage(exception))
        {
            try
            {
                var warning = $"Discarding invalid metric-snapshot catalog persistence: {exception.Message} Sampling values will start empty.";
                if (persistence?.Warning is { } reportWarning) reportWarning(warning);
                else Trace.TraceWarning(warning);
                persistence?.Discard();
                pendingPersistence = null;
            }
            catch { session.Dispose(); throw; }
        }
        catch { session.Dispose(); throw; }
    }

    public NativeMetricSnapshotConfiguration Configuration { get; }
    public NativeMetricSnapshotCatalogConfiguration CatalogConfiguration { get; }

    public NativeMetricSnapshotCapacity Capacity
    {
        get
        {
            ThrowIfDisposed();
            return session.Capacity;
        }
    }

    public T Execute<T>(Func<NativeMetricSnapshotSession, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (gate)
        {
            ThrowIfDisposed();
            return operation(session);
        }
    }

    public NativeMetricSnapshotCatalogHandleMap CreateCatalogHandleMap()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return pendingPersistence?.HandleMap
                ?? NativeMetricSnapshotCatalogHandleMap.Empty;
        }
    }

    public NativeMetricSnapshotCatalogProjection ReadCatalog()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return activeProjection
                ?? throw new InvalidOperationException(
                    "The metric-snapshot catalog is not ready.");
        }
    }

    public NativeMetricSnapshotSnapshotHeader ReplaceCatalog(
        NativeMetricSnapshotCatalogProjection projection,
        DateTimeOffset commandAt)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var sourceRows = projection.Sources.ToArray();
        var ruleRows = projection.Rules.ToArray();
        lock (gate)
        {
            ThrowIfDisposed();
            var catalogGeneration =
                ResolveCatalogGeneration(projection);
            ValidateCatalog(sourceRows, ruleRows, catalogGeneration);
            var fingerprint =
                NativeMetricSnapshotSession.CalculateCatalogFingerprint(
                    sourceRows,
                    ruleRows);
            var input = new NativeMetricSnapshotCatalogReplaceInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version,
                StructSize = SizeOf<
                    NativeMetricSnapshotCatalogReplaceInput>(),
                ConfigurationGeneration =
                    Configuration.Generation,
                CatalogGeneration = catalogGeneration,
                OperationEpoch = Next(ref operationEpoch, "operation epoch"),
                CommandAtMilliseconds = Timestamp(commandAt),
                SourceCount = checked((uint)sourceRows.Length),
                RuleCount = checked((uint)ruleRows.Length),
                MetricCount = checked(
                    (uint)ruleRows.Select(static rule => rule.MetricHandle)
                        .Distinct()
                        .Count()),
                GpuInventorySourceCount = checked(
                    (uint)sourceRows.Count(static source =>
                        source.SourceRole
                            == (uint)NativeMetricSnapshotSourceRole
                                .GpuInventory)),
                SourceIndexCapacity = session.Capacity.SourceIndexCapacity,
                MetricIndexCapacity = session.Capacity.MetricIndexCapacity,
                RuleIndexCapacity = session.Capacity.RuleIndexCapacity,
                Flags = 0,
                SemanticFingerprint = fingerprint
            };
            RequireOk(
                session.ReplaceCatalog(in input, sourceRows, ruleRows),
                "replace catalog");
            pendingPersistence = null;
            activeCatalog = new NativeMetricSnapshotCatalogPersistenceState(
                catalogGeneration,
                projection.NativeRowFingerprint,
                projection.CatalogIdentitySha256,
                CatalogConfiguration.CatalogManifestSha256,
                projection.HandleMap,
                projection.MetricIds);
            activeProjection = projection;
            return QueryHeader();
        }
    }

    public void SynchronizeCounterDemand(IReadOnlyCollection<ulong> activeHandles, DateTimeOffset commandAt)
    {
        ArgumentNullException.ThrowIfNull(activeHandles);
        lock (gate)
        {
            ThrowIfDisposed();
            var input = new NativeMetricSnapshotControlInput {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = SizeOf<NativeMetricSnapshotControlInput>(),
                ConfigurationGeneration = Configuration.Generation,
                OperationEpoch = Next(ref operationEpoch, "operation epoch"), CommandAtMilliseconds = Timestamp(commandAt)
            };
            var metrics = activeHandles.Distinct().Order().Select(static handle => new NativeMetricSnapshotPlanMetricInput {
                StructSize = SizeOf<NativeMetricSnapshotPlanMetricInput>(), MetricHandle = handle
            }).ToArray();
            RequireOk(session.SynchronizeCounterDemand(in input, metrics), "synchronize counter demand");
        }
    }

    public NativeMetricSnapshotCollectionPlan PlanCollection(
        IReadOnlyCollection<ulong> requestedMetricHandles,
        IReadOnlyCollection<NativeMetricSnapshotSourceRuntimeMode> sourceModes,
        bool includeGpuInventory,
        DateTimeOffset commandAt)
    {
        ArgumentNullException.ThrowIfNull(requestedMetricHandles);
        ArgumentNullException.ThrowIfNull(sourceModes);
        lock (gate)
        {
            ThrowIfDisposed();
            var projection = activeProjection
                ?? throw new InvalidOperationException(
                    "The metric-snapshot catalog is not ready.");
            var header = QueryHeader();
            var metricHandles = requestedMetricHandles
                .Order()
                .ToArray();
            if (metricHandles.Length == 0 && !includeGpuInventory
                || metricHandles.Length
                    > session.Capacity.PlanMetricCapacity
                || metricHandles.Distinct().Count()
                    != metricHandles.Length
                || metricHandles.Any(handle =>
                    !projection.MetricIds.ContainsKey(handle)))
            {
                throw new ArgumentException(
                    "The requested metric handles are invalid.",
                    nameof(requestedMetricHandles));
            }
            var modes = sourceModes
                .OrderBy(static mode => mode.SourceHandle)
                .ToArray();
            if (modes.Length != projection.Sources.Length
                || modes.Select(static mode => mode.SourceHandle)
                    .Distinct()
                    .Count() != modes.Length
                || modes.Any(mode =>
                    mode.CapabilityGeneration == 0
                    || !projection.SourceIds.ContainsKey(
                        mode.SourceHandle)))
            {
                throw new ArgumentException(
                    "The source modes do not cover the active catalog exactly.",
                    nameof(sourceModes));
            }
            var requested = metricHandles
                .Select(static handle =>
                    new NativeMetricSnapshotPlanMetricInput
                    {
                        StructSize =
                            SizeOf<NativeMetricSnapshotPlanMetricInput>(),
                        Flags = 0,
                        MetricHandle = handle
                    })
                .ToArray();
            var nativeModes = modes
                .Select(static mode =>
                    new NativeMetricSnapshotSourceModeInput
                    {
                        StructSize =
                            SizeOf<NativeMetricSnapshotSourceModeInput>(),
                        Flags = 0,
                        SourceHandle = mode.SourceHandle,
                        CapabilityGeneration =
                            mode.CapabilityGeneration,
                        ZoneMode = (uint)mode.ZoneMode,
                        Availability = (uint)mode.Availability
                    })
                .ToArray();
            var input = new NativeMetricSnapshotPlanInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version,
                StructSize = SizeOf<NativeMetricSnapshotPlanInput>(),
                ConfigurationGeneration =
                    Configuration.Generation,
                CatalogGeneration = header.CatalogGeneration,
                OperationEpoch = Next(ref operationEpoch, "operation epoch"),
                PlanEpoch = Next(ref planEpoch, "plan epoch"),
                CommandAtMilliseconds = Timestamp(commandAt),
                RequestedMetricCount = checked((uint)requested.Length),
                SourceModeCount = checked((uint)nativeModes.Length),
                SourcePlanCapacity = session.Capacity.SourcePlanCapacity,
                MetricPlanCapacity = session.Capacity.MetricPlanCapacity,
                Flags = includeGpuInventory
                    ? (ulong)NativeMetricSnapshotPlanFlags
                        .IncludeGpuInventory
                    : 0UL
            };
            var sourcePlans =
                new NativeMetricSnapshotSourcePlanOutput[
                    checked((int)session.Capacity.SourcePlanCapacity)];
            var metricPlans =
                new NativeMetricSnapshotMetricPlanOutput[
                    checked((int)session.Capacity.MetricPlanCapacity)];
            RequireOk(
                session.Plan(
                    in input,
                    requested,
                    nativeModes,
                    out var output,
                    sourcePlans,
                    metricPlans),
                "plan collection");
            ValidatePlanOutput(in input, in output);
            return new NativeMetricSnapshotCollectionPlan(
                output,
                sourcePlans.AsSpan(
                        0,
                        checked((int)output.SourcePlanCount))
                    .ToArray()
                    .ToImmutableArray(),
                metricPlans.AsSpan(
                        0,
                        checked((int)output.MetricPlanCount))
                    .ToArray()
                    .ToImmutableArray(),
                projection);
        }
    }

    public NativeMetricSnapshotSnapshotHeader CompleteSource(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourceCompletion completion,
        DateTimeOffset commandAt)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(completion);
        lock (gate)
        {
            ThrowIfDisposed();
            var projection = activeProjection
                ?? throw new InvalidOperationException(
                    "The metric-snapshot catalog is not ready.");
            if (!string.Equals(
                    plan.Catalog.CatalogIdentitySha256,
                    projection.CatalogIdentitySha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The collection plan belongs to a retired metric catalog.");
            }
            var sourcePlan = plan.Sources.SingleOrDefault(
                source => source.SourceHandle == completion.SourceHandle);
            if (sourcePlan.SourceHandle == 0
                || sourcePlan.PlanEpoch != plan.Header.PlanEpoch)
            {
                throw new ArgumentException(
                    "The completion source was not selected by the collection plan.",
                    nameof(completion));
            }
            var metricPlans = plan.Metrics
                .Where(metric =>
                    metric.SourceHandle == completion.SourceHandle
                    && (metric.Flags
                        & (uint)NativeMetricSnapshotMetricPlanFlags
                            .Selected) != 0)
                .OrderBy(static metric => metric.RuleHandle)
                .ToArray();
            if (metricPlans.Length != sourcePlan.RequestedRuleCount)
            {
                throw new InvalidOperationException(
                    "The native collection plan has inconsistent source and metric counts.");
            }
            var requested = metricPlans
                .Select(static metric =>
                    new NativeMetricSnapshotRequestedMetricInput
                    {
                        StructSize = SizeOf<
                            NativeMetricSnapshotRequestedMetricInput>(),
                        Flags = 0,
                        RuleHandle = metric.RuleHandle
                })
                .ToArray();
            var operation = Next(ref operationEpoch, "operation epoch");
            var completionCapturedAt = ResolveCompletionCapturedAt(
                completion.CpuCounter.HasValue,
                completion.CapturedAt,
                commandAt);
            var capturedAt = Timestamp(completionCapturedAt);
            var completionHeader =
                new NativeMetricSnapshotCompletionHeader
                {
                    AbiVersion = NativeMetricSnapshotAbi.Version,
                    StructSize = SizeOf<
                        NativeMetricSnapshotCompletionHeader>(),
                    ConfigurationGeneration =
                        Configuration.Generation,
                    CatalogGeneration =
                        plan.Header.CatalogGeneration,
                    OperationEpoch = operation,
                    PlanEpoch = plan.Header.PlanEpoch,
                    SourceHandle = completion.SourceHandle,
                    SourceIncarnation =
                        completion.SourceIncarnation,
                    SourceObservationSequence =
                        completion.SourceObservationSequence,
                    CapabilityGeneration =
                        sourcePlan.CapabilityGeneration,
                    PlanTokenFingerprint =
                        sourcePlan.PlanTokenFingerprint,
                    CommandAtMilliseconds = Timestamp(commandAt),
                    CapturedAtMilliseconds = capturedAt,
                    RequestedRuleCount = checked(
                        (uint)requested.Length),
                    ObservationCount = checked(
                        (uint)completion.Observations.Length),
                    GpuInventoryCount = checked(
                        (uint)completion.GpuInventory.Length),
                    ExpectedGpuInventoryCount =
                        completion.ExpectedGpuInventoryCount,
                    CpuCounterCount =
                        completion.CpuCounter.HasValue ? 1U : 0U,
                    OverflowCount = completion.OverflowCount,
                    Status = (uint)completion.Status,
                    Flags = 0
                };
            var finalize = new NativeMetricSnapshotFinalizeInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version,
                StructSize = SizeOf<NativeMetricSnapshotFinalizeInput>(),
                ConfigurationGeneration =
                    Configuration.Generation,
                CatalogGeneration = plan.Header.CatalogGeneration,
                OperationEpoch = operation,
                PlanEpoch = plan.Header.PlanEpoch,
                SourceHandle = completion.SourceHandle,
                CommandAtMilliseconds = Timestamp(commandAt),
                Flags = 0
            };
            RequireOk(
                session.BeginCompletion(in completionHeader),
                "begin source completion");
            try
            {
                RequireOk(
                    session.SubmitRequested(requested),
                    "submit requested rules");
                if (!completion.Observations.IsDefaultOrEmpty)
                {
                    RequireOk(
                        session.SubmitObservations(
                            completion.Observations.AsSpan()),
                        "submit observations");
                }
                if (completion.CpuCounter is { } cpuCounter)
                {
                    RequireOk(
                        session.SubmitCpuCounter(in cpuCounter),
                        "submit CPU counter");
                }
                if (!completion.GpuInventory.IsDefaultOrEmpty)
                {
                    RequireOk(
                        session.SubmitGpuInventory(
                            completion.GpuInventory.AsSpan()),
                        "submit GPU inventory");
                }
                RequireOk(
                    session.FinalizeCompletion(in finalize),
                    "finalize source completion");
                return QueryHeader();
            }
            catch
            {
                RequireOk(
                    session.AbortCompletion(in finalize),
                    "abort source completion");
                throw;
            }
        }
    }

    public static DateTimeOffset ResolveCompletionCapturedAt(
        bool hasCpuCounter,
        DateTimeOffset observationCapturedAt,
        DateTimeOffset commandAt)
        => hasCpuCounter ? observationCapturedAt : commandAt;

    public NativeMetricSnapshotCommittedFrame ReadCommitted()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var projection = activeProjection
                ?? throw new InvalidOperationException(
                    "The metric-snapshot catalog is not ready.");
            var header = QueryHeader();
            var read = CreateReadInput(header);
            var sources = new NativeMetricSnapshotSourceOutput[
                checked((int)header.SourceCount)];
            var metrics = new NativeMetricSnapshotMetricOutput[
                checked((int)header.MetricCount)];
            var gpuInventory =
                new NativeMetricSnapshotGpuInventoryOutput[
                    checked((int)header.GpuAdapterCount)];
            var rules = new NativeMetricSnapshotRuleStateOutput[
                checked((int)header.RuleCount)];
            RequireOk(
                session.Read(
                    in read,
                    sources,
                    metrics,
                    gpuInventory,
                    rules),
                "read committed snapshot");
            return new NativeMetricSnapshotCommittedFrame(
                header,
                ImmutableCollectionsMarshal.AsImmutableArray(sources),
                ImmutableCollectionsMarshal.AsImmutableArray(metrics),
                ImmutableCollectionsMarshal.AsImmutableArray(gpuInventory),
                ImmutableCollectionsMarshal.AsImmutableArray(rules),
                projection);
        }
    }

    internal (NativeMetricSnapshotSnapshotHeader Header,
        ImmutableArray<NativeMetricSnapshotSourceOutput> Sources) ReadSources()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            _ = activeProjection
                ?? throw new InvalidOperationException(
                    "The metric-snapshot catalog is not ready.");
            var header = QueryHeader();
            var read = CreateReadInput(header);
            var sources = new NativeMetricSnapshotSourceOutput[
                checked((int)header.SourceCount)];
            RequireOk(
                session.Read(
                    in read,
                    sources,
                    Span<NativeMetricSnapshotMetricOutput>.Empty,
                    Span<NativeMetricSnapshotGpuInventoryOutput>.Empty,
                    Span<NativeMetricSnapshotRuleStateOutput>.Empty),
                "read committed metric sources");
            return (header,
                ImmutableCollectionsMarshal.AsImmutableArray(sources));
        }
    }

    public NativeMetricSnapshotSnapshotHeader ReadHeader()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return QueryHeader();
        }
    }

    public NativeMetricSnapshotPersistenceImage ExportAndPersist(
        DateTimeOffset commandAt)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return ExportAndPersistCore(QueryHeader(), commandAt);
        }
    }

    public ulong PersistedCatalogGeneration { get { lock (gate) { ThrowIfDisposed(); return persistedCatalogGeneration; } } }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            // Repeated Dispose must retry a retained endUse/retirement failure.
            session.Dispose();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private NativeMetricSnapshotPersistenceImage ExportAndPersistCore(
        NativeMetricSnapshotSnapshotHeader header,
        DateTimeOffset commandAt)
    {
        if (header.CatalogGeneration == 0)
        {
            throw new InvalidOperationException(
                "The metric-snapshot catalog is not ready.");
        }
        var catalog = activeCatalog
            ?? throw new InvalidOperationException(
                "The metric-snapshot catalog identity is not ready.");
        var read = CreateReadInput(header);
        var sources =
            new NativeMetricSnapshotSourcePersistenceOutput[
                checked((int)header.SourceCount)];
        var rules = new NativeMetricSnapshotRuleStateOutput[
                checked((int)header.RuleCount)];
        var gpuInventory =
            new NativeMetricSnapshotGpuInventoryOutput[
                checked((int)header.GpuAdapterCount)];
        RequireOk(
            session.ExportState(
                in read,
                out var persistenceHeader,
                sources,
                rules,
                gpuInventory),
            "export state");
        var image = new NativeMetricSnapshotPersistenceImage(
            catalog,
            persistenceHeader,
            sources,
            rules,
            gpuInventory);
        persistence?.Save(
            Configuration.Generation,
            image.Catalog);
        persistedCatalogGeneration =
            catalog.CatalogGeneration;
        _ = commandAt;
        return image;
    }

    private static bool IsInvalidCacheImage(Exception exception)
        => exception is InvalidDataException
            or EndOfStreamException
            or DecoderFallbackException
            or OverflowException;

    private ulong ResolveCatalogGeneration(
        NativeMetricSnapshotCatalogProjection projection)
    {
        var pending = pendingPersistence;
        if (pending is null)
        {
            return CatalogConfiguration.CatalogGenerationBase;
        }
        if (PendingCatalogMatches(projection))
        {
            return pending.CatalogGeneration;
        }

        var next = checked(pending.CatalogGeneration + 1);
        return Math.Max(next, CatalogConfiguration.CatalogGenerationBase);
    }

    private bool PendingCatalogMatches(
        NativeMetricSnapshotCatalogProjection projection)
    {
        var pending = pendingPersistence;
        return pending is not null
            && pending.NativeRowFingerprint
                == projection.NativeRowFingerprint
            && string.Equals(
                pending.CatalogIdentitySha256,
                projection.CatalogIdentitySha256,
                StringComparison.Ordinal)
            && string.Equals(
                pending.ManifestSha256,
                CatalogConfiguration.CatalogManifestSha256,
                StringComparison.Ordinal)
            && pending.MetricIds.Count == projection.MetricIds.Count
            && pending.MetricIds.All(pair =>
                projection.MetricIds.TryGetValue(
                    pair.Key,
                    out var value)
                && string.Equals(
                    pair.Value,
                    value,
                    StringComparison.Ordinal));
    }

    private void ValidateCatalog(
        NativeMetricSnapshotSourcePolicyInput[] sources,
        NativeMetricSnapshotMetricDefinitionInput[] rules,
        ulong catalogGeneration)
    {
        if (catalogGeneration == 0
            || sources.Length == 0
            || rules.Length == 0
            || sources.Length > session.Capacity.SourceCapacity
            || rules.Length > session.Capacity.RuleCapacity
            || rules.Select(static rule => rule.MetricHandle)
                    .Distinct()
                    .Count()
                > session.Capacity.MetricCapacity)
        {
            throw new InvalidOperationException(
                "The metric-snapshot catalog exceeds its compiled capacity.");
        }
        var expectedSources = CatalogConfiguration.SourcePolicies;
        if (sources.Length != expectedSources.Length)
        {
            throw new InvalidOperationException(
                "The metric-snapshot catalog does not contain every compiled source policy.");
        }
        for (var index = 0; index < sources.Length; index++)
        {
            var actual = sources[index];
            var expected = expectedSources[index];
            var expectedFlags = expected.Required
                ? (uint)NativeMetricSnapshotSourceFlags.Required
                : 0U;
            if (actual.StructSize
                    != SizeOf<NativeMetricSnapshotSourcePolicyInput>()
                || actual.Flags != expectedFlags
                || actual.SourceHandle != expected.SourceHandle
                || actual.SourceRole != expected.SourceRole
                || actual.Priority != expected.Priority
                || actual.CapabilityMask != expected.CapabilityMask
                || actual.SemanticFingerprint != expected.SemanticFingerprint)
            {
                throw new InvalidOperationException(
                    "The metric-snapshot source catalog differs from the compiled Host Manager policy.");
            }
        }
    }

    private NativeMetricSnapshotSnapshotHeader QueryHeader()
    {
        RequireOk(session.QueryHeader(out var header), "query header");
        if (header.AbiVersion != NativeMetricSnapshotAbi.Version
            || header.StructSize
                != SizeOf<NativeMetricSnapshotSnapshotHeader>()
            || header.ConfigurationGeneration
                != Configuration.Generation)
        {
            throw new InvalidOperationException(
                "The native metric-snapshot header is non-canonical.");
        }
        return header;
    }

    private void ValidatePlanOutput(
        in NativeMetricSnapshotPlanInput input,
        in NativeMetricSnapshotPlanOutput output)
    {
        if (output.AbiVersion != NativeMetricSnapshotAbi.Version
            || output.StructSize
                != SizeOf<NativeMetricSnapshotPlanOutput>()
            || output.ConfigurationGeneration
                != input.ConfigurationGeneration
            || output.CatalogGeneration != input.CatalogGeneration
            || output.OperationEpoch != input.OperationEpoch
            || output.PlanEpoch != input.PlanEpoch
            || output.SourcePlanCount
                > session.Capacity.SourcePlanCapacity
            || output.MetricPlanCount
                > session.Capacity.MetricPlanCapacity
            || output.SemanticFingerprint == 0)
        {
            throw new InvalidOperationException(
                "The native metric-snapshot plan output is non-canonical.");
        }
    }

    private NativeMetricSnapshotReadInput CreateReadInput(
        NativeMetricSnapshotSnapshotHeader header)
        => new()
        {
            AbiVersion = NativeMetricSnapshotAbi.Version,
            StructSize = SizeOf<NativeMetricSnapshotReadInput>(),
            ConfigurationGeneration =
                Configuration.Generation,
            CatalogGeneration = header.CatalogGeneration,
            CatalogFingerprint = header.CatalogFingerprint,
            StateRevision = header.StateRevision,
            CommittedGeneration = header.CommittedGeneration,
            Flags = 0
        };

    private static void RequireOk(
        NativeMetricSnapshotStatus status,
        string operation)
    {
        if (status != NativeMetricSnapshotStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native metric-snapshot {operation} failed with {status}.");
        }
    }

    private static ulong Timestamp(DateTimeOffset value)
        => checked((ulong)value.ToUnixTimeMilliseconds());

    private static uint SizeOf<T>()
        where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static ulong Next(ref ulong value, string name)
    {
        value = checked(value + 1);
        return value != 0
            ? value
            : throw new InvalidOperationException($"{name} exhausted.");
    }
}
