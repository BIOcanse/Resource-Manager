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
    private readonly NativeComputeScoringProcessInput[] processes;
    private readonly NativeComputeScoringGpuInput[] gpus;
    private readonly FrozenDictionary<string, uint> cpuCoreIndexes;
    private readonly NativeComputeScoringCpuCoreInput[] cpuCoreInputs;
    private readonly double[] weightedCpuUse;
    private readonly NativeComputeScoringOutput[] outputs;
    private readonly int[] adapterOrder;
    private ImmutableArray<SoftwareBaseScore> softwareBaseScores = [];
    private double softwareBaseMean;
    private readonly ulong configurationFingerprint;
    private bool disposed;

    internal HostManagerComputeScoringWorkspace(
        in NativeComputeScoringConfiguration configuration,
        CompiledCpuScoringPlan cpuScoring)
    {
        CpuScoring = cpuScoring;
        var weights = cpuScoring.CoreWeights.Cores.Select(static core => core.ReferenceWeight).ToArray();
        configurationFingerprint = ComputeConfigurationFingerprint(in configuration, weights);
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
        var capacity = cpuSession.Capacity;
        processes = new NativeComputeScoringProcessInput[checked((int)capacity.ProcessCapacity)];
        weightedCpuUse = new double[processes.Length];
        cpuCoreInputs = new NativeComputeScoringCpuCoreInput[checked(processes.Length * weights.Length)];
        gpus = new NativeComputeScoringGpuInput[checked((int)capacity.GpuCapacity)];
        outputs = new NativeComputeScoringOutput[checked((int)capacity.OutputCapacity)];
        adapterOrder = new int[Math.Max(1, checked((int)capacity.GpuCapacity))];
    }

    internal ulong ConfigurationGeneration =>
        cpuSession.Capacity.ConfigurationGeneration;

    internal CompiledCpuScoringPlan CpuScoring { get; }

    internal HostManagerComputeScoringCycleResult? Score(
        ulong schedulingGeneration,
        SchedulingProcessFactSnapshot processFacts,
        SchedulingGpuInventorySnapshot gpuInventory,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact> runtimeFacts,
        CpuCoreResidencySnapshot? cpuResidency,
        HostManagerWelfareCapacityInput? welfareCapacity = null,
        bool scoreCpu = true,
        bool scoreGpu = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(processFacts);
        ArgumentNullException.ThrowIfNull(gpuInventory);
        ArgumentNullException.ThrowIfNull(runtimeFacts);
        ArgumentOutOfRangeException.ThrowIfZero(schedulingGeneration);
        if (!processFacts.IsInventoryCurrentComplete())
        {
            return null;
        }
        if (runtimeFacts.Count != processFacts.Processes.Count)
        {
            throw new InvalidDataException(
                "The compute scoring runtime-state set does not match the process snapshot union.");
        }

        var eligibleProcessCount = checked((uint)runtimeFacts.Values.Count(static runtime =>
            runtime.RuntimeState != NativeComputeScoringRuntimeState.NotRunning));
        var welfare = welfareCapacity
            ?? HostManagerWelfareCapacityInput.Unavailable(eligibleProcessCount);
        if (!welfare.IsValid || welfare.EligibleProcessCount != eligibleProcessCount)
        {
            throw new InvalidDataException(
                "The welfare-capacity input does not match the current runtime process union.");
        }

        var replacement = processFacts.SoftwareBaseScores;
        if (softwareBaseScores != replacement)
        {
            if (!softwareBaseScores.AsSpan().SequenceEqual(replacement.AsSpan()))
            {
                softwareBaseMean = cpuSession.CalculateSoftwareBaseMean(
                    replacement.Select(static item => item.BaseScore).ToArray());
            }
            softwareBaseScores = replacement;
        }

        var cpuFacts = scoreCpu ? ProjectCpuDomain(processFacts, cpuResidency) : null;
        var gpuFacts = scoreGpu ? ProjectGpuDomain(processFacts, gpuInventory) : null;
        if (cpuFacts is null && gpuFacts is null)
        {
            return null;
        }

        var cpuSourceIdentity = cpuFacts is null
            ? default
            : CreateScoreSourceIdentity(
                processFacts,
                SchedulingProcessMetricMask.CpuUsage,
                welfare,
                cpuResidency);
        var gpuSourceIdentity = gpuFacts is null
            ? default
            : CreateScoreSourceIdentity(
                processFacts,
                SchedulingProcessMetricMask.GpuUsage,
                welfare);

        var cpuResult = cpuFacts is null
            ? null
            : ScoreEnvelope(
                cpuSession,
                schedulingGeneration,
                cpuFacts,
                gpuInventory,
                ProjectRuntimeFacts(cpuFacts, runtimeFacts),
                cpuSourceIdentity,
                cpuResidency,
                welfare);
        var gpuResult = gpuFacts is null
            ? null
            : ScoreEnvelope(
                gpuSession,
                schedulingGeneration,
                gpuFacts,
                gpuInventory,
                ProjectRuntimeFacts(gpuFacts, runtimeFacts),
                gpuSourceIdentity,
                null,
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
            schedulingGeneration,
            cpuResult?.Cpu,
            gpuResult?.Gpu)
        {
            Welfare = welfareSnapshot,
            Memory = cpuResult?.Memory
        };
    }

    private HostManagerComputeScoringCycleResult? ScoreEnvelope(
        NativeComputeScoringSession session,
        ulong schedulingGeneration,
        SchedulingProcessFactSnapshot processFacts,
        SchedulingGpuInventorySnapshot gpuInventory,
        IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact> runtimeFacts,
        HostManagerComputeScoreSourceIdentity sourceIdentity,
        CpuCoreResidencySnapshot? cpuResidency,
        HostManagerWelfareCapacityInput welfare)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(processFacts);
        ArgumentNullException.ThrowIfNull(gpuInventory);
        ArgumentNullException.ThrowIfNull(runtimeFacts);
        ArgumentOutOfRangeException.ThrowIfZero(schedulingGeneration);

        if (!processFacts.IsInventoryCurrentComplete())
        {
            return null;
        }

        var cpuComplete = cpuResidency is not null;
        var gpuComplete = !cpuComplete && processFacts.IsGpuUsageCurrentComplete(gpuInventory);
        if (!cpuComplete && !gpuComplete)
        {
            return null;
        }
        if (!sourceIdentity.IsWellFormed(gpuComplete))
        {
            throw new InvalidDataException(
                "The compute scoring source identity is incomplete.");
        }
        SchedulingProcessDatasetObservation? gpuDataset = null;
        if (gpuComplete)
        {
            if (!processFacts.TryGetCurrentDataset(
                    SchedulingProcessMetricMask.GpuUsage,
                    out var currentGpuDataset))
            {
                return null;
            }
            gpuDataset = currentGpuDataset;
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

        FillAdapterOrder(gpuInventory, gpuComplete);
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
                SourceGeneration = sourceIdentity.MetricGeneration,
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
                    || gpuFact.UsageSourceGeneration !=
                        gpuDataset!.SourceGeneration
                    || gpuFact.UsageTopologyGeneration !=
                        gpuDataset.TopologyGeneration
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
                    SourceGeneration = gpuDataset!.SourceGeneration,
                    AdapterKey = adapter.AdapterKey,
                    GpuPolicyMultiplier = runtime.GpuPolicyMultiplier,
                    GpuOccupancyPercent = gpuFact.UsagePercent,
                    SourceIndex = checked((uint)gpuCursor),
                    ProcessId = checked((uint)process.ProcessId)
                };
                gpuCursor++;
            }
        }

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
        var validMask = NativeComputeScoringEnvelopeValidity.ProcessGeneration |
            NativeComputeScoringEnvelopeValidity.ProcessObservedAt |
            NativeComputeScoringEnvelopeValidity.WelfareCapacity;
        if (gpuComplete)
        {
            validMask |= NativeComputeScoringEnvelopeValidity.GpuGeneration |
                NativeComputeScoringEnvelopeValidity.GpuObservedAt |
                NativeComputeScoringEnvelopeValidity.GpuTopology;
        }

        var envelope = new NativeComputeScoringGenerationEnvelope
        {
            AbiVersion = NativeComputeScoringAbi.Version,
            StructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringGenerationEnvelope>(),
            ProcessInputStructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringProcessInput>(),
            GpuInputStructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringGpuInput>(),
            OutputStructSize = NativeComputeScoringSession.SizeOf<NativeComputeScoringOutput>(),
            ConfigurationGeneration = ConfigurationGeneration,
            SchedulingGeneration = schedulingGeneration,
            ProcessSourceGeneration = sourceIdentity.MetricGeneration,
            GpuSourceGeneration = gpuComplete ? gpuDataset!.SourceGeneration : 0,
            GpuTopologyGeneration = gpuComplete ? gpuDataset!.TopologyGeneration : 0,
            GpuTopologyFingerprint = gpuComplete ? gpuDataset!.TopologyFingerprint : 0,
            ProcessObservedAtMilliseconds = ToUnixTimeMilliseconds(
                sourceIdentity.MetricObservedAtUtcTicks),
            GpuObservedAtMilliseconds = gpuComplete
                ? ToUnixTimeMilliseconds(gpuDataset!.ObservedAtUtcTicks)
                : 0,
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
            WelfareEligibleProcessCount = welfare.EligibleProcessCount,
            SoftwareBaseMean = softwareBaseMean
        };
        var nativeSnapshot = default(NativeComputeScoringSnapshot);
        var status = session.Score(
            in envelope,
            processes.AsSpan(0, processCount),
            gpus.AsSpan(0, gpuRowCount),
            outputs.AsSpan(0, requiredOutputCapacity),
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
            nativeSnapshot,
            cpuComplete,
            gpuComplete,
            sourceIdentity);
    }

    private SchedulingProcessFactSnapshot? ProjectCpuDomain(
        SchedulingProcessFactSnapshot source, CpuCoreResidencySnapshot? current)
    {
        if (current is null || !CpuScoring.CoreWeights.IsAvailable) return null;

        var observed = new Dictionary<HostManagerComputeProcessIdentity, CpuProcessCoreResidency>();
        foreach (var process in current.Processes)
        {
            if (process.ProcessId > 0 && ulong.TryParse(process.ProcessStartKey, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var startKey) && startKey != 0)
            {
                if (!observed.TryAdd(new(process.ProcessId, startKey), process))
                    throw new InvalidDataException("CPU core observations contain a duplicate process identity.");
            }
        }

        var selected = new List<SchedulingProcessFact>();
        var cursor = 0;
        foreach (var process in source.Processes)
        {
            if (!observed.TryGetValue(new(process.ProcessId, process.ProcessStartKey), out var observation)
                || observation.PhysicalCores.Count == 0
                || observation.PhysicalCores.Any(core => !cpuCoreIndexes.ContainsKey(core.PhysicalCoreId)))
                continue;
            if (selected.Count == processes.Length) throw new InvalidDataException("CPU process capacity was exceeded.");
            foreach (var core in observation.PhysicalCores.OrderBy(core => cpuCoreIndexes[core.PhysicalCoreId]))
            {
                if (cursor == cpuCoreInputs.Length) throw new InvalidDataException("CPU physical-core capacity was exceeded.");
                cpuCoreInputs[cursor++] = new NativeComputeScoringCpuCoreInput
                {
                    ProcessIndex = checked((uint)selected.Count),
                    CoreIndex = cpuCoreIndexes[core.PhysicalCoreId],
                    UsagePercent = core.UsagePercent
                };
            }
            selected.Add(process);
        }
        if (selected.Count == 0) return null;
        var status = cpuSession.CalculateWeightedCpuUse(cpuCoreInputs.AsSpan(0, cursor),
            weightedCpuUse.AsSpan(0, selected.Count));
        if (status != NativeComputeScoringStatus.Ok)
            throw new InvalidDataException($"CPU physical-core projection failed with {status}.");

        return source with
        {
            Processes = selected.ToArray(),
            EmittedCount = checked((uint)selected.Count),
            SkippedCount = checked(source.SkippedCount + (uint)(source.Processes.Count - selected.Count))
        };
    }

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
                MemoryUsagePercent = 0,
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
        var emitted = checked((uint)processes.Length);
        var accounted = checked((ulong)source.ExcludedCount + emitted);
        if (accounted > source.EnumeratedCount)
        {
            throw new InvalidDataException(
                "The process-domain projection exceeds the source inventory accounting.");
        }
        return source with
        {
            EmittedCount = emitted,
            SkippedCount = checked((uint)(source.EnumeratedCount - accounted)),
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
        cpuSession.Dispose();
        gpuSession.Dispose();
    }

    private void FillAdapterOrder(
        SchedulingGpuInventorySnapshot inventory,
        bool gpuComplete)
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

    private static long ToUnixTimeMilliseconds(long utcTicks)
    {
        if (utcTicks < DateTimeOffset.UnixEpoch.UtcTicks)
        {
            throw new InvalidDataException("The compute scoring observation time predates the Unix epoch.");
        }
        return new DateTimeOffset(utcTicks, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    private static void ValidateNativeSnapshot(
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
            || snapshot.ConfigurationGeneration != envelope.ConfigurationGeneration
            || snapshot.SchedulingGeneration != envelope.SchedulingGeneration
            || snapshot.ProcessSourceGeneration != envelope.ProcessSourceGeneration
            || snapshot.GpuSourceGeneration != (gpuComplete ? envelope.GpuSourceGeneration : 0)
            || snapshot.GpuTopologyGeneration != (gpuComplete ? envelope.GpuTopologyGeneration : 0)
            || snapshot.GpuTopologyFingerprint != (gpuComplete ? envelope.GpuTopologyFingerprint : 0)
            || snapshot.Flags != expectedFlags
            || snapshot.ProcessInputCount != envelope.ProcessCount
            || snapshot.GpuInputCount != envelope.GpuRowCount
            || snapshot.OutputCount != snapshot.ProcessCpuOutputCount
                + snapshot.ProcessGpuOutputCount
                + snapshot.SoftwareCpuOutputCount
                + snapshot.SoftwareGpuOutputCount
                + snapshot.SoftwareMemoryOutputCount
            || snapshot.InvalidFactCount != 0
            || snapshot.OutputCount > envelope.OutputCapacity
            || snapshot.CpuFreeRatio != envelope.CpuFreeRatio
            || snapshot.GpuFreeRatio != envelope.GpuFreeRatio
            || snapshot.VramFreeRatio != envelope.VramFreeRatio
            || snapshot.MemoryFreeRatio != envelope.MemoryFreeRatio
            || snapshot.SoftwareBaseMean != envelope.SoftwareBaseMean
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
            || snapshot.CpuWelfareBonus > snapshot.SoftwareBaseMean
            || !double.IsFinite(snapshot.MemoryWelfareBonus)
            || snapshot.MemoryWelfareBonus < 0
            || snapshot.MemoryWelfareBonus > snapshot.SoftwareBaseMean
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

    private HostManagerComputeScoringCycleResult CreateResult(
        NativeComputeScoringSnapshot snapshot,
        bool cpuComplete,
        bool gpuComplete,
        HostManagerComputeScoreSourceIdentity sourceIdentity)
    {
        var cpuCount = checked((int)(snapshot.ProcessCpuOutputCount + snapshot.SoftwareCpuOutputCount));
        var gpuCount = checked((int)(snapshot.ProcessGpuOutputCount + snapshot.SoftwareGpuOutputCount));
        var memoryCount = checked((int)snapshot.SoftwareMemoryOutputCount);
        var cpu = cpuComplete
            ? ImmutableArray.CreateBuilder<HostManagerComputeScore>(cpuCount)
            : null;
        var gpu = gpuComplete
            ? ImmutableArray.CreateBuilder<HostManagerComputeScore>(gpuCount)
            : null;
        var memory = cpuComplete
            ? ImmutableArray.CreateBuilder<HostManagerComputeScore>(memoryCount)
            : null;
        var outputCount = checked((int)snapshot.OutputCount);
        for (var index = 0; index < outputCount; index++)
        {
            var row = ConvertOutput(outputs[index], snapshot.SchedulingGeneration);
            switch (row.Kind)
            {
                case NativeComputeScoringOutputKind.ProcessCpu:
                case NativeComputeScoringOutputKind.SoftwareCpu:
                    if (cpu is null)
                    {
                        throw new InvalidOperationException("Native compute scoring emitted CPU output for an incomplete CPU domain.");
                    }
                    cpu.Add(row);
                    break;
                case NativeComputeScoringOutputKind.ProcessGpu:
                case NativeComputeScoringOutputKind.SoftwareGpu:
                    if (gpu is null)
                    {
                        throw new InvalidOperationException("Native compute scoring emitted GPU output for an incomplete GPU domain.");
                    }
                    gpu.Add(row);
                    break;
                case NativeComputeScoringOutputKind.SoftwareMemory:
                    if (memory is null)
                    {
                        throw new InvalidOperationException("Native compute scoring emitted memory output without CPU scores.");
                    }
                    memory.Add(row);
                    break;
                default:
                    throw new InvalidOperationException("Native compute scoring emitted an unknown output kind.");
            }
        }

        if (cpu is not null && cpu.Count != cpuCount
            || gpu is not null && gpu.Count != gpuCount
            || memory is not null && memory.Count != memoryCount)
        {
            throw new InvalidOperationException("Native compute scoring output counts are inconsistent.");
        }

        return new HostManagerComputeScoringCycleResult(
            snapshot.SchedulingGeneration,
            cpu is null
                ? null
                : new HostManagerComputeScoreDomainSnapshot(
                    snapshot.SchedulingGeneration,
                    sourceIdentity,
                    cpu.MoveToImmutable()),
            gpu is null
                ? null
                : new HostManagerComputeScoreDomainSnapshot(
                    snapshot.SchedulingGeneration,
                    sourceIdentity,
                    gpu.MoveToImmutable()))
        {
            Memory = memory is null
                ? null
                : new HostManagerComputeScoreDomainSnapshot(
                    snapshot.SchedulingGeneration,
                    sourceIdentity,
                    memory.MoveToImmutable()),
            Welfare = new HostManagerWelfareScoreSnapshot(
                snapshot.CpuFreeRatio,
                snapshot.GpuFreeRatio,
                snapshot.VramFreeRatio,
                snapshot.MemoryFreeRatio,
                snapshot.WelfareMultiplier,
                snapshot.SystemPressure,
                snapshot.SoftwareBaseMean,
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

    private HostManagerComputeScoreSourceIdentity CreateScoreSourceIdentity(
        SchedulingProcessFactSnapshot processFacts,
        SchedulingProcessMetricMask metric,
        HostManagerWelfareCapacityInput welfare,
        CpuCoreResidencySnapshot? cpuResidency = null)
    {
        SchedulingProcessDatasetObservation? metricObservation = null;
        if (cpuResidency is null && !processFacts.TryGetCurrentDataset(metric, out metricObservation))
        {
            throw new InvalidDataException(
                $"The compute scoring metric {metric} has no current observation.");
        }
        var metricGeneration = cpuResidency is null ? metricObservation!.SourceGeneration : checked((ulong)cpuResidency.SessionGeneration);
        var metricObservedAtUtcTicks = cpuResidency?.CapturedAt.UtcTicks ?? metricObservation!.ObservedAtUtcTicks;

        ulong inventoryGeneration;
        long inventoryObservedAtUtcTicks;
        ulong attributionGeneration;
        long attributionObservedAtUtcTicks;
        ulong runtimeStateGeneration;
        long runtimeStateObservedAtUtcTicks;

        if (processFacts.HasSeparateFoundationPayloads)
        {
            if (!processFacts.TryGetCurrentFoundationDataset(
                    SamplingDatasetIds.ProcessInventory,
                    out var inventory)
                || !processFacts.TryGetCurrentFoundationDataset(
                    SamplingDatasetIds.ProcessAttribution,
                    out var attribution)
                || !processFacts.TryGetCurrentDataset(
                    SchedulingProcessMetricMask.RuntimeState,
                    out var runtimeState))
            {
                throw new InvalidDataException(
                    "The compute scoring production snapshot is missing a current inventory, attribution, or runtime-state publication.");
            }
            if (inventory.SourceGeneration != processFacts.Generation
                || inventory.ObservedAtUtcTicks != processFacts.ObservedAtUtcTicks)
            {
                throw new InvalidDataException(
                    "The compute scoring production snapshot inventory does not match its composed process union.");
            }

            inventoryGeneration = inventory.SourceGeneration;
            inventoryObservedAtUtcTicks = inventory.ObservedAtUtcTicks;
            attributionGeneration = attribution.SourceGeneration;
            attributionObservedAtUtcTicks = attribution.ObservedAtUtcTicks;
            runtimeStateGeneration = runtimeState.SourceGeneration;
            runtimeStateObservedAtUtcTicks = runtimeState.ObservedAtUtcTicks;
        }
        else
        {
            // Legacy fixtures predate separate foundation publications. Production snapshots
            // always take the strict branch above.
            inventoryGeneration = processFacts.Generation;
            inventoryObservedAtUtcTicks = processFacts.ObservedAtUtcTicks;
            attributionGeneration = processFacts.Generation;
            attributionObservedAtUtcTicks = processFacts.ObservedAtUtcTicks;
            if (processFacts.TryGetCurrentDataset(
                    SchedulingProcessMetricMask.RuntimeState,
                    out var runtimeState))
            {
                runtimeStateGeneration = runtimeState.SourceGeneration;
                runtimeStateObservedAtUtcTicks = runtimeState.ObservedAtUtcTicks;
            }
            else
            {
                runtimeStateGeneration = processFacts.Generation;
                runtimeStateObservedAtUtcTicks = processFacts.ObservedAtUtcTicks;
            }
        }

        var gpuDomain = metric == SchedulingProcessMetricMask.GpuUsage;
        var topologyGeneration = gpuDomain
            ? metricObservation!.TopologyGeneration
            : 0;
        var topologyFingerprint = gpuDomain
            ? metricObservation!.TopologyFingerprint
            : 0;
        var sourceFingerprint = ComputeSourceFingerprint(
            gpuDomain ? 2UL : 1UL,
            configurationFingerprint,
            inventoryGeneration,
            checked((ulong)inventoryObservedAtUtcTicks),
            attributionGeneration,
            checked((ulong)attributionObservedAtUtcTicks),
            runtimeStateGeneration,
            checked((ulong)runtimeStateObservedAtUtcTicks),
            metricGeneration,
            checked((ulong)metricObservedAtUtcTicks),
            topologyGeneration,
            topologyFingerprint,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(welfare.CpuFreeRatio)),
            unchecked((ulong)BitConverter.DoubleToInt64Bits(welfare.GpuFreeRatio)),
            unchecked((ulong)BitConverter.DoubleToInt64Bits(welfare.VramFreeRatio)),
            unchecked((ulong)BitConverter.DoubleToInt64Bits(welfare.MemoryFreeRatio)),
            welfare.EligibleProcessCount,
            DoubleBits(softwareBaseMean),
            welfare.PublicationFingerprint);
        var result = new HostManagerComputeScoreSourceIdentity(
            inventoryGeneration,
            inventoryObservedAtUtcTicks,
            attributionGeneration,
            attributionObservedAtUtcTicks,
            runtimeStateGeneration,
            runtimeStateObservedAtUtcTicks,
            metricGeneration,
            metricObservedAtUtcTicks,
            topologyGeneration,
            topologyFingerprint,
            sourceFingerprint);
        if (!result.IsWellFormed(gpuDomain))
        {
            throw new InvalidDataException(
                "The compute scoring source identity is not well formed.");
        }
        return result;
    }

    private static ulong ComputeConfigurationFingerprint(
        in NativeComputeScoringConfiguration configuration,
        ReadOnlySpan<double> coreWeights)
    {
        var fields = new ulong[
            10 + (NativeComputeScoringAbi.RuntimeStateCount * 2) + coreWeights.Length];
        var index = 0;
        fields[index++] = configuration.AbiVersion;
        fields[index++] = configuration.Generation;
        fields[index++] = configuration.MaximumProcessCount;
        fields[index++] = configuration.MaximumGpuRowCount;
        fields[index++] = configuration.MaximumOutputCount;
        fields[index++] = configuration.CpuCoreCount;
        fields[index++] = DoubleBits(configuration.MaximumBaseImportance);
        fields[index++] = DoubleBits(configuration.MaximumPolicyMultiplier);
        fields[index++] = DoubleBits(configuration.CpuBaselineRatio);
        fields[index++] = DoubleBits(configuration.WelfareUtilizationBaselinePercent);

        var copy = configuration;
        var source = &copy;
        for (var state = 0; state < NativeComputeScoringAbi.RuntimeStateCount; state++)
        {
            fields[index++] = DoubleBits(source->CpuStateMultipliers[state]);
        }
        for (var state = 0; state < NativeComputeScoringAbi.RuntimeStateCount; state++)
        {
            fields[index++] = DoubleBits(source->GpuStateMultipliers[state]);
        }
        foreach (var coreWeight in coreWeights)
        {
            fields[index++] = DoubleBits(coreWeight);
        }
        return ComputeSourceFingerprint(fields);
    }

    private static ulong DoubleBits(double value)
        => unchecked((ulong)BitConverter.DoubleToInt64Bits(value));

    private static bool IsRatio(double value)
        => double.IsFinite(value) && value is >= 0 and <= 1;

    private static ulong ComputeSourceFingerprint(
        params ulong[] fields)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var field in fields)
        {
            var value = field;
            for (var byteIndex = 0; byteIndex < sizeof(ulong); byteIndex++)
            {
                hash ^= (byte)value;
                hash = unchecked(hash * prime);
                value >>= 8;
            }
        }
        return hash == 0 ? 1 : hash;
    }

    private static HostManagerComputeScore ConvertOutput(
        NativeComputeScoringOutput row,
        ulong schedulingGeneration)
    {
        var processOutput = row.Kind is NativeComputeScoringOutputKind.ProcessCpu
            or NativeComputeScoringOutputKind.ProcessGpu;
        var gpuOutput = row.Kind is NativeComputeScoringOutputKind.ProcessGpu
            or NativeComputeScoringOutputKind.SoftwareGpu;
        var expectedValidity = NativeComputeScoringOutputValidity.SchedulingGeneration |
            NativeComputeScoringOutputValidity.SoftwareIdentity |
            NativeComputeScoringOutputValidity.Score |
            (processOutput
                ? NativeComputeScoringOutputValidity.ProcessIdentity
                : NativeComputeScoringOutputValidity.MemberCount) |
            (gpuOutput
                ? NativeComputeScoringOutputValidity.AdapterIdentity
                : NativeComputeScoringOutputValidity.None);
        if (row.StructSize != NativeComputeScoringSession.SizeOf<NativeComputeScoringOutput>()
            || row.ValidMask != expectedValidity
            || row.SchedulingGeneration != schedulingGeneration
            || row.SoftwareKey == 0
            || !double.IsFinite(row.Score)
            || row.Score < 0
            || (row.AdapterKey == 0) == gpuOutput
            || row.Flags != 0
            || row.Reserved0 != 0
            || row.Reserved[0] != 0
            || row.Reserved[1] != 0)
        {
            throw new InvalidOperationException("Native compute scoring emitted an invalid output row.");
        }

        if (processOutput)
        {
            if (row.TargetKey == 0
                || row.ProcessId == 0
                || row.ProcessStartKey == 0
                || row.MemberCount != 1
                || row.RuntimeState > NativeComputeScoringRuntimeState.NotRunning)
            {
                throw new InvalidOperationException("Native compute scoring emitted an invalid process output row.");
            }
        }
        else if (row.TargetKey != 0
            || row.ProcessId != 0
            || row.ProcessStartKey != 0
            || row.SourceIndex != 0
            || row.MemberCount == 0
            || row.RuntimeState != NativeComputeScoringRuntimeState.Unknown)
        {
            throw new InvalidOperationException("Native compute scoring emitted an invalid software output row.");
        }

        return new HostManagerComputeScore(
            row.Kind,
            row.SchedulingGeneration,
            row.TargetKey,
            row.SoftwareKey,
            checked((int)row.ProcessId),
            row.ProcessStartKey,
            row.AdapterKey,
            row.Score,
            row.MemberCount,
            row.RuntimeState);
    }
}
