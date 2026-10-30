using ResourceManager.App.Infrastructure.Adaptation;
using System.Collections.Immutable;
using System.Collections.Frozen;
using System.Globalization;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed unsafe class HostManagerComputeScoringWorkspace : IDisposable
{
    private readonly NativeComputeScoringSession cpuSession;
    private readonly NativeComputeScoringSession gpuSession;
    private readonly FrozenDictionary<string, uint> cpuCoreIndexes;
    private readonly NativeComputeScoringCapacity capacity;
    private const string WorkArea = "compute-scoring";
    private ComputeScoringScratchLayout layout = null!;
    private readonly Func<ulong, IComputeScratch> createScratch;
    private readonly double welfareBaseScore;
    private bool disposed;

    internal HostManagerComputeScoringWorkspace(
        in NativeComputeScoringConfiguration configuration,
        CompiledCpuScoringPlan cpuScoring,
        Func<ulong, IComputeScratch> createScratch)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        this.createScratch = createScratch;
        CpuScoring = cpuScoring;
        welfareBaseScore = configuration.WelfareBaseScore;
        var weights = cpuScoring.CoreWeights.Cores.Select(static core => core.ReferenceWeight).ToArray();
        cpuCoreIndexes = cpuScoring.CoreWeights.Cores.Select((core, index) => (core.PhysicalCoreId, Index: checked((uint)index)))
            .ToFrozenDictionary(static core => core.PhysicalCoreId, static core => core.Index, StringComparer.OrdinalIgnoreCase);
        cpuSession = new NativeComputeScoringSession(in configuration, weights);
        try
        {
            gpuSession = new NativeComputeScoringSession(in configuration, weights);
        }
        catch
        {
            cpuSession.Dispose();
            throw;
        }
        try
        {
            capacity = cpuSession.Capacity;
            var gpuCapacity = gpuSession.Capacity;
            if (capacity.ScratchByteCount != gpuCapacity.ScratchByteCount ||
                capacity.ScratchAlignment != gpuCapacity.ScratchAlignment)
                throw new InvalidOperationException("CPU and GPU scoring sessions require different scratch layouts.");
        }
        catch
        {
            try { cpuSession.Dispose(); }
            finally { gpuSession.Dispose(); }
            throw;
        }
    }

    internal ulong ConfigurationGeneration =>
        cpuSession.Capacity.ConfigurationGeneration;

    internal CompiledCpuScoringPlan CpuScoring { get; }

    internal HostManagerComputeScoringCycleResult? Score(
        ulong roundNumber,
        SchedulingProcessFactSnapshot processFacts,
        SchedulingGpuInventorySnapshot gpuInventory,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact> runtimeFacts,
        CpuCoreResidencySnapshot? cpuResidency,
        HostManagerWelfareCapacityInput? welfareCapacity = null,
        bool scoreCpu = true,
        bool scoreGpu = true,
        ICollection<SchedulingFactExclusion>? exclusions = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(processFacts);
        ArgumentNullException.ThrowIfNull(gpuInventory);
        ArgumentNullException.ThrowIfNull(runtimeFacts);
        ArgumentOutOfRangeException.ThrowIfZero(roundNumber);
        if (!processFacts.IsInventoryCurrent())
        {
            return null;
        }
        processFacts = processFacts.ProjectValidFacts(gpuInventory);
        foreach (var exclusion in processFacts.FactExclusions)
        {
            if (exclusions is not null && !exclusions.Contains(exclusion)) exclusions.Add(exclusion);
        }
        var selected = new List<SchedulingProcessFact>();
        foreach (var process in processFacts.Processes)
        {
            if (!double.IsFinite(process.BaseScore) || process.BaseScore < 0
                || string.IsNullOrWhiteSpace(process.SoftwareId) || string.IsNullOrWhiteSpace(process.ProcessName)) continue;
            if (runtimeFacts.ContainsKey(new(process.ProcessId, process.ProcessStartKey)))
            {
                selected.Add(process);
            }
            else
            {
                exclusions?.Add(new SchedulingFactExclusion(process.ProcessId, process.ProcessStartKey,
                    HostManagerTargetIdentity.CreateProcessTargetId(process.ProcessId, process.ProcessStartKey),
                    SchedulingFactCode.RuntimeState, SchedulingFactExclusionReason.ComputeRuntimeStateMissing, null));
            }
        }
        processFacts = Narrow(processFacts, selected);
        runtimeFacts = ProjectRuntimeFacts(processFacts, runtimeFacts);
        var eligibleProcessCount = checked((uint)runtimeFacts.Values.Count(static runtime =>
            runtime.RuntimeState != NativeComputeScoringRuntimeState.NotRunning));
        var welfare = welfareCapacity
            ?? HostManagerWelfareCapacityInput.Unavailable(eligibleProcessCount);
        if (!welfare.IsValid)
        {
            throw new InvalidDataException("The welfare-capacity input is invalid.");
        }
        welfare = welfare with { EligibleProcessCount = eligibleProcessCount };

        var cpuProjection = scoreCpu ? PrepareCpuDomain(processFacts, cpuResidency, exclusions) : null;
        var gpuFacts = scoreGpu ? ProjectGpuDomain(processFacts, gpuInventory) : null;
        if (cpuProjection is null && gpuFacts is null) return null;
        var next = CreateLayout(cpuProjection, gpuFacts, gpuInventory, out var rows);
        // Held for this round only: at the scheduler's 5 s cadence one create and close per
        // round costs less than keeping the largest round's region between rounds.
        using var scratch = CreateRoundScratch(next.Length, rows);
        layout = next;
        using var lease = scratch.Acquire();
        using var view = new ComputeScratchView(lease);
        if (cpuProjection is not null) FillCpuDomain(view, cpuProjection);
        var result = ScoreCore(view, roundNumber, cpuProjection, gpuFacts, gpuInventory,
            runtimeFacts, welfare);
        lease.Complete();
        return result;
    }

    private HostManagerComputeScoringCycleResult? ScoreCore(
        ComputeScratchView scratchView,
        ulong roundNumber,
        CpuProjection? cpuProjection,
        SchedulingProcessFactSnapshot? gpuFacts,
        SchedulingGpuInventorySnapshot gpuInventory,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact> runtimeFacts,
        HostManagerWelfareCapacityInput welfare)
    {
        var cpuFacts = cpuProjection?.Facts;
        if (cpuFacts is null && gpuFacts is null)
        {
            return null;
        }

        var cpuResult = cpuFacts is null
            ? null
            : ScoreEnvelope(
                scratchView,
                cpuSession,
                roundNumber,
                cpuFacts,
                gpuInventory,
                ProjectRuntimeFacts(cpuFacts, runtimeFacts),
                cpuDomain: true,
                welfare);
        var gpuResult = gpuFacts is null
            ? null
            : ScoreEnvelope(
                scratchView,
                gpuSession,
                roundNumber,
                gpuFacts,
                gpuInventory,
                ProjectRuntimeFacts(gpuFacts, runtimeFacts),
                cpuDomain: false,
                welfare);
        if (cpuResult is null && gpuResult is null) return null;
        var welfareSnapshot = cpuResult is not null
            ? cpuResult.Welfare
            : gpuResult!.Welfare;
        if (cpuResult is not null
            && gpuResult is not null
            && cpuResult.Welfare != gpuResult.Welfare)
        {
            throw new InvalidOperationException(
                "The CPU and GPU scoring domains produced different welfare snapshots.");
        }
        return new HostManagerComputeScoringCycleResult(
            roundNumber,
            cpuResult?.Cpu,
            gpuResult?.Gpu)
        {
            Welfare = welfareSnapshot,
            Memory = cpuResult?.Memory,
            CpuOccupancySource = cpuResult is null ? null : cpuProjection?.Source
        };
    }

    private HostManagerComputeScoringCycleResult? ScoreEnvelope(
        ComputeScratchView scratchView,
        NativeComputeScoringSession session,
        ulong roundNumber,
        SchedulingProcessFactSnapshot processFacts,
        SchedulingGpuInventorySnapshot gpuInventory,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact> runtimeFacts,
        bool cpuDomain,
        HostManagerWelfareCapacityInput welfare)
    {
        var processes = scratchView.Column<NativeComputeScoringProcessInput>(layout.Processes, layout.ProcessCapacity);
        var gpus = scratchView.Column<NativeComputeScoringGpuInput>(layout.Gpus, layout.GpuCapacity);
        var weightedCpuUse = scratchView.Column<double>(layout.WeightedCpuUse, layout.ProcessCapacity);
        var outputs = scratchView.Column<NativeComputeScoringOutput>(layout.Outputs, layout.OutputCapacity);
        var adapterOrder = scratchView.Column<int>(layout.AdapterOrder, layout.AdapterCapacity);
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(processFacts);
        ArgumentNullException.ThrowIfNull(gpuInventory);
        ArgumentNullException.ThrowIfNull(runtimeFacts);
        ArgumentOutOfRangeException.ThrowIfZero(roundNumber);

        if (!processFacts.IsInventoryCurrent())
        {
            return null;
        }

        var cpuComplete = cpuDomain;
        var gpuComplete = !cpuDomain && processFacts.IsGpuUsageCurrentComplete(gpuInventory);
        if (!cpuComplete && !gpuComplete)
        {
            return null;
        }
        if (runtimeFacts.Count != processFacts.Processes.Count)
        {
            throw new InvalidDataException(
                "The compute scoring runtime-state set does not match the exact process envelope.");
        }

        var processCount = processFacts.Processes.Count;
        if (processCount > processes.Length)
        {
            throw new InvalidDataException("The compute scoring process capacity was exceeded.");
        }

        FillAdapterOrder(gpuInventory, gpuComplete, adapterOrder);
        var gpuRowCount = gpuComplete
            ? processFacts.Processes.Sum(static process =>
                process.Gpus.Count(static gpu => gpu.ValidMetricMask.HasFlag(
                    SchedulingProcessMetricMask.GpuUsage)))
            : 0;
        if (gpuRowCount > gpus.Length)
        {
            throw new InvalidDataException("The compute scoring GPU row capacity was exceeded.");
        }

        var processFlags = NativeComputeScoringProcessFlags.None;
        var processValidity = NativeComputeScoringProcessValidity.BaseRequired;
        if (cpuComplete)
        {
            processFlags |= NativeComputeScoringProcessFlags.CpuMetricsComplete;
            processValidity |= NativeComputeScoringProcessValidity.CpuPolicyMultiplier |
                NativeComputeScoringProcessValidity.CpuOccupancy;
        }
        if (gpuComplete)
        {
            processFlags |= NativeComputeScoringProcessFlags.GpuMetricsComplete;
        }

        var gpuCursor = 0;
        for (var processIndex = 0; processIndex < processCount; processIndex++)
        {
            var process = processFacts.Processes[processIndex];
            var identity = new HostManagerComputeProcessIdentity(
                process.ProcessId,
                process.ProcessStartKey);
            if (!runtimeFacts.TryGetValue(identity, out var runtime))
            {
                throw new InvalidDataException(
                    $"The compute scoring envelope is missing runtime state for process {process.ProcessId}/{process.ProcessStartKey}.");
            }
            ValidateRuntimeFact(runtime);

            var targetId = HostManagerTargetIdentity.CreateProcessTargetId(
                process.ProcessId,
                process.ProcessStartKey);
            var targetKey = NativeStableIdentity.CreateCaseInsensitiveKey(targetId);
            var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(process.SoftwareId);
            processes[processIndex] = new NativeComputeScoringProcessInput
            {
                StructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringProcessInput>(),
                Flags = runtime.RuntimeState == NativeComputeScoringRuntimeState.NotRunning
                    ? processFlags
                    : processFlags | NativeComputeScoringProcessFlags.Running,
                ValidMask = processValidity,
                TargetKey = targetKey,
                SoftwareKey = softwareKey,
                ProcessStartKey = process.ProcessStartKey,
                BaseImportance = process.BaseScore,
                CpuPolicyMultiplier = cpuComplete
                    ? runtime.CpuPolicyMultiplier
                    : 0,
                WeightedCpuUsePercent = cpuComplete
                    ? weightedCpuUse[processIndex]
                    : 0,
                SourceIndex = checked((uint)processIndex),
                ProcessId = checked((uint)process.ProcessId),
                RuntimeState = runtime.RuntimeState
            };

            if (!gpuComplete)
            {
                continue;
            }

            for (var adapterCursor = 0;
                 adapterCursor < gpuInventory.Adapters.Count;
                 adapterCursor++)
            {
                var adapter = gpuInventory.Adapters[adapterOrder[adapterCursor]];
                var gpuFact = FindGpuFact(process, adapter.AdapterKey);
                if (gpuFact is null)
                {
                    continue;
                }
                if (gpuFact.GpuIndex != adapter.Index
                    || !gpuFact.ValidMetricMask.HasFlag(
                        SchedulingProcessMetricMask.GpuUsage))
                {
                    throw new InvalidDataException(
                        $"The compute scoring GPU row is inconsistent for process {process.ProcessId}/{process.ProcessStartKey} and adapter {adapter.AdapterKey}.");
                }

                gpus[gpuCursor] = new NativeComputeScoringGpuInput
                {
                    StructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringGpuInput>(),
                    ValidMask = NativeComputeScoringGpuValidity.Required,
                    TargetKey = targetKey,
                    SoftwareKey = softwareKey,
                    ProcessStartKey = process.ProcessStartKey,
                    AdapterKey = adapter.AdapterKey,
                    GpuPolicyMultiplier = runtime.GpuPolicyMultiplier,
                    GpuOccupancyPercent = gpuFact.UsagePercent,
                    SourceIndex = checked((uint)gpuCursor),
                    ProcessId = checked((uint)process.ProcessId)
                };
                gpuCursor++;
            }
        }

        if (gpuCursor != gpuRowCount)
            throw new InvalidDataException("The compute scoring GPU rows do not match their declared count.");

        var requiredOutputCapacity = Math.Max(
            1,
            checked((cpuComplete ? 3 * processCount : 0) + 2 * gpuRowCount));
        if (requiredOutputCapacity > outputs.Length)
        {
            throw new InvalidDataException("The compute scoring output capacity was exceeded.");
        }

        var envelopeFlags = NativeComputeScoringEnvelopeFlags.None;
        if (cpuComplete)
        {
            envelopeFlags |= NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete;
        }
        if (gpuComplete)
        {
            envelopeFlags |= NativeComputeScoringEnvelopeFlags.GpuSnapshotComplete;
        }
        var validMask = NativeComputeScoringEnvelopeValidity.WelfareCapacity;

        var envelope = new NativeComputeScoringGenerationEnvelope
        {
            AbiVersion = NativeComputeScoringAbi.Version,
            StructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringGenerationEnvelope>(),
            ProcessInputStructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringProcessInput>(),
            GpuInputStructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringGpuInput>(),
            OutputStructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringOutput>(),
            RoundNumber = roundNumber,
            ValidMask = validMask,
            Flags = envelopeFlags,
            ProcessCount = checked((uint)processCount),
            GpuRowCount = checked((uint)gpuRowCount),
            GpuAdapterCount = gpuComplete
                ? checked((uint)gpuInventory.Adapters.Count)
                : 0,
            OutputCapacity = checked((uint)requiredOutputCapacity),
            CpuFreeRatio = welfare.CpuFreeRatio,
            GpuFreeRatio = welfare.GpuFreeRatio,
            VramFreeRatio = welfare.VramFreeRatio,
            MemoryFreeRatio = welfare.MemoryFreeRatio,
            WelfareEligibleProcessCount = welfare.EligibleProcessCount
        };
        var nativeSnapshot = default(NativeComputeScoringSnapshot);
        var status = session.Score(
            in envelope,
            processes.Slice(0, processCount),
            gpus.Slice(0, gpuRowCount),
            outputs.Slice(0, requiredOutputCapacity),
            scratchView.AlignedRegion(layout.NativeScratch, layout.NativeScratchByteCount, layout.NativeScratchAlignment),
            layout.NativeScratchByteCount,
            ref nativeSnapshot);
        if (status != NativeComputeScoringStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native compute scoring failed with {status}.");
        }

        ValidateNativeSnapshot(
            nativeSnapshot,
            envelope,
            cpuComplete,
            gpuComplete);
        return CreateResult(
            outputs,
            processes.Slice(0, processCount),
            gpus.Slice(0, gpuRowCount),
            runtimeFacts,
            nativeSnapshot,
            cpuComplete,
            gpuComplete);
    }

    // This round's actual rows; nothing is carried from an earlier round.
    private ComputeScoringScratchLayout CreateLayout(CpuProjection? cpu, SchedulingProcessFactSnapshot? gpu,
        SchedulingGpuInventorySnapshot inventory, out string rows)
    {
        var processes = Math.Max(cpu?.Facts.Processes.Count ?? 0, gpu?.Processes.Count ?? 0);
        var gpuRows = gpu?.Processes.Sum(static process => process.Gpus.Count) ?? 0;
        var outputs = Math.Max(1, Math.Max(checked(3 * (cpu?.Facts.Processes.Count ?? 0)), checked(2 * gpuRows)));
        if ((uint)processes > capacity.ProcessCapacity || (uint)gpuRows > capacity.GpuCapacity
            || (uint)outputs > capacity.OutputCapacity)
            throw new InvalidDataException("The compute scoring configured capacity was exceeded.");
        var cores = cpu?.CoreInputCount ?? 0;
        var adapters = gpu is null ? 0 : inventory.Adapters.Count;
        rows = $"processes={processes},gpu-rows={gpuRows},outputs={outputs},core-rows={cores},adapters={adapters}";
        return new ComputeScoringScratchLayout(checked((uint)processes), checked((uint)gpuRows),
            checked((uint)outputs), cores,
            NativeComputeScoringSession.ScratchByteCount(checked((uint)processes), checked((uint)gpuRows)),
            capacity.ScratchAlignment, adapters);
    }

    private IComputeScratch CreateRoundScratch(ulong length, string rows)
    {
        var created = createScratch(length)
            ?? throw new InvalidOperationException($"{WorkArea} scratch factory returned no resource.");
        if (created.Length != length)
        {
            var mismatch = new InvalidOperationException($"{WorkArea} scratch factory returned the wrong length.");
            try { created.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(mismatch, cleanup); }
            throw mismatch;
        }
        if (created is ResourceManagerLedgerOwner.ComputeScratch registered)
        {
            registered.WorkArea = WorkArea;
            registered.Rows = rows;
        }
        return created;
    }

    private sealed record CpuProjection(SchedulingProcessFactSnapshot Facts, HostManagerCpuOccupancySource Source,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, CpuProcessCoreResidency>? Observed, int CoreInputCount);

    private void FillCpuDomain(ComputeScratchView scratchView, CpuProjection projection)
    {
        var weightedCpuUse = scratchView.Column<double>(layout.WeightedCpuUse, layout.ProcessCapacity);
        if (projection.Source == HostManagerCpuOccupancySource.ProcessCpuUsage)
        {
            for (var index = 0; index < projection.Facts.Processes.Count; index++)
                weightedCpuUse[index] = projection.Facts.Processes[index].CpuUsagePercent;
            return;
        }
        var inputs = scratchView.Column<NativeComputeScoringCpuCoreInput>(layout.CoreInputs, layout.CoreInputCapacity);
        var cursor = 0;
        for (var index = 0; index < projection.Facts.Processes.Count; index++)
        {
            var process = projection.Facts.Processes[index];
            if (!projection.Observed!.TryGetValue(new(process.ProcessId, process.ProcessStartKey), out var observation)) continue;
            foreach (var core in observation.PhysicalCores.OrderBy(core => cpuCoreIndexes[core.PhysicalCoreId]))
                inputs[cursor++] = new NativeComputeScoringCpuCoreInput
                {
                    ProcessIndex = checked((uint)index), CoreIndex = cpuCoreIndexes[core.PhysicalCoreId],
                    UsagePercent = core.UsagePercent
                };
        }
        var status = cpuSession.CalculateWeightedCpuUse(inputs.Slice(0, cursor),
            weightedCpuUse.Slice(0, projection.Facts.Processes.Count));
        if (status != NativeComputeScoringStatus.Ok)
            throw new InvalidDataException($"CPU physical-core projection failed with {status}.");
    }

    /// <summary>
    /// A trusted closed residency window includes zero CPU use for covered processes absent from its sparse rows.
    /// Invalid or uncovered process rows are omitted; an untrusted dataset uses explicit process CPU usage.
    /// The chosen source travels with the result.
    /// </summary>
    private CpuProjection? PrepareCpuDomain(
        SchedulingProcessFactSnapshot source, CpuCoreResidencySnapshot? current,
        ICollection<SchedulingFactExclusion>? exclusions)
    {
        if (!IsTrustedCpuResidency(current) || !CpuScoring.CoreWeights.IsAvailable)
        {
            return PrepareProcessCpuUsage(source);
        }

        var residency = current!;
        var measuredThroughStartKey = checked((ulong)residency.MeasuredThrough.ToFileTime());
        var observed = new Dictionary<HostManagerComputeProcessIdentity, CpuProcessCoreResidency>();
        var ambiguous = new HashSet<HostManagerComputeProcessIdentity>();
        var invalidIdentityProcessIds = new HashSet<int>();
        foreach (var process in residency.Processes)
        {
            if (process.ProcessId > 0 && ulong.TryParse(process.ProcessStartKey, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var startKey) && startKey != 0)
            {
                var identity = new HostManagerComputeProcessIdentity(process.ProcessId, startKey);
                if (!observed.TryAdd(identity, process)) ambiguous.Add(identity);
            }
            else if (process.ProcessId > 0)
            {
                invalidIdentityProcessIds.Add(process.ProcessId);
            }
        }

        var selected = new List<SchedulingProcessFact>();
        var cursor = 0;
        foreach (var process in source.Processes)
        {
            var identity = new HostManagerComputeProcessIdentity(process.ProcessId, process.ProcessStartKey);
            if (process.ProcessStartKey > measuredThroughStartKey)
            {
                Exclude(process, SchedulingFactExclusionReason.CpuResidencyNotCovered);
                continue;
            }
            observed.TryGetValue(identity, out var observation);
            if (invalidIdentityProcessIds.Contains(process.ProcessId) || ambiguous.Contains(identity)
                || observation is not null && (observation.PhysicalCores.Count == 0
                    || observation.PhysicalCores.Any(core => string.IsNullOrWhiteSpace(core.PhysicalCoreId)
                        || !cpuCoreIndexes.ContainsKey(core.PhysicalCoreId)
                        || !double.IsFinite(core.UsagePercent) || core.UsagePercent is < 0 or > 100)
                    || observation.PhysicalCores.Select(core => core.PhysicalCoreId)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != observation.PhysicalCores.Count))
            {
                Exclude(process, SchedulingFactExclusionReason.CpuResidencyInvalid);
                continue;
            }
            if ((uint)selected.Count == capacity.ProcessCapacity) throw new InvalidDataException("CPU process capacity was exceeded.");
            cursor = checked(cursor + (observation?.PhysicalCores.Count ?? 0));
            selected.Add(process);
        }
        if (selected.Count == 0) return null;
        return new(Narrow(source, selected), HostManagerCpuOccupancySource.CoreResidency, observed, cursor);

        void Exclude(SchedulingProcessFact process, SchedulingFactExclusionReason reason)
            => exclusions?.Add(new SchedulingFactExclusion(process.ProcessId, process.ProcessStartKey,
                HostManagerTargetIdentity.CreateProcessTargetId(process.ProcessId, process.ProcessStartKey),
                SchedulingFactCode.CpuCoreResidency, reason, null));
    }

    private static bool IsTrustedCpuResidency(CpuCoreResidencySnapshot? snapshot)
        => snapshot is not null && snapshot.SessionGeneration > 0 && snapshot.Window > TimeSpan.Zero
            && snapshot.MeasuredFrom >= DateTimeOffset.FromFileTime(0)
            && snapshot.MeasuredThrough > snapshot.MeasuredFrom
            && snapshot.MeasuredThrough - snapshot.MeasuredFrom == snapshot.Window
            && snapshot.CapturedAt >= snapshot.MeasuredThrough;

    private CpuProjection? PrepareProcessCpuUsage(SchedulingProcessFactSnapshot source)
    {
        if (!source.IsCpuCurrentComplete()) return null;

        var selected = new List<SchedulingProcessFact>();
        foreach (var process in source.Processes)
        {
            if (!process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage)) continue;
            if ((uint)selected.Count == capacity.ProcessCapacity) throw new InvalidDataException("CPU process capacity was exceeded.");
            selected.Add(process);
        }
        if (selected.Count == 0) return null;

        return new(Narrow(source, selected), HostManagerCpuOccupancySource.ProcessCpuUsage, null, 0);
    }

    private static SchedulingProcessFactSnapshot Narrow(
        SchedulingProcessFactSnapshot source,
        List<SchedulingProcessFact> selected)
        => source with
        {
            Processes = selected.ToArray(),
            EmittedCount = checked((uint)selected.Count)
        };

    private static SchedulingProcessFactSnapshot? ProjectGpuDomain(
        SchedulingProcessFactSnapshot source,
        SchedulingGpuInventorySnapshot gpuInventory)
    {
        const SchedulingProcessMetricMask metric = SchedulingProcessMetricMask.GpuUsage;
        if (!source.IsGpuUsageCurrentComplete(gpuInventory))
        {
            return null;
        }

        var processes = source.Processes
            .Where(process => process.ValidMetricMask.HasFlag(metric))
            .Select(process => process with
            {
                ValidMetricMask = metric,
                CpuUsagePercent = 0,
                MemoryUsedBytes = 0,
                Gpus = process.Gpus
                        .Where(static gpu => gpu.ValidMetricMask.HasFlag(
                            SchedulingProcessMetricMask.GpuUsage))
                        .Select(static gpu => gpu with
                        {
                            ValidMetricMask = SchedulingProcessMetricMask.GpuUsage,
                            DedicatedMemoryUsedPercent = 0,
                            DedicatedMemorySourceGeneration = 0,
                            DedicatedMemoryTopologyGeneration = 0,
                            PrivateMemoryBytes = null,
                            SharedMemoryBytes = null
                        })
                        .ToArray()
            })
            .ToArray();
        return Narrow(source, processes.ToList()) with
        {
            RequestedMetricMask = metric,
            CurrentMetricMask = metric,
            Processes = processes,
            GpuSourceGeneration = source.DatasetObservations[metric].SourceGeneration,
            GpuObservedAtUtcTicks = source.DatasetObservations[metric].ObservedAtUtcTicks,
            GpuTopologyGeneration = source.DatasetObservations[metric].TopologyGeneration,
            GpuTopologyFingerprint = source.DatasetObservations[metric].TopologyFingerprint,
            DatasetObservations = new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>
            {
                [metric] = source.DatasetObservations[metric]
            }
        };
    }

    private static IReadOnlyDictionary<
        HostManagerComputeProcessIdentity,
        HostManagerComputeRuntimeFact> ProjectRuntimeFacts(
        SchedulingProcessFactSnapshot processFacts,
        IReadOnlyDictionary<
            HostManagerComputeProcessIdentity,
            HostManagerComputeRuntimeFact> source)
    {
        var result = new Dictionary<
            HostManagerComputeProcessIdentity,
            HostManagerComputeRuntimeFact>(processFacts.Processes.Count);
        foreach (var process in processFacts.Processes)
        {
            var identity = new HostManagerComputeProcessIdentity(
                process.ProcessId,
                process.ProcessStartKey);
            if (!source.TryGetValue(identity, out var runtime))
            {
                throw new InvalidDataException(
                    $"The compute scoring domain is missing runtime state for process {process.ProcessId}/{process.ProcessStartKey}.");
            }
            result.Add(identity, runtime);
        }
        return result;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        try { cpuSession.Dispose(); }
        finally { gpuSession.Dispose(); }
    }

    private void FillAdapterOrder(
        SchedulingGpuInventorySnapshot inventory,
        bool gpuComplete,
        Span<int> adapterOrder)
    {
        if (!gpuComplete)
        {
            return;
        }
        if (inventory.Adapters.Count > adapterOrder.Length)
        {
            throw new InvalidDataException("The compute scoring adapter capacity was exceeded.");
        }

        var count = inventory.Adapters.Count;
        var index = 0;
        while (index < count)
        {
            adapterOrder[index] = index;
            index++;
        }

        index = 1;
        while (index < count)
        {
            var slot = adapterOrder[index];
            var cursor = index;
            while (cursor > 0
                && inventory.Adapters[adapterOrder[cursor - 1]].AdapterKey
                    > inventory.Adapters[slot].AdapterKey)
            {
                adapterOrder[cursor] = adapterOrder[cursor - 1];
                cursor--;
            }
            adapterOrder[cursor] = slot;
            index++;
        }
    }

    private static SchedulingProcessGpuFact? FindGpuFact(
        SchedulingProcessFact process,
        ulong adapterKey)
    {
        var index = 0;
        while (index < process.Gpus.Count)
        {
            var fact = process.Gpus[index];
            if (fact.AdapterKey == adapterKey)
            {
                return fact;
            }
            index++;
        }
        return null;
    }

    private static void ValidateRuntimeFact(HostManagerComputeRuntimeFact runtime)
    {
        if (runtime.RuntimeState > NativeComputeScoringRuntimeState.NotRunning
            || !IsValidPolicyMultiplier(runtime.CpuPolicyMultiplier)
            || !IsValidPolicyMultiplier(runtime.GpuPolicyMultiplier))
        {
            throw new InvalidDataException("The compute scoring runtime fact is invalid.");
        }
    }

    private static bool IsValidPolicyMultiplier(double value)
        => double.IsFinite(value) && value >= 0;

    private void ValidateNativeSnapshot(
        NativeComputeScoringSnapshot snapshot,
        NativeComputeScoringGenerationEnvelope envelope,
        bool cpuComplete,
        bool gpuComplete)
    {
        var expectedFlags = (cpuComplete
                ? NativeComputeScoringSnapshotFlags.CpuScoresValid
                : NativeComputeScoringSnapshotFlags.None)
            | (gpuComplete
                ? NativeComputeScoringSnapshotFlags.GpuScoresValid
                : NativeComputeScoringSnapshotFlags.None);
        if (snapshot.AbiVersion != NativeComputeScoringAbi.Version
            || snapshot.StructSize != NativeComputeScoringSession.SizeOf<NativeComputeScoringSnapshot>()
            || snapshot.Flags != expectedFlags
            || snapshot.ProcessInputCount != envelope.ProcessCount
            || snapshot.GpuInputCount != envelope.GpuRowCount
            || snapshot.OutputCount != snapshot.ProcessCpuOutputCount
                + snapshot.ProcessGpuOutputCount
                + snapshot.SoftwareCpuOutputCount
                + snapshot.SoftwareGpuOutputCount
                + snapshot.SoftwareMemoryOutputCount
            || snapshot.OutputCount > envelope.OutputCapacity
            || snapshot.CpuFreeRatio != envelope.CpuFreeRatio
            || snapshot.GpuFreeRatio != envelope.GpuFreeRatio
            || snapshot.VramFreeRatio != envelope.VramFreeRatio
            || snapshot.MemoryFreeRatio != envelope.MemoryFreeRatio
            || snapshot.WelfareEligibleProcessCount
                != envelope.WelfareEligibleProcessCount
            || snapshot.Reserved0 != 0
            || snapshot.Reserved1 != 0
            || snapshot.Reserved != 0)
        {
            throw new InvalidOperationException(
                "The native compute scoring snapshot violated its managed contract.");
        }

        if (!IsRatio(snapshot.CpuWelfareMultiplier)
            || !IsRatio(snapshot.MemoryWelfareMultiplier)
            || !double.IsFinite(snapshot.CpuWelfareBonus)
            || snapshot.CpuWelfareBonus < 0
            || snapshot.CpuWelfareBonus > welfareBaseScore
            || !double.IsFinite(snapshot.MemoryWelfareBonus)
            || snapshot.MemoryWelfareBonus < 0
            || snapshot.MemoryWelfareBonus > welfareBaseScore
            || !IsRatio(snapshot.WelfareMultiplier)
            || !IsRatio(snapshot.SystemPressure)
            || !double.IsFinite(snapshot.WelfareBudget)
            || snapshot.WelfareBudget < 0
            || !double.IsFinite(snapshot.WelfareShare)
            || snapshot.WelfareShare < 0
            || snapshot.WelfareEligibleProcessCount == 0
                && snapshot.WelfareShare != 0
            || snapshot.WelfareEligibleProcessCount > 0
                && snapshot.WelfareShare > snapshot.WelfareBudget)
        {
            throw new InvalidOperationException(
                "The native compute scoring welfare result violated its managed contract.");
        }
    }

    // Output sections follow the native order: process CPU, software CPU, process GPU,
    // software GPU, software memory. Identity comes from the input row each output names.
    private HostManagerComputeScoringCycleResult CreateResult(
        ReadOnlySpan<NativeComputeScoringOutput> outputs,
        ReadOnlySpan<NativeComputeScoringProcessInput> processes,
        ReadOnlySpan<NativeComputeScoringGpuInput> gpus,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact> runtimeFacts,
        NativeComputeScoringSnapshot snapshot,
        bool cpuComplete,
        bool gpuComplete)
    {
        var processCpuCount = checked((int)snapshot.ProcessCpuOutputCount);
        var softwareCpuCount = checked((int)snapshot.SoftwareCpuOutputCount);
        var processGpuCount = checked((int)snapshot.ProcessGpuOutputCount);
        var softwareGpuCount = checked((int)snapshot.SoftwareGpuOutputCount);
        var memoryCount = checked((int)snapshot.SoftwareMemoryOutputCount);
        if (!cpuComplete && processCpuCount + softwareCpuCount != 0)
        {
            throw new InvalidOperationException("Native compute scoring emitted CPU output for an incomplete CPU domain.");
        }
        if (!gpuComplete && processGpuCount + softwareGpuCount != 0)
        {
            throw new InvalidOperationException("Native compute scoring emitted GPU output for an incomplete GPU domain.");
        }
        if (!cpuComplete && memoryCount != 0)
        {
            throw new InvalidOperationException("Native compute scoring emitted memory output without CPU scores.");
        }

        var roundNumber = snapshot.RoundNumber;
        var cursor = 0;
        ImmutableArray<HostManagerComputeScore>.Builder? cpu = null;
        ImmutableArray<HostManagerComputeScore>.Builder? gpu = null;
        ImmutableArray<HostManagerComputeScore>.Builder? memory = null;
        if (cpuComplete)
        {
            cpu = ImmutableArray.CreateBuilder<HostManagerComputeScore>(processCpuCount + softwareCpuCount);
            for (var index = 0; index < processCpuCount; index++)
                cpu.Add(ConvertOutput(NativeComputeScoringOutputKind.ProcessCpu, outputs[cursor++],
                    processes, gpus, runtimeFacts, roundNumber));
            for (var index = 0; index < softwareCpuCount; index++)
                cpu.Add(ConvertOutput(NativeComputeScoringOutputKind.SoftwareCpu, outputs[cursor++],
                    processes, gpus, runtimeFacts, roundNumber));
        }
        if (gpuComplete)
        {
            gpu = ImmutableArray.CreateBuilder<HostManagerComputeScore>(processGpuCount + softwareGpuCount);
            for (var index = 0; index < processGpuCount; index++)
                gpu.Add(ConvertOutput(NativeComputeScoringOutputKind.ProcessGpu, outputs[cursor++],
                    processes, gpus, runtimeFacts, roundNumber));
            for (var index = 0; index < softwareGpuCount; index++)
                gpu.Add(ConvertOutput(NativeComputeScoringOutputKind.SoftwareGpu, outputs[cursor++],
                    processes, gpus, runtimeFacts, roundNumber));
        }
        if (cpuComplete)
        {
            memory = ImmutableArray.CreateBuilder<HostManagerComputeScore>(memoryCount);
            for (var index = 0; index < memoryCount; index++)
                memory.Add(ConvertOutput(NativeComputeScoringOutputKind.SoftwareMemory, outputs[cursor++],
                    processes, gpus, runtimeFacts, roundNumber));
        }

        return new HostManagerComputeScoringCycleResult(
            snapshot.RoundNumber,
            cpu is null
                ? null
                : new HostManagerComputeScoreDomainSnapshot(
                    snapshot.RoundNumber,
                    cpu.MoveToImmutable()),
            gpu is null
                ? null
                : new HostManagerComputeScoreDomainSnapshot(
                    snapshot.RoundNumber,
                    gpu.MoveToImmutable()))
        {
            Memory = memory is null
                ? null
                : new HostManagerComputeScoreDomainSnapshot(
                    snapshot.RoundNumber,
                    memory.MoveToImmutable()),
            Welfare = new HostManagerWelfareScoreSnapshot(
                snapshot.CpuFreeRatio,
                snapshot.GpuFreeRatio,
                snapshot.VramFreeRatio,
                snapshot.MemoryFreeRatio,
                snapshot.WelfareMultiplier,
                snapshot.SystemPressure,
                welfareBaseScore,
                snapshot.WelfareBudget,
                snapshot.WelfareShare,
                snapshot.WelfareEligibleProcessCount)
            {
                CpuMultiplier = snapshot.CpuWelfareMultiplier,
                MemoryMultiplier = snapshot.MemoryWelfareMultiplier,
                CpuBonus = snapshot.CpuWelfareBonus,
                MemoryBonus = snapshot.MemoryWelfareBonus
            }
        };
    }

    private static bool IsRatio(double value)
        => double.IsFinite(value) && value is >= 0 and <= 1;

    private static HostManagerComputeScore ConvertOutput(
        NativeComputeScoringOutputKind kind,
        NativeComputeScoringOutput row,
        ReadOnlySpan<NativeComputeScoringProcessInput> processes,
        ReadOnlySpan<NativeComputeScoringGpuInput> gpus,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact> runtimeFacts,
        ulong roundNumber)
    {
        var processOutput = kind is NativeComputeScoringOutputKind.ProcessCpu
            or NativeComputeScoringOutputKind.ProcessGpu;
        var gpuOutput = kind is NativeComputeScoringOutputKind.ProcessGpu
            or NativeComputeScoringOutputKind.SoftwareGpu;
        if (!double.IsFinite(row.Score)
            || row.Score < 0
            || row.InputIndex >= (uint)(gpuOutput ? gpus.Length : processes.Length)
            || (processOutput ? row.MemberCount != 1 : row.MemberCount == 0))
        {
            throw new InvalidOperationException("Native compute scoring emitted an invalid output row.");
        }

        if (gpuOutput)
        {
            ref readonly var gpu = ref gpus[(int)row.InputIndex];
            if (!processOutput)
            {
                return new HostManagerComputeScore(kind, roundNumber, 0, gpu.SoftwareKey, 0, 0,
                    gpu.AdapterKey, row.Score, row.MemberCount, NativeComputeScoringRuntimeState.Unknown);
            }
            var processId = checked((int)gpu.ProcessId);
            // A GPU input carries its process identity but not its runtime state.
            if (!runtimeFacts.TryGetValue(new HostManagerComputeProcessIdentity(processId, gpu.ProcessStartKey), out var runtime))
            {
                throw new InvalidOperationException("Native compute scoring named a GPU row without runtime state.");
            }
            return new HostManagerComputeScore(kind, roundNumber, gpu.TargetKey, gpu.SoftwareKey, processId,
                gpu.ProcessStartKey, gpu.AdapterKey, row.Score, 1, runtime.RuntimeState);
        }

        ref readonly var process = ref processes[(int)row.InputIndex];
        return processOutput
            ? new HostManagerComputeScore(kind, roundNumber, process.TargetKey, process.SoftwareKey,
                checked((int)process.ProcessId), process.ProcessStartKey, 0, row.Score, 1, process.RuntimeState)
            : new HostManagerComputeScore(kind, roundNumber, 0, process.SoftwareKey, 0, 0, 0,
                row.Score, row.MemberCount, NativeComputeScoringRuntimeState.Unknown);
    }
}
