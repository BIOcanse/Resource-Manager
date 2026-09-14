using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class OptimizationEnhancementBatchExecutionTests
{
    private const int ProcessId = 4242;
    private const string ProcessName = "batch-target";
    private const string ExecutablePath = @"D:\Apps\batch-target.exe";
    private const string ReportId = "report:batch-target";
    private const string TargetKey = "software:batch-target";

    [Fact]
    public async Task A1ApplyReadsOneExecutionSnapshotAndBatchesPriorityWithPower()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var snapshot = CreateSnapshot(startedAt, "Normal", affinityMask: 15, powerControl: 1, powerState: 1);
        var writer = new RecordingPolicyWriter([snapshot, snapshot], CreateSuccessfulFields);
        var store = new RecordingA1Store();
        var service = CreateA1Service(writer, store, snapshot);

        var result = await service.ApplyAsync(
            new OptimizationA1Request(ReportId, ConfirmOperation: true),
            CancellationToken.None);

        Assert.Equal(2, writer.ReadCount);
        Assert.Equal(1, writer.BatchCallCount);
        var request = Assert.Single(writer.Requests);
        Assert.Equal(ProcessId, request.ProcessId);
        Assert.Equal(startedAt, request.ExpectedStartedAt);
        Assert.Equal("High", request.PriorityClass);
        Assert.Null(request.AffinityMask);
        Assert.Equal(1u, request.PowerControlMask);
        Assert.Equal(0u, request.PowerStateMask);

        var record = Assert.IsType<OptimizationA1Record>(result.Record);
        Assert.Equal(
            new[]
            {
                OptimizationA1ActionKinds.RaiseProcessPriority,
                OptimizationA1ActionKinds.DisableExecutionSpeedThrottling
            },
            record.Actions.Select(static action => action.Kind).ToArray());
        Assert.Equal("Normal", record.Actions[0].PreviousRawValue);
        Assert.Equal("High", record.Actions[0].AppliedRawValue);
        Assert.Equal("control=1;state=1", record.Actions[1].PreviousRawValue);
        Assert.Equal("control=1;state=0", record.Actions[1].AppliedRawValue);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task A2ApplyUsesOneBatchAndRecordsOnlySuccessfulFieldsInActionOrder()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var targetAffinity = GetA2TargetAffinityMask();
        var currentAffinity = targetAffinity == 1 ? 3 : 1;
        var snapshot = CreateSnapshot(startedAt, "Normal", currentAffinity, powerControl: 1, powerState: 1);
        var writer = new RecordingPolicyWriter(
            [snapshot, snapshot],
            static _ =>
            [
                new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.PowerThrottling,
                    false,
                    "power failed"),
                new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.AffinityMask,
                    true,
                    "affinity ok"),
                new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.PriorityClass,
                    true,
                    "priority ok")
            ]);
        var store = new RecordingA2Store();
        var service = CreateA2Service(writer, store, snapshot);

        var result = await service.ApplyAsync(
            new OptimizationA2Request(ReportId, ConfirmOperation: true),
            CancellationToken.None);

        Assert.Equal(2, writer.ReadCount);
        Assert.Equal(1, writer.BatchCallCount);
        var request = Assert.Single(writer.Requests);
        Assert.Equal(ProcessId, request.ProcessId);
        Assert.Equal(startedAt, request.ExpectedStartedAt);
        Assert.Equal("High", request.PriorityClass);
        Assert.Equal(targetAffinity, request.AffinityMask);
        Assert.Equal(1u, request.PowerControlMask);
        Assert.Equal(0u, request.PowerStateMask);

        var record = Assert.IsType<OptimizationA2Record>(result.Record);
        Assert.Equal(
            new[]
            {
                OptimizationA2ActionKinds.RaiseProcessPriority,
                OptimizationA2ActionKinds.MoveTargetAffinity
            },
            record.Actions.Select(static action => action.Kind).ToArray());
        Assert.Equal("Normal", record.Actions[0].PreviousRawValue);
        Assert.Equal(currentAffinity.ToString(), record.Actions[1].PreviousRawValue);
        Assert.Equal(targetAffinity.ToString(), record.Actions[1].AppliedRawValue);
        Assert.DoesNotContain(
            record.Actions,
            static action => action.Kind == OptimizationA2ActionKinds.DisableExecutionSpeedThrottling);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task A1ApplyRejectsAReusedProcessIdentityBeforeBatching()
    {
        var previewStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var previewSnapshot = CreateSnapshot(
            previewStartedAt,
            "Normal",
            affinityMask: 15,
            powerControl: 1,
            powerState: 1);
        var reusedPidSnapshot = previewSnapshot with { StartedAt = previewStartedAt.AddSeconds(5) };
        var writer = new RecordingPolicyWriter([previewSnapshot, reusedPidSnapshot], CreateSuccessfulFields);
        var service = CreateA1Service(writer, new RecordingA1Store(), previewSnapshot);

        var result = await service.ApplyAsync(
            new OptimizationA1Request(ReportId, ConfirmOperation: true),
            CancellationToken.None);

        Assert.Equal(2, writer.ReadCount);
        Assert.Equal(0, writer.BatchCallCount);
        Assert.Equal(OptimizationA1RecordStates.NoChanges, result.State);
        Assert.Null(result.Record);
    }

    [Fact]
    public async Task A2ApplyRevalidatesDirectionAndCurrentValuesBeforeBatching()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var targetAffinity = GetA2TargetAffinityMask();
        var currentAffinity = targetAffinity == 1 ? 3 : 1;
        var previewSnapshot = CreateSnapshot(
            startedAt,
            "Normal",
            currentAffinity,
            powerControl: 1,
            powerState: 1);
        var changedSnapshot = CreateSnapshot(
            startedAt,
            "RealTime",
            targetAffinity,
            powerControl: 1,
            powerState: 0);
        var writer = new RecordingPolicyWriter([previewSnapshot, changedSnapshot], CreateSuccessfulFields);
        var service = CreateA2Service(writer, new RecordingA2Store(), previewSnapshot);

        var result = await service.ApplyAsync(
            new OptimizationA2Request(ReportId, ConfirmOperation: true),
            CancellationToken.None);

        Assert.Equal(2, writer.ReadCount);
        Assert.Equal(0, writer.BatchCallCount);
        Assert.Equal(OptimizationA2RecordStates.NoChanges, result.State);
        Assert.Null(result.Record);
    }

    private static OptimizationA1Service CreateA1Service(
        IProcessResourcePolicyWriter writer,
        IOptimizationA1Store store,
        ProcessResourcePolicySnapshot snapshot)
    {
        var report = CreateReport();
        var services = new StubOptimizationReportService(report);
        return new OptimizationA1Service(
            services,
            services,
            new StubResourceBreakdownSampler(CreateBreakdownSnapshot(snapshot)),
            store,
            writer,
            new LegacyGpuPreferenceActionRestorer(new ThrowingGraphicsPreferenceStore()),
            NullLogger<OptimizationA1Service>.Instance);
    }

    private static OptimizationA2Service CreateA2Service(
        IProcessResourcePolicyWriter writer,
        IOptimizationA2Store store,
        ProcessResourcePolicySnapshot snapshot)
    {
        var report = CreateReport();
        var services = new StubOptimizationReportService(report);
        return new OptimizationA2Service(
            services,
            services,
            new StubResourceBreakdownSampler(CreateBreakdownSnapshot(snapshot)),
            store,
            writer,
            new LegacyGpuPreferenceActionRestorer(new ThrowingGraphicsPreferenceStore()),
            NullLogger<OptimizationA2Service>.Instance);
    }

    private static ProcessResourcePolicySnapshot CreateSnapshot(
        DateTimeOffset startedAt,
        string priorityClass,
        long affinityMask,
        uint powerControl,
        uint powerState)
    {
        return new ProcessResourcePolicySnapshot(
            ProcessId,
            ProcessName,
            ExecutablePath,
            startedAt,
            priorityClass,
            affinityMask,
            Environment.ProcessorCount,
            PowerThrottlingControlMask: powerControl,
            PowerThrottlingStateMask: powerState,
            PowerThrottlingRawValue: $"control={powerControl};state={powerState}",
            PowerThrottlingDisplayValue: powerState == 0
                ? "execution speed throttling disabled"
                : "execution speed throttling enabled");
    }

    private static OptimizationReportItem CreateReport()
    {
        var now = DateTimeOffset.UtcNow;
        return new OptimizationReportItem(
            ReportId,
            OptimizationReportTypes.GameBackgroundResourceUsage,
            OptimizationReportStates.Active,
            OptimizationSeverity.Warning,
            OptimizationConfidence.High,
            now,
            now,
            now,
            now,
            "Batch target",
            "Batch target",
            OptimizationActivityContext.Unknown(),
            new OptimizationReportTarget(
                OptimizationReportTargetTypes.Software,
                TargetKey,
                "Batch target",
                TargetKey,
                "Batch target",
                SoftwareKinds.Game,
                SoftwareKinds.Game,
                [ProcessName],
                [ProcessId],
                null),
            new OptimizationReportEvidence(
                OptimizationResourceKinds.Cpu,
                10,
                20,
                15,
                "10%",
                "20%",
                "15%",
                1,
                1,
                5,
                []),
            []);
    }

    private static ResourceBreakdownSnapshot CreateBreakdownSnapshot(
        ProcessResourcePolicySnapshot process)
    {
        return new ResourceBreakdownSnapshot(
            DateTimeOffset.UtcNow,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.CpuUsage,
                    "CPU",
                    "%",
                    ResourceBreakdownScaleModes.Capacity,
                    10,
                    100,
                    10,
                    "10%",
                    [
                        new ResourceSoftwareSegment(
                            TargetKey,
                            "Batch target",
                            SoftwareKinds.Game,
                            SoftwareKinds.Game,
                            10,
                            10,
                            "10%",
                            1,
                            [
                                new ResourceProcessSegment(
                                    process.ProcessId,
                                    process.ProcessName,
                                    process.ExecutablePath,
                                    10,
                                    10,
                                    100,
                                    "10%")
                            ])
                    ])
            ]);
    }

    private static long GetA2TargetAffinityMask()
    {
        var plan = CpuAffinityPlanner.CreatePrimaryLogicalProcessorPlan(Environment.ProcessorCount);
        Assert.True(plan.Available, plan.DisabledReason);
        return plan.AffinityMask
            ?? throw new InvalidOperationException("The A2 test requires an affinity-mask plan.");
    }

    private static IReadOnlyList<ProcessResourcePolicyBatchFieldResult> CreateSuccessfulFields(
        ProcessResourcePolicyBatchRequest request)
    {
        var fields = new List<ProcessResourcePolicyBatchFieldResult>();
        Add(request.PriorityClass is not null, ProcessResourcePolicyBatchFields.PriorityClass);
        Add(request.AffinityMask is not null, ProcessResourcePolicyBatchFields.AffinityMask);
        Add(request.PowerControlMask is not null, ProcessResourcePolicyBatchFields.PowerThrottling);
        return fields;

        void Add(bool included, ProcessResourcePolicyBatchFields field)
        {
            if (included)
            {
                fields.Add(new ProcessResourcePolicyBatchFieldResult(field, true, $"ok:{field}"));
            }
        }
    }

    private sealed class RecordingPolicyWriter : IProcessResourcePolicyWriter
    {
        private readonly Queue<ProcessResourcePolicySnapshot?> snapshots;
        private readonly Func<
            ProcessResourcePolicyBatchRequest,
            IReadOnlyList<ProcessResourcePolicyBatchFieldResult>> createFields;

        public RecordingPolicyWriter(
            IEnumerable<ProcessResourcePolicySnapshot?> snapshots,
            Func<
                ProcessResourcePolicyBatchRequest,
                IReadOnlyList<ProcessResourcePolicyBatchFieldResult>> createFields)
        {
            this.snapshots = new Queue<ProcessResourcePolicySnapshot?>(snapshots);
            this.createFields = createFields;
        }

        public int ReadCount { get; private set; }

        public int BatchCallCount { get; private set; }

        public IReadOnlyList<ProcessResourcePolicyBatchRequest> Requests { get; private set; } = [];

        public ProcessResourcePolicySnapshot? TryReadProcess(int processId)
        {
            ReadCount++;
            if (processId != ProcessId || snapshots.Count == 0)
            {
                throw new InvalidOperationException("The enhancement path performed an unexpected process snapshot read.");
            }

            return snapshots.Dequeue();
        }

        public RecoveryReadResult<ProcessPlacementRecoverySnapshot> ReadPlacementStateForRecovery(
            int processId,
            ProcessPlacementReadFields requiredFields)
            => RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Unavailable(0, "Recovery read is outside this enhancement test.");

        public RecoveryReadResult<ThreadCpuSetPolicySnapshot> ReadThreadPlacementStateForRecovery(
            int processId,
            int threadId)
            => RecoveryReadResult<ThreadCpuSetPolicySnapshot>.Unavailable(0, "Recovery read is outside this enhancement test.");

        public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
            IReadOnlyList<ProcessResourcePolicyBatchRequest> requests)
        {
            BatchCallCount++;
            Requests = requests.ToArray();
            return requests
                .Select(request => new ProcessResourcePolicyBatchWriteResult(
                    request.ProcessId,
                    createFields(request)))
                .ToArray();
        }

        public DateTimeOffset? TryReadProcessStartedAt(int processId) => throw Unexpected();

        public RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadProcessInstanceForRecovery(
            int processId)
            => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetPriorityClass(int processId, string priorityClass) => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(int processId, long affinityMask) => throw Unexpected();

        public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int processId) => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(
            int processId,
            IReadOnlyList<uint> cpuSetIds) => throw Unexpected();

        public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(int processId, int threadId) => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
            int processId,
            int threadId,
            DateTimeOffset expectedCreatedAt,
            IReadOnlyList<uint> cpuSetIds) => throw Unexpected();

        public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(int processId) => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
            int processId,
            DateTimeOffset expectedStartedAt,
            uint expectedCurrentMemoryPriority,
            uint memoryPriority) => throw Unexpected();

        public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(int processId) => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetPowerThrottling(
            int processId,
            uint controlMask,
            uint stateMask) => throw Unexpected();

        public ProcessResourcePolicyWriteResult TryTrimWorkingSet(int processId) => throw Unexpected();

        private static InvalidOperationException Unexpected()
        {
            return new InvalidOperationException("The enhancement batch path used a legacy per-field writer call.");
        }
    }

    private sealed class RecordingA1Store : IOptimizationA1Store
    {
        private IReadOnlyList<OptimizationA1Record> records = [];

        public int SaveCount { get; private set; }

        public Task<IReadOnlyList<OptimizationA1Record>> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(records);
        }

        public Task SaveAsync(
            IReadOnlyList<OptimizationA1Record> records,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            this.records = records.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingA2Store : IOptimizationA2Store
    {
        private IReadOnlyList<OptimizationA2Record> records = [];

        public int SaveCount { get; private set; }

        public Task<IReadOnlyList<OptimizationA2Record>> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(records);
        }

        public Task SaveAsync(
            IReadOnlyList<OptimizationA2Record> records,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            this.records = records.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class StubResourceBreakdownSampler(ResourceBreakdownSnapshot snapshot)
        : IResourceBreakdownSampler
    {
        public Task<ResourceBreakdownSnapshot> GetSnapshotAsync(
            ResourceBreakdownSampleRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(snapshot);
        }

        public Task<ResourceBreakdownSnapshot> GetSnapshotAsync(
            IReadOnlyList<string> metricIds,
            IReadOnlyDictionary<string, string> scaleModes,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(snapshot);
        }

        public Task<ResourceBreakdownSnapshot> CaptureSnapshotAsync(
            ResourceBreakdownSampleRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(snapshot);
        }
    }

    private sealed class StubOptimizationReportService
        : IHostManagerReportService, IOptimizationProtectionService
    {
        private readonly OptimizationReportOverview overview;

        public StubOptimizationReportService(OptimizationReportItem report)
        {
            overview = new OptimizationReportOverview(
                DateTimeOffset.UtcNow,
                [report],
                [],
                [],
                new OptimizationRecorderStatus(null, null, 1, 1, 1, 0, 0, 5));
        }

        public Task<OptimizationReportOverview> GetReportsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(overview);
        }

        public Task<OptimizationReportOverview> RefreshReportsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(overview);
        }

        public Task<OptimizationReportItem?> GetReportAsync(
            string reportId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<OptimizationReportItem?>(
                overview.Reports.FirstOrDefault(report =>
                    report.Id.Equals(
                        reportId,
                        StringComparison.OrdinalIgnoreCase)));
        }

        public Task<TrustedOptimizationTarget?> DismissReportAsync(
            string reportId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TrustedOptimizationTarget?> TrustReportAsync(
            string reportId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProtectedOptimizationTarget?> ProtectReportAsync(
            OptimizationReportItem report,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TrustedOptimizationTarget>> GetTrustedTargetsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProtectedOptimizationTarget>> GetProtectedTargetsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TrustedOptimizationTarget>> RefreshTrustedTargetsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProtectedOptimizationTarget>> RefreshProtectedTargetsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RemoveTrustedTargetAsync(
            string trustId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RemoveProtectedTargetAsync(
            string protectionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProtectedOptimizationTarget?> UpdateProtectedTargetLevelAsync(
            string protectionId,
            int protectionLevel,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsTargetProtectedAsync(
            OptimizationReportTarget target,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public OptimizationRecorderStatus GetStatus() => overview.Status;

        public int ProtectedTargetCount => 0;
    }

    private sealed class ThrowingGraphicsPreferenceStore : IWindowsGraphicsPreferenceStore
    {
        public RecoveryReadResult<string> ReadValueForRecovery(string executablePath)
            => RecoveryReadResult<string>.Unavailable(0, "Recovery read is outside this enhancement test.");

        public void WriteValue(string executablePath, string value) => throw new NotSupportedException();

        public void DeleteValue(string executablePath) => throw new NotSupportedException();

        public string BuildPreferIntegratedGpuValue(string? currentValue) => throw new NotSupportedException();

        public string BuildPreferHighPerformanceGpuValue(string? currentValue) => throw new NotSupportedException();

        public string DescribePreference(string? value) => throw new NotSupportedException();
    }
}
