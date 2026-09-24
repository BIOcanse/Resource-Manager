using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class SchedulingObservationContractTests
{
    [Fact]
    public void ProcessFacts_IdleProcessIsStructurallyExcludedFromGovernance()
    {
        Assert.True(WindowsResourceBreakdownSampler.IsStructurallyExcludedProcessId(0));
        Assert.True(WindowsResourceBreakdownSampler.IsStructurallyExcludedProcessId(-1));
        Assert.False(WindowsResourceBreakdownSampler.IsStructurallyExcludedProcessId(1));
    }

    [Fact]
    public void NativePartialSource_RemainsPartialInsteadOfClaimingRetainedLastGood()
    {
        Assert.Equal(
            SamplingObservationStatus.Partial,
            NativeMetricSnapshotCommittedProjection.ProjectSourceStatus(
                NativeMetricSnapshotSourceStatus.Partial));
    }

    [Fact]
    public void CompositeMemoryObservation_RequiresEveryMetricToBeCurrent()
    {
        Assert.Equal(
            SamplingObservationStatus.Current,
            NativeMetricSnapshotCommittedProjection.ProjectCompositeObservationStatus(
                NativeMetricSnapshotMetricStatus.Current,
                NativeMetricSnapshotMetricStatus.Current,
                NativeMetricSnapshotMetricStatus.Current));
        Assert.Equal(
            SamplingObservationStatus.RetainedLastGood,
            NativeMetricSnapshotCommittedProjection.ProjectCompositeObservationStatus(
                NativeMetricSnapshotMetricStatus.Current,
                NativeMetricSnapshotMetricStatus.Retained,
                NativeMetricSnapshotMetricStatus.Current));
        Assert.Equal(
            SamplingObservationStatus.Unavailable,
            NativeMetricSnapshotCommittedProjection.ProjectCompositeObservationStatus(
                NativeMetricSnapshotMetricStatus.Current,
                NativeMetricSnapshotMetricStatus.Unavailable,
                NativeMetricSnapshotMetricStatus.Current));
    }

    [Fact]
    public void GpuInventory_CurrentValidZeroValues_IsComplete()
    {
        var snapshot = CreateGpuInventory();

        Assert.True(snapshot.IsCurrentComplete());
        Assert.Equal(0, snapshot.Adapters[0].UsagePercent);
        Assert.Equal((ulong)0, snapshot.Adapters[0].UsedDedicatedMemoryBytes);
    }

    [Fact]
    public void GpuInventory_RetainedSkippedOrDuplicateIdentity_IsNotComplete()
    {
        var current = CreateGpuInventory();

        Assert.False((current with { Status = SamplingObservationStatus.RetainedLastGood }).IsCurrentComplete());
        Assert.False((current with { SkippedCount = 1 }).IsCurrentComplete());
        Assert.False((current with
        {
            ObservedCount = 2,
            Adapters = [current.Adapters[0], current.Adapters[0]]
        }).IsCurrentComplete());
    }

    [Fact]
    public void GpuInventory_ExplicitUnsupportedCapacity_IsComplete()
    {
        var snapshot = CreateGpuInventory() with
        {
            Adapters =
            [
                CreateGpuInventory().Adapters[0] with
                {
                    CapabilityMask = SchedulingGpuCapabilityMask.Usage,
                    ValidMetricMask = SchedulingGpuMetricMask.Usage,
                    CapacityStatus = SamplingObservationStatus.Unsupported,
                    TotalDedicatedMemoryBytes = 0
                }
            ]
        };

        Assert.True(snapshot.IsCurrentComplete());
    }

    [Fact]
    public void ProcessFacts_CurrentValidZeroValues_IsComplete()
    {
        var snapshot = CreateProcessFacts();

        Assert.True(snapshot.IsCurrentComplete());
        Assert.True(snapshot.IsInventoryCurrentComplete());
        Assert.True(snapshot.IsCpuCurrentComplete());
        Assert.True(snapshot.IsMemoryCurrentComplete());
        Assert.True(snapshot.IsGpuUsageCurrentComplete(CreateGpuInventory()));
        Assert.True(snapshot.IsGpuDedicatedMemoryCurrentComplete(CreateGpuInventory()));
        Assert.Equal(0, snapshot.Processes[0].CpuUsagePercent);
        Assert.Equal(0, snapshot.Processes[0].Gpus[0].UsagePercent);
    }

    [Fact]
    public void ProcessFacts_CpuCompletenessIsIndependentFromMemoryAndGpuDomains()
    {
        var current = CreateProcessFacts();
        var cpuOnly = current with
        {
            CurrentMetricMask = SchedulingProcessMetricMask.CpuUsage,
            Processes =
            [
                current.Processes[0] with
                {
                    ValidMetricMask = SchedulingProcessMetricMask.CpuUsage,
                    Gpus = []
                }
            ],
            GpuSourceGeneration = 0,
            GpuObservedAtUtcTicks = 0,
            GpuTopologyGeneration = 0,
            GpuTopologyFingerprint = 0
        };

        Assert.True(cpuOnly.IsInventoryCurrentComplete());
        Assert.True(cpuOnly.IsCpuCurrentComplete());
        Assert.False(cpuOnly.IsMemoryCurrentComplete());
        Assert.False(cpuOnly.IsCurrentComplete());
    }

    [Fact]
    public void DisplayMetrics_SettleIndependentlyAcrossSourceGenerationsAndTimestamps()
    {
        const ulong usedHandle = 1;
        const ulong totalHandle = 2;
        const ulong percentHandle = 3;
        var metricIds = new Dictionary<ulong, string>
        {
            [usedHandle] = "memory.usage",
            [totalHandle] = "memory.total",
            [percentHandle] = "memory.percent"
        }.ToImmutableDictionary();
        var catalog = new NativeMetricSnapshotCatalogProjection(
            ImmutableArray<NativeMetricSnapshotSourcePolicyInput>.Empty,
            ImmutableArray<NativeMetricSnapshotMetricDefinitionInput>.Empty,
            metricIds,
            metricIds.ToImmutableDictionary(
                static pair => pair.Value,
                static pair => pair.Key,
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<ulong, string>
            {
                [11] = "memory-used",
                [22] = "memory-total",
                [33] = "memory-percent"
            }.ToImmutableDictionary(),
            new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
            {
                ["memory-used"] = 11,
                ["memory-total"] = 22,
                ["memory-percent"] = 33
            }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary<ulong, NativeMetricSnapshotRuleCatalogBinding>.Empty,
            ImmutableDictionary<ulong, NativeMetricSnapshotGpuScopeBinding>.Empty,
            NativeMetricSnapshotCatalogHandleMap.Empty,
            1,
            "independent-item-test");
        var frame = new NativeMetricSnapshotCommittedFrame(
            new NativeMetricSnapshotSnapshotHeader
            {
                ConfigurationGeneration = 1,
                CatalogGeneration = 1,
                CommittedGeneration = 9,
                CapturedAtMilliseconds = 4_000
            },
            ImmutableArray<NativeMetricSnapshotSourceOutput>.Empty,
            [
                CurrentMetric(
                    usedHandle,
                    sourceHandle: 11,
                    sourceGeneration: 101,
                    observedAtMilliseconds: 1_000,
                    valueBits: 8UL * 1024 * 1024 * 1024,
                    NativeMetricSnapshotValueKind.Unsigned64),
                CurrentMetric(
                    totalHandle,
                    sourceHandle: 22,
                    sourceGeneration: 202,
                    observedAtMilliseconds: 2_000,
                    valueBits: 32UL * 1024 * 1024 * 1024,
                    NativeMetricSnapshotValueKind.Unsigned64),
                CurrentMetric(
                    percentHandle,
                    sourceHandle: 33,
                    sourceGeneration: 303,
                    observedAtMilliseconds: 3_000,
                    valueBits: BitConverter.DoubleToUInt64Bits(25),
                    NativeMetricSnapshotValueKind.Float64)
            ],
            ImmutableArray<NativeMetricSnapshotGpuInventoryOutput>.Empty,
            ImmutableArray<NativeMetricSnapshotRuleStateOutput>.Empty,
            catalog);

        var snapshot = NativeMetricSnapshotCommittedProjection.Create(
            frame,
            "CPU",
            "Memory",
            workspaceIdentity: 1);

        Assert.Equal(
            [
                "memory.percent",
                "memory.total",
                "memory.usage",
                SamplingDatasetIds.SystemGpuInventory
            ],
            snapshot.Datasets.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.All(
            snapshot.Datasets.Values.Where(static observation =>
                !observation.DatasetId.Equals(
                    SamplingDatasetIds.SystemGpuInventory,
                    StringComparison.OrdinalIgnoreCase)),
            static observation => Assert.Equal(
                SamplingObservationStatus.Current,
                observation.Status));
        Assert.Equal(101UL, snapshot.Datasets["memory.usage"].SourceGeneration);
        Assert.Equal(202UL, snapshot.Datasets["memory.total"].SourceGeneration);
        Assert.Equal(303UL, snapshot.Datasets["memory.percent"].SourceGeneration);
        Assert.Equal(
            SamplingObservationStatus.Current,
            snapshot.Memory.ObservationStatus);
        Assert.DoesNotContain("system.memory.usage", snapshot.Datasets.Keys);
    }

    [Fact]
    public void CompositeMemoryUsage_DerivesPercentWithoutSubscribingToPercentMetric()
    {
        const ulong usedHandle = 1;
        const ulong totalHandle = 2;
        var metricIds = new Dictionary<ulong, string>
        {
            [usedHandle] = "memory.usage",
            [totalHandle] = "memory.total"
        }.ToImmutableDictionary();
        var catalog = new NativeMetricSnapshotCatalogProjection(
            ImmutableArray<NativeMetricSnapshotSourcePolicyInput>.Empty,
            ImmutableArray<NativeMetricSnapshotMetricDefinitionInput>.Empty,
            metricIds,
            metricIds.ToImmutableDictionary(
                static pair => pair.Value,
                static pair => pair.Key,
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<ulong, string>
            {
                [11] = "memory-used",
                [22] = "memory-total"
            }.ToImmutableDictionary(),
            new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
            {
                ["memory-used"] = 11,
                ["memory-total"] = 22
            }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary<ulong, NativeMetricSnapshotRuleCatalogBinding>.Empty,
            ImmutableDictionary<ulong, NativeMetricSnapshotGpuScopeBinding>.Empty,
            NativeMetricSnapshotCatalogHandleMap.Empty,
            1,
            "memory-composite-test");
        var frame = new NativeMetricSnapshotCommittedFrame(
            new NativeMetricSnapshotSnapshotHeader
            {
                ConfigurationGeneration = 1,
                CatalogGeneration = 1,
                CommittedGeneration = 2,
                CapturedAtMilliseconds = 2_000
            },
            ImmutableArray<NativeMetricSnapshotSourceOutput>.Empty,
            [
                CurrentMetric(
                    usedHandle,
                    sourceHandle: 11,
                    sourceGeneration: 101,
                    observedAtMilliseconds: 1_000,
                    valueBits: 8UL * 1024 * 1024 * 1024,
                    NativeMetricSnapshotValueKind.Unsigned64),
                CurrentMetric(
                    totalHandle,
                    sourceHandle: 22,
                    sourceGeneration: 202,
                    observedAtMilliseconds: 1_000,
                    valueBits: 32UL * 1024 * 1024 * 1024,
                    NativeMetricSnapshotValueKind.Unsigned64)
            ],
            ImmutableArray<NativeMetricSnapshotGpuInventoryOutput>.Empty,
            ImmutableArray<NativeMetricSnapshotRuleStateOutput>.Empty,
            catalog);

        var snapshot = NativeMetricSnapshotCommittedProjection.Create(
            frame,
            "CPU",
            "Memory",
            workspaceIdentity: 1);

        Assert.True(snapshot.Memory.IsUsageAvailable);
        Assert.Equal(25, snapshot.Memory.UsagePercent);
        Assert.Equal(
            SamplingObservationStatus.Current,
            snapshot.Memory.ObservationStatus);
        var item = snapshot.Items["memory.usage"];
        Assert.Equal(25, item.Percent);
        // 容量类指标发的是原始字节：不拼显示串，也不写死单位标签。
        // 换算与标签由前端按用户选择的进制统一给出（见 ClientApp byteUnitsContract）。
        Assert.Equal(string.Empty, item.DisplayValue);
        Assert.Equal(MetricUnits.Bytes, item.Unit);
        Assert.Equal(8d * 1024 * 1024 * 1024, item.NumericValue);
        Assert.Equal(32d * 1024 * 1024 * 1024, item.Total);
        Assert.DoesNotContain("memory.percent", snapshot.Items.Keys);
        Assert.Equal(
            SamplingObservationStatus.Current,
            snapshot.Datasets["memory.usage"].Status);
    }

    [Fact]
    public void ProcessCpu_FirstBaselineWithNoValidRowsIsStillACurrentEmptyDataset()
    {
        var request = new SchedulingProcessFactRequest(
            SchedulingProcessMetricMask.CpuUsage,
            null,
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []));
        var process = CreateProcessFacts().Processes[0] with
        {
            ValidMetricMask = SchedulingProcessMetricMask.None,
            CpuUsagePercent = 0,
            Gpus = []
        };

        var current = WindowsResourceBreakdownSampler.ResolveCurrentMetricMask(
            request,
            [process],
            SchedulingProcessGpuRead.NotRequested,
            WindowsProcessRuntimeStateSnapshot.NotRequested);

        Assert.Equal(SchedulingProcessMetricMask.CpuUsage, current);
    }

    [Fact]
    public void ProcessMetricReadersFailIndependently()
    {
        Assert.False(WindowsResourceBreakdownSampler.TryReadProcessLong(
            static () => throw new InvalidOperationException("working set unavailable"),
            out var missingWorkingSet));
        Assert.True(WindowsResourceBreakdownSampler.TryReadProcessLong(
            static () => 4_096,
            out var privateCommit));
        Assert.False(WindowsResourceBreakdownSampler.TryReadProcessTimeSpan(
            static () => throw new NotSupportedException("processor time unavailable"),
            out var missingProcessorTime));

        Assert.Equal(0, missingWorkingSet);
        Assert.Equal(4_096, privateCommit);
        Assert.Equal(TimeSpan.Zero, missingProcessorTime);
    }

    [Fact]
    public void ProcessGpuDomainsBecomeCurrentIndependently()
    {
        var request = new SchedulingProcessFactRequest(
            SchedulingProcessMetricMask.GpuUsage
                | SchedulingProcessMetricMask.GpuDedicatedMemory,
            null,
            CreateGpuInventory());
        var usageOnly = new SchedulingProcessGpuRead(
            SamplingObservationStatus.Current,
            SamplingObservationStatus.RetainedLastGood,
            11,
            DateTimeOffset.UtcNow.UtcTicks,
            10,
            DateTimeOffset.UtcNow.AddSeconds(-5).UtcTicks,
            new Dictionary<
                (ulong AdapterKey, int ProcessId, ulong ProcessStartKey),
                double>(),
            new Dictionary<
                (ulong AdapterKey, int ProcessId, ulong ProcessStartKey),
                double>());
        var memoryOnly = usageOnly with
        {
            UsageStatus = SamplingObservationStatus.RetainedLastGood,
            DedicatedMemoryStatus = SamplingObservationStatus.Current,
            UsageGeneration = 10,
            DedicatedMemoryGeneration = 12
        };

        var usageCurrent = WindowsResourceBreakdownSampler
            .ResolveCurrentMetricMask(
                request,
                [],
                usageOnly,
                WindowsProcessRuntimeStateSnapshot.NotRequested);
        var memoryCurrent = WindowsResourceBreakdownSampler
            .ResolveCurrentMetricMask(
                request,
                [],
                memoryOnly,
                WindowsProcessRuntimeStateSnapshot.NotRequested);

        Assert.Equal(
            SchedulingProcessMetricMask.GpuUsage,
            usageCurrent);
        Assert.Equal(
            SchedulingProcessMetricMask.GpuDedicatedMemory,
            memoryCurrent);
    }

    [Fact]
    public void RuntimeState_IncompleteWindowOwnershipOmitsOnlyUnprovenProcesses()
    {
        var visible = new ProcessInstanceKey(101, 1_001);
        var unproven = new ProcessInstanceKey(102, 1_002);

        var snapshot = WindowsProcessRuntimeStateSnapshot.Project(
            [visible, unproven],
            foregroundProcessId: null,
            new Dictionary<int, ProcessWindowPresence>
            {
                [visible.ProcessId] = new(
                    HasVisibleWindow: true,
                    HasBackgroundWindow: false,
                    HasHiddenWindow: false)
            },
            ownershipIncomplete: true,
            static _ => true);

        Assert.Equal(SamplingObservationStatus.Current, snapshot.Status);
        var fact = Assert.Single(snapshot.Processes).Value;
        Assert.Equal(HostManagerRuntimeStates.ForegroundUnfocused, fact.RuntimeState);
        Assert.True(fact.HasVisibleWindow);
        Assert.DoesNotContain(unproven, snapshot.Processes.Keys);
    }

    [Fact]
    public void RuntimeState_CompleteEnumerationCanProveBackgroundProcess()
    {
        var background = new ProcessInstanceKey(103, 1_003);

        var snapshot = WindowsProcessRuntimeStateSnapshot.Project(
            [background],
            foregroundProcessId: null,
            new Dictionary<int, ProcessWindowPresence>(),
            ownershipIncomplete: false,
            static _ => true);

        var fact = Assert.Single(snapshot.Processes).Value;
        Assert.Equal(HostManagerRuntimeStates.BackgroundProcess, fact.RuntimeState);
        Assert.False(fact.HasVisibleWindow);
    }

    [Fact]
    public void ProcessFacts_GpuAcceptsIndependentlyRefreshedUnchangedInventory()
    {
        var inventory = CreateGpuInventory();
        var refreshed = inventory with
        {
            Generation = inventory.Generation + 1,
            ObservedAtUtcTicks = inventory.ObservedAtUtcTicks + 1,
            Adapters = inventory.Adapters.Select(adapter => adapter with
            {
                Generation = inventory.Generation + 1,
                ObservedAtUtcTicks = inventory.ObservedAtUtcTicks + 1
            }).ToArray()
        };

        Assert.True(refreshed.IsCurrentComplete());
        Assert.True(CreateProcessFacts().IsGpuUsageCurrentComplete(refreshed));
        Assert.True(CreateProcessFacts().IsGpuDedicatedMemoryCurrentComplete(refreshed));
    }

    [Fact]
    public void ProcessFacts_GpuCompletenessRejectsTopologyDrift()
    {
        var current = CreateProcessFacts();

        Assert.False((current with
        {
            DatasetObservations = current.DatasetObservations.ToDictionary(
                static pair => pair.Key,
                pair => pair.Key == SchedulingProcessMetricMask.GpuUsage
                    ? pair.Value with { TopologyGeneration = 10 }
                    : pair.Value)
        }).IsGpuUsageCurrentComplete(CreateGpuInventory()));
        Assert.False((current with
        {
            DatasetObservations = current.DatasetObservations.ToDictionary(
                static pair => pair.Key,
                pair => pair.Key == SchedulingProcessMetricMask.GpuUsage
                    ? pair.Value with { TopologyFingerprint = 18 }
                    : pair.Value)
        }).IsGpuUsageCurrentComplete(CreateGpuInventory()));
    }

    [Fact]
    public void ProcessFacts_GpuUsageAndVramKeepIndependentSourceGenerations()
    {
        var current = CreateProcessFacts();
        var process = Assert.Single(current.Processes);
        var gpu = Assert.Single(process.Gpus);
        var splitGpu = gpu with
        {
            DedicatedMemorySourceGeneration = 42,
            DedicatedMemoryTopologyGeneration = 9
        };
        var split = current with
        {
            Processes = [process with { Gpus = [splitGpu] }],
            DatasetObservations = current.DatasetObservations.ToDictionary(
                static pair => pair.Key,
                pair => pair.Key ==
                        SchedulingProcessMetricMask.GpuDedicatedMemory
                    ? pair.Value with
                    {
                        SourceGeneration = 42,
                        ObservedAtUtcTicks = 44,
                        LastAttemptAtUtcTicks = 44,
                        LastSuccessAtUtcTicks = 44
                    }
                    : pair.Value)
        };

        Assert.True(split.IsGpuUsageCurrentComplete(CreateGpuInventory()));
        Assert.True(split.IsGpuDedicatedMemoryCurrentComplete(
            CreateGpuInventory()));
        Assert.Equal(41UL, splitGpu.UsageSourceGeneration);
        Assert.Equal(42UL, splitGpu.DedicatedMemorySourceGeneration);
    }

    [Fact]
    public void ProcessFacts_GpuCompletenessRejectsAdapterIndexOrStableKeyDrift()
    {
        var current = CreateProcessFacts();
        var fact = current.Processes[0].Gpus[0];

        var indexDrift = current with
        {
            Processes =
            [
                current.Processes[0] with
                {
                    Gpus = [fact with { GpuIndex = 1 }]
                }
            ]
        };
        var keyDrift = current with
        {
            Processes =
            [
                current.Processes[0] with
                {
                    Gpus = [fact with { AdapterKey = 24 }]
                }
            ]
        };

        Assert.False(indexDrift.IsGpuUsageCurrentComplete(CreateGpuInventory()));
        Assert.False(keyDrift.IsGpuUsageCurrentComplete(CreateGpuInventory()));
    }

    [Fact]
    public void ProcessFacts_GpuCompletenessRejectsUnknownExtraAdapterFacts()
    {
        var current = CreateProcessFacts();
        var fact = current.Processes[0].Gpus[0];
        var withUnknownAdapter = current with
        {
            Processes =
            [
                current.Processes[0] with
                {
                    Gpus =
                    [
                        fact,
                        fact with { GpuIndex = 1, AdapterKey = 24 }
                    ]
                }
            ]
        };

        Assert.False(withUnknownAdapter.IsGpuUsageCurrentComplete(CreateGpuInventory()));
    }

    [Fact]
    public void ProcessFacts_SkipOverflowOrMissingCurrentMask_IsNotComplete()
    {
        var current = CreateProcessFacts();

        Assert.False((current with { SkippedCount = 1 }).IsCurrentComplete());
        Assert.False((current with { OverflowCount = 1 }).IsCurrentComplete());
        Assert.False((current with
        {
            CurrentMetricMask = current.CurrentMetricMask & ~SchedulingProcessMetricMask.CpuUsage
        }).IsCurrentComplete());
    }

    [Fact]
    public void ProcessFacts_ExcludedUngovernableProcesses_PreserveCompleteGovernableInventory()
    {
        var current = CreateProcessFacts();
        var withIdleProcessExcluded = current with
        {
            EnumeratedCount = 2,
            ExcludedCount = 1
        };

        Assert.True(withIdleProcessExcluded.IsInventoryCurrentComplete());
        Assert.True(withIdleProcessExcluded.IsCurrentComplete());
    }

    [Fact]
    public void ProcessFacts_InventoryAccountingMismatch_IsNotComplete()
    {
        var current = CreateProcessFacts();

        Assert.False((current with
        {
            EnumeratedCount = 2,
            ExcludedCount = 0
        }).IsInventoryCurrentComplete());
        Assert.False((current with
        {
            EnumeratedCount = 1,
            ExcludedCount = 1
        }).IsInventoryCurrentComplete());
    }

    [Fact]
    public void ProcessFacts_DuplicateProcessIdentity_IsNotComplete()
    {
        var current = CreateProcessFacts();

        Assert.False((current with
        {
            EnumeratedCount = 2,
            EmittedCount = 2,
            Processes = [current.Processes[0], current.Processes[0]]
        }).IsCurrentComplete());
    }

    private static NativeMetricSnapshotMetricOutput CurrentMetric(
        ulong metricHandle,
        ulong sourceHandle,
        ulong sourceGeneration,
        ulong observedAtMilliseconds,
        ulong valueBits,
        NativeMetricSnapshotValueKind valueKind)
        => new()
        {
            MetricHandle = metricHandle,
            SourceHandle = sourceHandle,
            SourceGeneration = sourceGeneration,
            ObservedAtMilliseconds = observedAtMilliseconds,
            ValueBits = valueBits,
            ValueKind = (uint)valueKind,
            Status = (uint)NativeMetricSnapshotMetricStatus.Current
        };

    private static SchedulingGpuInventorySnapshot CreateGpuInventory()
    {
        const ulong generation = 9;
        const long observedAtUtcTicks = 11;
        return new SchedulingGpuInventorySnapshot(
            SamplingObservationStatus.Current,
            generation,
            observedAtUtcTicks,
            1,
            0,
            0,
            17,
            [
                new SchedulingGpuAdapterObservation(
                    0,
                    23,
                    SchedulingGpuCapabilityMask.Usage | SchedulingGpuCapabilityMask.DedicatedMemory,
                    SchedulingGpuMetricMask.Usage
                        | SchedulingGpuMetricMask.UsedDedicatedMemory
                        | SchedulingGpuMetricMask.TotalDedicatedMemory,
                    SamplingObservationStatus.Current,
                    SamplingObservationStatus.Current,
                    0,
                    0,
                    8UL * 1024 * 1024 * 1024,
                    generation,
                    observedAtUtcTicks)
            ]);
    }

    private static SchedulingProcessFactSnapshot CreateProcessFacts()
    {
        const SchedulingProcessMetricMask allMetrics =
            SchedulingProcessMetricMask.CpuUsage
            | SchedulingProcessMetricMask.MemoryUsage
            | SchedulingProcessMetricMask.GpuUsage
            | SchedulingProcessMetricMask.GpuDedicatedMemory;
        return new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Current,
            31,
            37,
            1,
            1,
            0,
            0,
            allMetrics,
            allMetrics,
            [
                new SchedulingProcessFact(
                    123,
                    456,
                    "process",
                    null,
                    "software",
                    "Software",
                    "Other",
                    "Other",
                    0,
                    allMetrics,
                    0,
                    0,
                    31,
                    [
                        new SchedulingProcessGpuFact(
                            0,
                            23,
                            SchedulingProcessMetricMask.GpuUsage
                                | SchedulingProcessMetricMask.GpuDedicatedMemory,
                            0,
                            0,
                            41,
                            41,
                            9,
                            9)
                    ])
            ],
            41,
            43,
            9,
            17)
        {
            DatasetObservations = new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>
            {
                [SchedulingProcessMetricMask.CpuUsage] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.CpuUsage,
                        31,
                        37,
                        31,
                        37),
                [SchedulingProcessMetricMask.MemoryUsage] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.MemoryUsage,
                        31,
                        37,
                        31,
                        37,
                        memoryUsageDependency:
                            TestSystemMemoryUsageDependency.Create(
                                sourceGeneration: 31,
                                observedAtUtcTicks: 37,
                                committedGeneration: 31)),
                [SchedulingProcessMetricMask.GpuUsage] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.GpuUsage,
                        41,
                        43,
                        31,
                        37,
                        topologyGeneration: 9,
                        topologyFingerprint: 17),
                [SchedulingProcessMetricMask.GpuDedicatedMemory] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.GpuDedicatedMemory,
                        41,
                        43,
                        31,
                        37,
                        topologyGeneration: 9,
                        topologyFingerprint: 17)
            }
        };
    }
}
