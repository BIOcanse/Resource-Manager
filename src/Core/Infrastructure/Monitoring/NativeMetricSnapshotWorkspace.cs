using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class NativeMetricSnapshotWorkspace : IDisposable
{
    private readonly object gate = new();
    private readonly NativeMetricSnapshotSession session;
    private readonly NativeMetricSnapshotPersistenceStore persistenceStore;
    private NativeMetricSnapshotPersistenceImage? pendingPersistence;
    private NativeMetricSnapshotCatalogPersistenceState? activeCatalog;
    private NativeMetricSnapshotCatalogProjection? activeProjection;
    private ulong operationEpoch;
    private ulong planEpoch;
    private ulong persistedCatalogGeneration;
    private uint persistedPhase;
    private bool disposed;

    internal NativeMetricSnapshotWorkspace(
        CompiledHostManagerMetricSnapshotPlan plan,
        string persistencePath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new ArgumentException(
                "The metric-snapshot plan must be published.",
                nameof(plan));
        }
        if (string.IsNullOrWhiteSpace(persistencePath)
            || !Path.IsPathFullyQualified(persistencePath))
        {
            throw new ArgumentException(
                "The metric-snapshot persistence path must be absolute.",
                nameof(persistencePath));
        }

        Plan = plan;
        PersistencePath = Path.GetFullPath(persistencePath);
        var configuration = CreateConfiguration(plan);
        session = new NativeMetricSnapshotSession(in configuration);
        persistenceStore =
            new NativeMetricSnapshotPersistenceStore(PersistencePath);
        try
        {
            pendingPersistence = persistenceStore.Load(
                plan.HotPublish.ConfigurationGeneration,
                plan.HotPublish.CatalogManifestSha256,
                session.Capacity);
            if (pendingPersistence is not null)
            {
                persistedCatalogGeneration =
                    pendingPersistence.Header.CatalogGeneration;
                persistedPhase = pendingPersistence.Header.Phase;
            }
        }
        catch (Exception exception) when (IsInvalidCacheImage(exception))
        {
            persistenceStore.Discard();
            pendingPersistence = null;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal CompiledHostManagerMetricSnapshotPlan Plan { get; }

    internal string PersistencePath { get; }

    internal NativeMetricSnapshotCapacity Capacity
    {
        get
        {
            ThrowIfDisposed();
            return session.Capacity;
        }
    }

    internal T Execute<T>(Func<NativeMetricSnapshotSession, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (gate)
        {
            ThrowIfDisposed();
            return operation(session);
        }
    }

    internal NativeMetricSnapshotCatalogHandleMap CreateCatalogHandleMap()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return pendingPersistence?.Catalog.HandleMap
                ?? NativeMetricSnapshotCatalogHandleMap.Empty;
        }
    }

    internal NativeMetricSnapshotCatalogProjection ReadCatalog()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return activeProjection
                ?? throw new InvalidOperationException(
                    "The metric-snapshot catalog is not ready.");
        }
    }

    internal NativeMetricSnapshotSnapshotHeader ReplaceCatalog(
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
                    Plan.HotPublish.ConfigurationGeneration,
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
            var matchesPending = PendingCatalogMatches(projection);
            if (matchesPending)
            {
                TryImportPendingPersistence(
                    fingerprint,
                    sourceRows.Length,
                    ruleRows.Length,
                    commandAt);
            }
            else
            {
                pendingPersistence = null;
            }
            activeCatalog = new NativeMetricSnapshotCatalogPersistenceState(
                catalogGeneration,
                projection.NativeRowFingerprint,
                projection.CatalogIdentitySha256,
                Plan.HotPublish.CatalogManifestSha256,
                projection.HandleMap,
                projection.MetricIds);
            activeProjection = projection;
            return QueryHeader();
        }
    }

    internal NativeMetricSnapshotCollectionPlan PlanCollection(
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
            if (metricHandles.Length == 0
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
                    Plan.HotPublish.ConfigurationGeneration,
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

    internal NativeMetricSnapshotSnapshotHeader CompleteSource(
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
                        Plan.HotPublish.ConfigurationGeneration,
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
                    Plan.HotPublish.ConfigurationGeneration,
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

    internal static DateTimeOffset ResolveCompletionCapturedAt(
        bool hasCpuCounter,
        DateTimeOffset observationCapturedAt,
        DateTimeOffset commandAt)
        => hasCpuCounter ? observationCapturedAt : commandAt;

    internal NativeMetricSnapshotCommittedFrame ReadCommitted()
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
                sources.ToImmutableArray(),
                metrics.ToImmutableArray(),
                gpuInventory.ToImmutableArray(),
                rules.ToImmutableArray(),
                projection);
        }
    }

    internal NativeMetricSnapshotSnapshotHeader ReadHeader()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return QueryHeader();
        }
    }

    internal NativeMetricSnapshotPersistenceImage ExportAndPersist(
        DateTimeOffset commandAt)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return ExportAndPersistCore(QueryHeader(), commandAt);
        }
    }

    internal bool PersistRecoveryCheckpointIfAdvanced(
        DateTimeOffset commandAt)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var header = QueryHeader();
            var catalogAdvanced =
                header.CatalogGeneration != persistedCatalogGeneration;
            var firstReadyFrame =
                header.Phase == (uint)NativeMetricSnapshotPhase.Ready
                && persistedPhase
                    != (uint)NativeMetricSnapshotPhase.Ready;
            if (!catalogAdvanced && !firstReadyFrame)
            {
                return false;
            }

            _ = ExportAndPersistCore(header, commandAt);
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            session.Dispose();
        }
    }

    internal static NativeMetricSnapshotConfiguration CreateConfiguration(
        CompiledHostManagerMetricSnapshotPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var capacity = plan.Recreate.Capacity;
        return new NativeMetricSnapshotConfiguration
        {
            AbiVersion = plan.Build.AbiVersion,
            StructSize = checked(
                (uint)Unsafe.SizeOf<NativeMetricSnapshotConfiguration>()),
            Generation = plan.HotPublish.ConfigurationGeneration,
            MaximumSourceCount = capacity.MaximumSourceCount,
            MaximumMetricCount = capacity.MaximumMetricCount,
            MaximumRuleCount = capacity.MaximumRuleCount,
            MaximumRequestedCount = capacity.MaximumRequestedCount,
            MaximumObservationCount = capacity.MaximumObservationCount,
            MaximumGpuAdapterCount = capacity.MaximumGpuAdapterCount,
            MaximumPersistenceSourceCount =
                capacity.MaximumPersistenceSourceCount,
            MaximumPersistenceRuleCount =
                capacity.MaximumPersistenceRuleCount,
            MaximumPersistenceGpuCount =
                capacity.MaximumPersistenceGpuCount,
            SourceIndexCapacity = capacity.SourceIndexCapacity,
            MetricIndexCapacity = capacity.MetricIndexCapacity,
            RuleIndexCapacity = capacity.RuleIndexCapacity,
            GpuIndexCapacity = capacity.GpuIndexCapacity,
            MaximumFutureSkewMilliseconds =
                plan.HotPublish.MaximumFutureSkewMilliseconds,
            ResidentByteBudget = plan.HotPublish.ResidentByteBudget,
            CatalogContractVersion = plan.Recreate.CatalogContractVersion,
            ValueContractVersion = plan.Recreate.ValueContractVersion,
            ObservationContractVersion =
                plan.Recreate.ObservationContractVersion,
            InventoryContractVersion =
                plan.Recreate.InventoryContractVersion,
            PersistenceContractVersion =
                plan.Recreate.PersistenceContractVersion,
            Flags = 0,
            MaximumPlanMetricCount = capacity.MaximumPlanMetricCount,
            MaximumSourceModeCount = capacity.MaximumSourceModeCount,
            MaximumSourcePlanCount = capacity.MaximumSourcePlanCount,
            MaximumMetricPlanCount = capacity.MaximumMetricPlanCount,
            GpuLuidIndexCapacity = capacity.GpuLuidIndexCapacity,
            GpuKeyIndexCapacity = capacity.GpuKeyIndexCapacity,
            ReservedU32 = 0
        };
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private void TryImportPendingPersistence(
        ulong catalogFingerprint,
        int sourceCount,
        int ruleCount,
        DateTimeOffset commandAt)
    {
        var pending = pendingPersistence;
        if (pending is null)
        {
            return;
        }
        if (pending.Header.CatalogFingerprint != catalogFingerprint
            || pending.Sources.Length != sourceCount
            || pending.Rules.Length != ruleCount)
        {
            persistenceStore.Discard();
            pendingPersistence = null;
            return;
        }

        var importedOperationEpoch = Math.Max(
            operationEpoch,
            pending.Header.LastOperationEpoch);
        var importedPlanEpoch = Math.Max(
            planEpoch,
            pending.Header.LastPlanEpoch);
        var input = new NativeMetricSnapshotPersistenceInput
        {
            AbiVersion = NativeMetricSnapshotAbi.Version,
            StructSize = SizeOf<NativeMetricSnapshotPersistenceInput>(),
            ConfigurationGeneration =
                Plan.HotPublish.ConfigurationGeneration,
            CatalogGeneration = pending.Header.CatalogGeneration,
            OperationEpoch = Next(
                ref importedOperationEpoch,
                "operation epoch"),
            CommandAtMilliseconds = Math.Max(
                Timestamp(commandAt),
                pending.Header.LastCommandAtMilliseconds),
            Flags = 0
        };
        var persistenceHeader = pending.Header;
        var status = session.ImportState(
            in input,
            in persistenceHeader,
            pending.Sources,
            pending.Rules,
            pending.GpuInventory);
        if (status is NativeMetricSnapshotStatus.InvalidArgument
            or NativeMetricSnapshotStatus.AbiMismatch
            or NativeMetricSnapshotStatus.StaleFrame)
        {
            persistenceStore.Discard();
            pendingPersistence = null;
            return;
        }
        RequireOk(status, "import state");
        operationEpoch = importedOperationEpoch;
        planEpoch = importedPlanEpoch;
        persistedCatalogGeneration =
            pending.Header.CatalogGeneration;
        persistedPhase = pending.Header.Phase;
        pendingPersistence = null;
    }

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
        persistenceStore.Save(
            Plan.HotPublish.ConfigurationGeneration,
            image);
        operationEpoch = Math.Max(
            operationEpoch,
            persistenceHeader.LastOperationEpoch);
        planEpoch = Math.Max(planEpoch, persistenceHeader.LastPlanEpoch);
        persistedCatalogGeneration =
            persistenceHeader.CatalogGeneration;
        persistedPhase = persistenceHeader.Phase;
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
            return Plan.HotPublish.CatalogGenerationBase;
        }
        if (PendingCatalogMatches(projection))
        {
            return pending.Catalog.CatalogGeneration;
        }

        var next = checked(pending.Catalog.CatalogGeneration + 1);
        return Math.Max(next, Plan.HotPublish.CatalogGenerationBase);
    }

    private bool PendingCatalogMatches(
        NativeMetricSnapshotCatalogProjection projection)
    {
        var pending = pendingPersistence;
        return pending is not null
            && pending.Catalog.NativeRowFingerprint
                == projection.NativeRowFingerprint
            && string.Equals(
                pending.Catalog.CatalogIdentitySha256,
                projection.CatalogIdentitySha256,
                StringComparison.Ordinal)
            && string.Equals(
                pending.Catalog.ManifestSha256,
                Plan.HotPublish.CatalogManifestSha256,
                StringComparison.Ordinal)
            && pending.Catalog.MetricIds.Count == projection.MetricIds.Count
            && pending.Catalog.MetricIds.All(pair =>
                projection.MetricIds.TryGetValue(
                    pair.Key,
                    out var value)
                && string.Equals(
                    pair.Value,
                    value,
                    StringComparison.Ordinal))
            && pending.GpuInventory.All(adapter =>
                projection.GpuScopeBindings.TryGetValue(adapter.AdapterHandle, out var binding)
                && adapter.StableKeyHandle == binding.ScopeHandle
                && adapter.AdapterLuidLow == binding.AdapterLuid
                && adapter.AdapterLuidHigh == 0);
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
        var expectedSources = Plan.HotPublish.SourcePolicies;
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
                || actual.RetentionPolicy != expected.RetentionPolicy
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
                != Plan.HotPublish.ConfigurationGeneration)
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
                Plan.HotPublish.ConfigurationGeneration,
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
