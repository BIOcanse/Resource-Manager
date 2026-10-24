using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task DefaultAllCoresBalancesThreadHintsWithoutRestrictingCpuSetsAndRestoresThem()
    {
        const int pid = 42_430;
        const ulong startKey = 133_900_000_000_000_030;
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                pid, startKey, "software:unbounded-default", 80, 50, 25),
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.CpuOnly,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => CpuCoreResidencyTestValues.Create(
                95, DateTimeOffset.UtcNow, CpuCoreResidencyTestValues.Process(pid, startKey, ("core:0", 50)))),
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology);
        var threads = new Dictionary<int, ThreadIdealProcessorPolicySnapshot>
        {
            [10001] = new(pid, startKey, 10001, startKey + 1, new(0, 0), 0, 15, []),
            [10002] = new(pid, startKey, 10002, startKey + 2, new(0, 0), 0, 15, [])
        };
        fixture.ProcessPolicyWriter.IdealThreadsReadHandler = (actualPid, actualKey, maximum) =>
        {
            Assert.Equal(pid, actualPid);
            Assert.Equal(startKey, actualKey);
            Assert.True(maximum >= threads.Count);
            return RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Found(threads.Values.ToArray());
        };
        fixture.ProcessPolicyWriter.PlacementReadHandler = (_, _) =>
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(new(pid, DateTimeOffset.FromFileTime((long)startKey),
                "fixture", @"C:\tests\fixture.exe", null, [1, 2, 3, 4], null, null));
        fixture.ProcessPolicyWriter.IdealReadHandler = (actualPid, actualKey, threadId) =>
        {
            Assert.Equal(pid, actualPid);
            Assert.Equal(startKey, actualKey);
            return RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Found(threads[threadId]);
        };
        fixture.ProcessPolicyWriter.IdealWriteHandler = (expected, desired) =>
        {
            Assert.Equal(threads[expected.ThreadId], expected);
            var record = Assert.Single(Assert.Single(fixture.StateStore.Current.AppliedPlacements).Records);
            Assert.Equal(HostManagerAppliedRecordKinds.CpuThreadIdealProcessor, record.Kind);
            Assert.Equal(expected.ThreadStartKey.ToString(), record.Metadata!["threadStartKey"]);
            threads[expected.ThreadId] = expected with { IdealProcessor = desired };
            return new(true, "recorded hint only");
        };
        var placementCapture = CapturePlacementOperations(fixture.Coordinator);
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.DefaultCpuSetWriteCalls);
        Assert.Equal(1, fixture.ProcessPolicyWriter.IdealProcessorWriteCalls);
        Assert.Equal(2, threads.Values.Select(static t => t.IdealProcessor).Distinct().Count());
        Assert.All(threads.Values, static t => Assert.Equal(15UL, t.AllowedMask));
        Assert.Equal((uint)NativePlacementKind.ThreadIdealProcessor, placementCapture.Desired[0].PlacementKind);
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(1, fixture.ProcessPolicyWriter.IdealProcessorWriteCalls);
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with { Version = fixture.RuntimePlan.Version + 1,
            CompiledAt = DateTimeOffset.UtcNow, OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal) });
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(2, fixture.ProcessPolicyWriter.IdealProcessorWriteCalls);
        Assert.All(threads.Values, static t => Assert.Equal(new ThreadIdealProcessor(0, 0), t.IdealProcessor));
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticCpuPlacementUsesCanonicalScoreAppliesOnceAndRestoresInNormalMode(bool externalGpuControlPending)
    {
        const int processId = 42_410;
        const ulong processStartKey = 133_900_000_000_000_001;
        var startedAt = DateTimeOffset.FromFileTime(checked((long)processStartKey));
        var topology = CreateAutomaticPlacementTopology();
        var cpuScoring = HostManagerTestPlanFactory.CreateCpuScoring(topology);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            "software:automatic-placement",
            baseScore: 80,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        var residency = CpuCoreResidencyTestValues.Create(
            91,
            DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(
                processId,
                processStartKey,
                ("core:0", 50)));
        var gpuActions = new RecordingRunningGpuActions { HasUnreleasedExternalControl = externalGpuControlPending };

        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            runningGpuActions: gpuActions,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            policyExecutionEnabled: false,
            automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => residency),
            cpuTopology: topology,
            cpuScoring: cpuScoring,
            automaticPlacementTopology: topology,
            singleCcdSoftwareIds: ["software:automatic-placement"]);
        var placementCapture = CapturePlacementOperations(fixture.Coordinator);
        var placementScope = ReadPrivateField<HostManagerProcessEffectValidationScopeAuthority>(
            fixture.Coordinator,
            "processEffectValidationScopeAuthorityOwner");
        Assert.True(placementScope!.Capture().IsProductionUnscoped);

        IReadOnlyList<uint> currentCpuSetIds = [1, 2, 3, 4];
        var boundaryEvents = new List<string>();
        fixture.ProcessPolicyWriter.PlacementReadHandler = (actualProcessId, fields) =>
        {
            Assert.Equal(processId, actualProcessId);
            Assert.Equal(ProcessPlacementReadFields.DefaultCpuSets, fields);
            boundaryEvents.Add($"read:{string.Join(',', currentCpuSetIds)}");
            return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(
                new ProcessPlacementRecoverySnapshot(
                    processId,
                    startedAt,
                    $"cycle-budget-{processId}",
                    $@"c:\tests\cycle-budget-{processId}.exe",
                    null,
                    currentCpuSetIds.ToArray(),
                    null,
                    null));
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(processId, request.ProcessId);
            Assert.Equal(startedAt, request.ExpectedStartedAt);
            var restoring = currentCpuSetIds.SequenceEqual([1U, 2U]);
            Assert.Equal(restoring ? [1U, 2U, 3U, 4U] : [1U, 2U], request.CpuSetIds);
            Assert.Single(fixture.StateStore.Current.AppliedPlacements);

            currentCpuSetIds = request.CpuSetIds!.ToArray();
            boundaryEvents.Add($"{(restoring ? "restore" : "write")}:{string.Join(',', currentCpuSetIds)}");
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [
                        new ProcessResourcePolicyBatchFieldResult(
                            ProcessResourcePolicyBatchFields.CpuSets,
                            Succeeded: true,
                            "applied")
                    ])
            ];
        };
        fixture.ProcessPolicyWriter.DefaultCpuSetWriteHandler = (actualProcessId, cpuSetIds) =>
        {
            Assert.Equal(processId, actualProcessId);
            throw new InvalidOperationException("Restoration must use the exact-process batch boundary.");
        };

        _ = await fixture.RunRealtimeCycleAsync();

        var canonicalScore = Assert.Single(
            fixture.Coordinator.SchedulingAuthority.Compute!.Cpu!.Scores,
            score => score.Kind == NativeComputeScoringOutputKind.ProcessCpu
                && score.ProcessId == processId);
        var placementWorkspace = Assert.IsType<NativePlacementCoordinatorWorkspace>(
            ReadPrivateField<NativePlacementCoordinatorWorkspace>(
                fixture.Coordinator,
                "placementCoordinatorWorkspace"));
        Assert.Equal(
            checked((uint)Math.Round(canonicalScore.Score, MidpointRounding.AwayFromZero)),
            placementCapture.Desired[0].Priority);
        Assert.Equal((uint)NativePlacementResourceKind.Cpu, placementCapture.Desired[0].ResourceKind);
        Assert.Equal((uint)NativePlacementKind.CpuSets, placementCapture.Desired[0].PlacementKind);
        Assert.Equal(checked((uint)processId), placementCapture.Desired[0].ProcessId);
        Assert.Equal(processStartKey, placementCapture.Desired[0].ProcessStartKey);
        Assert.Equal([1U, 2U], currentCpuSetIds);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(1, fixture.StateStore.SaveCalls);
        var applied = Assert.Single(fixture.StateStore.Current.AppliedPlacements);
        Assert.Equal(OptimizationResourceKinds.Cpu, applied.ResourceKind);
        var record = Assert.Single(applied.Records);
        Assert.Equal(HostManagerAppliedRecordKinds.CpuAffinity, record.Kind);
        Assert.Equal(processStartKey.ToString(), record.Metadata!["processStartKey"]);
        Assert.True(boundaryEvents.IndexOf("read:1,2,3,4") < boundaryEvents.IndexOf("write:1,2"));

        var savesAfterApply = fixture.StateStore.SaveCalls;
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(savesAfterApply, fixture.StateStore.SaveCalls);
        Assert.Equal([1U, 2U], currentCpuSetIds);

        Assert.Equal(0, gpuActions.PrepareCalls);
        Assert.Equal(0, gpuActions.ApplyCalls);
        gpuActions.HasUnreleasedExternalControl = false;
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = checked(fixture.RuntimePlan.Version + 1),
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "automatic-placement-normal-restore",
            OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal)
        });
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal([1U, 2U, 3U, 4U], currentCpuSetIds);
        Assert.Equal(
            (uint)NativePlacementActionDisposition.Restore,
            placementCapture.Actions[0].Disposition);
        Assert.NotEqual(0UL, placementCapture.Actions[0].DesiredDigest);
        Assert.Equal(2, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.DefaultCpuSetWriteCalls);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(2, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.DefaultCpuSetWriteCalls);
        Assert.Contains("restore:1,2,3,4", boundaryEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticCpuPlacementDoesNotWriteInsideOrOutsideActiveValidationJob(
        bool scopeContainsCandidate)
    {
        const int processId = 42_411;
        const ulong processStartKey = 133_900_000_000_000_002;
        var startedAt = DateTimeOffset.FromFileTime(checked((long)processStartKey));
        var topology = CreateAutomaticPlacementTopology();
        var cpuScoring = HostManagerTestPlanFactory.CreateCpuScoring(topology);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            "software:validation-job-candidate",
            baseScore: 80,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        var residency = CpuCoreResidencyTestValues.Create(
            92,
            DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(
                processId,
                processStartKey,
                ("core:0", 50)));
        var scopeIdentity = scopeContainsCandidate
            ? new HostManagerComputeProcessIdentity(processId, processStartKey)
            : new HostManagerComputeProcessIdentity(processId + 1, processStartKey + 1);
        using var validation = CoordinatorValidationScopeFixture.Create(scopeIdentity);

        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            policyExecutionEnabled: false,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.CpuOnly,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => residency),
            cpuTopology: topology,
            cpuScoring: cpuScoring,
            automaticPlacementTopology: topology,
            singleCcdSoftwareIds: ["software:validation-job-candidate"],
            processEffectValidationScopeAuthority: validation.Authority);
        var placementCapture = CapturePlacementOperations(fixture.Coordinator);
        fixture.ProcessPolicyWriter.PlacementReadHandler = (actualProcessId, fields) =>
        {
            Assert.Equal(processId, actualProcessId);
            Assert.Equal(ProcessPlacementReadFields.DefaultCpuSets, fields);
            return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(
                new ProcessPlacementRecoverySnapshot(
                    processId,
                    startedAt,
                    "validation-job-candidate",
                    @"c:\tests\validation-job-candidate.exe",
                    null,
                    [1, 2, 3, 4],
                    null,
                    null));
        };

        _ = await fixture.RunRealtimeCycleAsync();

        var placementWorkspace = Assert.IsType<NativePlacementCoordinatorWorkspace>(
            ReadPrivateField<NativePlacementCoordinatorWorkspace>(
                fixture.Coordinator,
                "placementCoordinatorWorkspace"));
        Assert.Equal(0, placementCapture.PlanCalls);
        Assert.Empty(placementCapture.Desired);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.StateStore.SaveCalls);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
    }

    [Fact]
    public async Task ActiveValidationScopeStillRestoresExistingOwnedCpuPlacement()
    {
        const int processId = 42_413;
        const ulong processStartKey = 133_900_000_000_000_004;
        var startedAt = DateTimeOffset.FromFileTime(checked((long)processStartKey));
        const string processName = "existing-owned-placement";
        const string executablePath = @"c:\tests\existing-owned-placement.exe";
        const string softwareId = "software:existing-owned-placement";
        var topology = CreateAutomaticPlacementTopology();
        var cpuScoring = HostManagerTestPlanFactory.CreateCpuScoring(topology);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            softwareId,
            baseScore: 80,
            cpuUsagePercent: 50,
            memoryUsagePercent: 25);
        using var validation = CoordinatorValidationScopeFixture.Create(new(
            processId,
            processStartKey));

        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            policyExecutionEnabled: false,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.CpuOnly,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() =>
                CpuCoreResidencyTestValues.Create(
                    94,
                    DateTimeOffset.UtcNow,
                    CpuCoreResidencyTestValues.Process(
                        processId,
                        processStartKey,
                        ("core:0", 50)))),
            cpuTopology: topology,
            cpuScoring: cpuScoring,
            automaticPlacementTopology: topology,
            singleCcdSoftwareIds: ["software:existing-owned-placement"],
            processEffectValidationScopeAuthority: validation.Authority);

        // Establish the initialized coordinator before adding its existing owned receipt.
        // A cold-start legacy receipt has a separate admission policy.
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        var targetId = HostManagerTargetIdentity.CreateProcessTargetId(processId, processStartKey);
        var now = DateTimeOffset.UtcNow;
        var record = new HostManagerAppliedRecord(
            HostManagerAppliedRecordKinds.CpuAffinity,
            "existing-owned-cpu-placement",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["owner"] = "host-manager-automatic-placement-v1",
                ["processId"] = processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["processStartKey"] = processStartKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["processStartedAt"] = startedAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["processName"] = processName,
                ["executablePath"] = executablePath,
                ["affinityKind"] = "cpu-sets",
                ["previousCpuSetIds"] = "1,2,3,4",
                ["appliedCpuSetIds"] = "1,2"
            });
        fixture.StateStore.Current = fixture.StateStore.Current with
        {
            AppliedPlacements =
            [
                new HostManagerAppliedPlacementReceipt(
                    targetId,
                    processName,
                    softwareId,
                    OptimizationResourceKinds.Cpu,
                    [record],
                    now,
                    now)
            ]
        };

        IReadOnlyList<uint> currentCpuSetIds = [1, 2];
        fixture.ProcessPolicyWriter.PlacementReadHandler = (actualProcessId, fields) =>
        {
            Assert.Equal(processId, actualProcessId);
            Assert.Equal(ProcessPlacementReadFields.DefaultCpuSets, fields);
            return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(
                new ProcessPlacementRecoverySnapshot(
                    processId,
                    startedAt,
                    processName,
                    executablePath,
                    null,
                    currentCpuSetIds.ToArray(),
                    null,
                    null));
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(processId, request.ProcessId);
            Assert.Equal([1U, 2U, 3U, 4U], request.CpuSetIds);
            currentCpuSetIds = request.CpuSetIds!.ToArray();
            return
            [
                new ProcessResourcePolicyBatchWriteResult(
                    processId,
                    [
                        new ProcessResourcePolicyBatchFieldResult(
                            ProcessResourcePolicyBatchFields.CpuSets,
                            Succeeded: true,
                            "restored")
                    ])
            ];
        };

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal([1U, 2U, 3U, 4U], currentCpuSetIds);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(1, fixture.StateStore.SaveCalls);
    }

    [Fact]
    public async Task CompiledScoreOnlyWithoutDebugModeKeepsPlacementCleanupAndPersistenceEffectFree()
    {
        const int processId = 42_412;
        const ulong processStartKey = 133_900_000_000_000_003;
        var topology = CreateAutomaticPlacementTopology();
        var cpuScoring = HostManagerTestPlanFactory.CreateCpuScoring(topology);
        var processFacts = CreateCompleteProcessFacts(
            processId,
            processStartKey,
            "software:compiled-score-only",
            baseScore: 20,
            cpuUsagePercent: 1,
            memoryUsagePercent: 25);
        var settings = AppSettingsDefaults.Create() with
        {
            Debug = new AppDebugSettings(
                DebugModeEnabled: false,
                DebugLogEnabled: false,
                HostManagerSmartCoordinatorScoreOnlyEnabled: true,
                HostManagerSmartCoordinatorPerformanceLogEnabled: false)
        };
        var projection = AppSettingsRuntimeCapabilityProjector.Evaluate(
            AppSettingsNormalizer.Normalize(settings),
            new RuntimeExecutionCapabilityPolicy(true, true, true));
        var diagnostics = RuntimePlanCompiler.CompileDiagnosticsPlan(
            projection.Settings.Debug);
        Assert.False(diagnostics.DebugModeEnabled);
        Assert.True(diagnostics.HostManagerSmartCoordinatorScoreOnlyEnabled);

        var residency = CpuCoreResidencyTestValues.Create(
            93,
            DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(
                processId,
                processStartKey,
                ("core:0", 1)));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            optimizationMode: AppOptimizationModes.Smart,
            automaticMemoryCleanupEnabled: true,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => residency),
            cpuTopology: topology,
            cpuScoring: cpuScoring,
            automaticPlacementTopology: topology,
            singleCcdSoftwareIds: ["software:compiled-score-only"]);
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = checked(fixture.RuntimePlan.Version + 1),
            Reason = "compiled-score-only-with-debug-mode-off",
            Diagnostics = diagnostics
        });
        var durableBefore = CaptureDurableFiles(fixture.Root);

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.True(fixture.Workspace.Snapshot.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.MemoryCleanupPlanner.PlanCalls);
        Assert.Equal(0, fixture.PublicResourceManager.TickAttemptCalls);
        Assert.Equal(0, fixture.StateStore.SaveCalls);
        Assert.Equal(durableBefore, CaptureDurableFiles(fixture.Root));
        fixture.AssertNoEffectCollaboratorCalls();
    }

    private static CpuTopologySnapshot CreateAutomaticPlacementTopology()
    {
        var physicalCores = Enumerable.Range(0, 4)
            .Select(index => new CpuPhysicalCoreModel(
                $"core:{index}",
                index,
                $"Core {index}",
                index < 2 ? "ccd:0" : "ccd:1",
                0,
                1,
                null,
                [index],
                []))
            .ToArray();
        var logicalProcessors = Enumerable.Range(0, 4)
            .Select(index => new CpuLogicalProcessorModel(
                index,
                0,
                index,
                $"core:{index}",
                index < 2 ? "ccd:0" : "ccd:1",
                1,
                null,
                true,
                checked((uint)(index + 1))))
            .ToArray();
        return new CpuTopologySnapshot(
            DateTimeOffset.UtcNow,
            "Automatic placement CPU",
            new CpuSpecificationModel(
                "Automatic placement CPU",
                "Test",
                "Test",
                4,
                4,
                null,
                null,
                null,
                null,
                "fixture"),
            "fixture",
            "fixture",
            CpuTopologyAffinityTargetKinds.LogicalProcessorMask,
            CpuTopologyVisualLayoutKinds.CcdGrid,
            "fixture",
            4,
            4,
            2,
            false,
            [
                new CpuCcdModel("ccd:0", 0, "CCD 0", null, [0, 1], [0, 1], "fixture"),
                new CpuCcdModel("ccd:1", 1, "CCD 1", null, [2, 3], [2, 3], "fixture")
            ],
            physicalCores,
            logicalProcessors,
            []);
    }
}
