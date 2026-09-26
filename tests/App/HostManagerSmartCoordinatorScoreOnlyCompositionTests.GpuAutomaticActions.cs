using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task ExternalAutomaticActionUsesResultLedgerWithoutPublishingShimPolicy()
    {
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions);
        var process = AutomaticGpuProcess();
        var policy = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        actions.Prepare = request =>
        {
            var renderer = Assert.Single(request.Processes);
            return new(new(request, [], new Dictionary<int, GpuGraphicsApi>
            {
                [renderer.ProcessId] = GpuGraphicsApi.D3D11
            }) { External = new(renderer, renderer) }, "confirmed external renderer");
        };
        actions.Apply = plan =>
        {
            Assert.Empty(plan.PolicyValue);
            Assert.Null(policy.ReadPolicy(process.TargetId));
            return new([new("attempt", WindowsExternalGpuPlacementRuntime.ProviderId,
                new Dictionary<string, string>
                {
                    ["controllerExited"] = bool.TrueString,
                    ["cleanupPassed"] = bool.TrueString,
                    ["controllerSucceeded"] = bool.TrueString,
                    ["residentConfirmation"] = "partial"
                })], "Confirmed partial transfer", RunningGpuPlacementActionStatuses.RecreateRequested);
        };

        var desired = (await CreateGpuDesired(fixture.Coordinator, process, null))!;
        Assert.True(ExternalGpuRuntimePlacementRecord.IsRecord(desired.Record));
        Assert.Null(policy.ReadPolicy(process.TargetId));
        Assert.True(await ApplyGpuDesired(fixture, desired));

        var stored = Assert.Single(Assert.Single(fixture.StateStore.Current.AppliedPlacements).Records);
        Assert.True(ExternalGpuRuntimePlacementRecord.TryReadResult(stored, out var result));
        Assert.True(ExternalGpuRuntimePlacementRecord.IsConfirmed(result!));
        Assert.False(result!.Applied);
        Assert.Null(policy.ReadPolicy(process.TargetId));
        Assert.Equal(1, actions.ApplyCalls);
    }

    [Theory]
    [InlineData(78_598_144d, 0d, 78_598_144d)]
    [InlineData(10_772_480d, 20_316_160d, 31_088_640d)]
    [InlineData(null, 20_316_160d, null)]
    [InlineData(10_772_480d, null, null)]
    public async Task PlacementUsesResidentBytesWithoutDedicatedCapacity(
        double? privateBytes, double? sharedBytes, double? expectedBytes)
    {
        var facts = CreateCompleteProcessFacts(42412, 133900000000000001,
            "software:gpu-app", baseScore: 35, cpuUsagePercent: 0, memoryUsagePercent: 0);
        facts = facts with
        {
            RequestedMetricMask = facts.RequestedMetricMask | SchedulingProcessMetricMask.GpuUsage
                | SchedulingProcessMetricMask.GpuDedicatedMemory,
            CurrentMetricMask = facts.CurrentMetricMask | SchedulingProcessMetricMask.GpuUsage
                | SchedulingProcessMetricMask.GpuDedicatedMemory,
            Processes = [facts.Processes.Single() with
            {
                ValidMetricMask = facts.Processes.Single().ValidMetricMask | SchedulingProcessMetricMask.GpuUsage
                    | SchedulingProcessMetricMask.GpuDedicatedMemory,
                Gpus = [new SchedulingProcessGpuFact(0, 69842, SchedulingProcessMetricMask.GpuUsage,
                    0, 0, 1, expectedBytes.HasValue ? 1UL : 0UL, 1, expectedBytes.HasValue ? 1UL : 0UL)
                    { PrivateMemoryBytes = privateBytes, SharedMemoryBytes = sharedBytes }]
            }]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: facts);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(gpuInventoryCurrent: true));
        var capture = (Task)typeof(HostManagerSmartCoordinator)
            .GetMethod("CaptureHostManagerSampleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [fixture.RuntimePlan, CancellationToken.None])!;
        await capture;
        var captured = capture.GetType().GetProperty("Result")!.GetValue(capture)!;
        var sample = captured.GetType().GetProperty("Sample")!.GetValue(captured);
        Assert.NotNull(sample);
        var processes = (IReadOnlyList<HostManagerAutomaticPlacementProcess>)typeof(HostManagerSmartCoordinator)
            .GetMethod("CreateAutomaticPlacementProcesses", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [sample, null, Array.Empty<HostManagerAppliedPlacementReceipt>()])!;
        var process = Assert.Single(processes);
        Assert.Equal(expectedBytes.HasValue, process.ObservedDedicatedMemoryBytes.TryGetValue(69842, out var actual));
        if (expectedBytes.HasValue) Assert.Equal(expectedBytes.Value, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettledUnconfirmedOrSkippedRuntimeActionRestoresPolicyBeforeALaterOpportunity(bool unconfirmed)
    {
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions);
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        var process = AutomaticGpuProcess();
        actions.Apply = _ => unconfirmed
            ? new([new("observation", "external", new Dictionary<string, string>
            {
                ["residentConfirmation"] = "unconfirmed", ["controllerSucceeded"] = "True",
                ["controllerExited"] = "True", ["cleanupPassed"] = "True"
            })], "Next resident sample did not confirm the completed action.", RunningGpuPlacementActionStatuses.NotApplied)
            : new([], "Browser became foreground before execution.", RunningGpuPlacementActionStatuses.Skipped);
        await ApplyGpuDesired(fixture, (await CreateGpuDesired(fixture.Coordinator, process, null))!);
        Assert.Equal(1, actions.ApplyCalls);
        Assert.Null(runtime.ReadPolicy(process.TargetId));
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);

        actions.Apply = _ => new([], "Owned runtime action dispatched.", RunningGpuPlacementActionStatuses.RecreateRequested);
        await ApplyGpuDesired(fixture, (await CreateGpuDesired(fixture.Coordinator, process, null))!);
        Assert.Equal(2, actions.ApplyCalls);
        Assert.NotNull(runtime.ReadPolicy(process.TargetId));
        var record = Assert.Single(Assert.Single(fixture.StateStore.Current.AppliedPlacements).Records);
        var result = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(record.Metadata!["runtimeActionResult"]);
        Assert.Equal(RunningGpuPlacementActionStatuses.RecreateRequested, result!.Status);
    }

    [Theory]
    [InlineData(true, false, true, 0)]
    [InlineData(false, true, false, 1)]
    [InlineData(false, false, false, 0)]
    public async Task GpuCandidateAdmissionSeparatesReadyUnknownAndUnsupportedRoutes(
        bool available, bool unknown, bool admitted, int observations)
    {
        var actions = new RecordingRunningGpuActions { Available = available, UnknownApi = unknown };
        await using var fixture = await CreateGpuActionFixture(actions);
        var process = AutomaticGpuProcess() with
        {
            CanApplyPhysicalPlacement = false,
            ObservedGpuUsagePercent = new Dictionary<ulong, double> { [123] = 1 }
        };
        var pending = new List<HostManagerAutomaticGpuPlacement>();
        var task = (Task<IReadOnlyList<HostManagerAutomaticPlacementProcess>>)typeof(HostManagerSmartCoordinator)
            .GetMethod("PrepareAutomaticGpuCandidatesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [new[] { process }, Array.Empty<HostManagerAppliedPlacementReceipt>(), pending, CancellationToken.None])!;
        var result = await task;
        Assert.Equal(admitted, Assert.Single(result).CanMigrateGpu);
        Assert.Equal(observations, pending.Count);
        Assert.All(pending, item => Assert.Equal(item.ObservedAdapterKey, item.TargetAdapterKey));
        Assert.Equal(0, actions.ApplyCalls);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
    }

    [Fact]
    public async Task UnconfirmedObservationDeadlineDoesNotStopFollowingActions()
    {
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions);
        actions.ApplyWithWindows = async (_, _, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            return new([new("observation", "external", new Dictionary<string, string>
            {
                ["residentConfirmation"] = "unconfirmed", ["controllerSucceeded"] = "True",
                ["controllerExited"] = "True", ["cleanupPassed"] = "True"
            })], "No next publication before the observation deadline.", RunningGpuPlacementActionStatuses.NotApplied);
        };
        var process = AutomaticGpuProcess();
        Assert.True(await ApplyGpuDesired(fixture, (await CreateGpuDesired(fixture.Coordinator, process, null))!, 500));
        Assert.Equal(1, actions.ApplyCalls);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        actions.ApplyWithWindows = null;
        actions.Apply = _ => new([], "Following unrelated action.", RunningGpuPlacementActionStatuses.RecreateRequested);
        var other = process with { TargetId = "following-software", ProcessId = process.ProcessId + 1 };
        Assert.True(await ApplyGpuDesired(fixture, (await CreateGpuDesired(fixture.Coordinator, other, null))!));
        Assert.Equal(2, actions.ApplyCalls);
    }

    [Fact]
    public async Task AutomaticPlacementWriterRejectsStartupPreferenceWithoutChangingUserValue()
    {
        var preferences = new GpuPreferenceValues();
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), preferences: preferences);
        var path = AutomaticGpuProcess().ExecutablePath!;
        preferences.WriteValue(path, "GpuPreference=1;");
        var record = new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.GpuPreference, "obsolete-auto-write",
            new Dictionary<string, string> { ["path"] = path, ["appliedValue"] = "GpuPreference=2;" });
        var result = (int)typeof(HostManagerSmartCoordinator).GetMethod("WriteAutomaticPlacement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [record])!;
        Assert.NotEqual(0, result);
        Assert.Equal("GpuPreference=1;", preferences.ReadValueForRecovery(path).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticGpuShimKeepsFileOwnershipAfterNoWindowOrFailedActionAndRestoresWithPreference(bool failAction)
    {
        var actions = new RecordingRunningGpuActions { Available = true };
        var preferences = new GpuPreferenceValues();
        await using var fixture = await CreateGpuActionFixture(actions, preferences: preferences);
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        var process = AutomaticGpuProcess();
        Assert.True(runtime.TryWritePolicy(process.TargetId, null, [1, 0, 255]));
        var preference = new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.GpuPreference, "preference",
            new Dictionary<string, string>
            {
                ["owner"] = "host-manager-automatic-placement-v1", ["path"] = process.ExecutablePath!,
                ["hadValue"] = "false", ["previousValue"] = "", ["appliedValue"] = "GpuPreference=1;"
            });
        preferences.WriteValue(process.ExecutablePath!, "GpuPreference=1;");
        var existing = new HostManagerAppliedPlacementReceipt(process.TargetId, "GPU target", process.SoftwareId,
            OptimizationResourceKinds.Gpu, [preference], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        fixture.StateStore.Current = fixture.StateStore.Current with { AppliedPlacements = [existing] };
        var desired = (await CreateGpuDesired(fixture.Coordinator, process, existing))!;
        Assert.NotNull(desired);
        Assert.Equal(new byte[] { 1, 0, 255 }, runtime.ReadPolicy(process.TargetId));
        Assert.Equal(0, actions.ApplyCalls);
        var preferenceDesired = new HostManagerPlacementDesired(existing, preference, 30);
        var beforeBudget = BuildGpuBudget(fixture, [preferenceDesired, desired], 1, 1);
        Assert.Equal(2, beforeBudget.Desired.Count);
        Assert.Single(beforeBudget.Applied.Single().Records);
        Assert.Single(BuildGpuBudget(fixture, [preferenceDesired, desired], 1, 0).Desired);
        actions.Apply = plan =>
        {
            var saved = Assert.Single(fixture.StateStore.Current.AppliedPlacements);
            Assert.Equal(2, saved.Records.Count);
            Assert.Contains(preference, saved.Records);
            Assert.True(GpuShimPolicyRecord.TryRead(Assert.Single(saved.Records, r => r.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy), out var owned));
            Assert.Equal(new byte[] { 1, 0, 255 }, owned.PreviousValue);
            Assert.Equal(plan.PolicyValue, runtime.ReadPolicy(process.TargetId));
            if (failAction) throw new IOException("fixture runtime action failure");
            return new([], "No window, provider configured", RunningGpuPlacementActionStatuses.Prepared)
            {
                Processes = [new(plan.Request.Processes.Single(), true, "configured", null, null, null)]
            };
        };
        await ApplyGpuDesired(fixture, desired);
        Assert.Equal(1, actions.ApplyCalls);
        var stored = Assert.Single(fixture.StateStore.Current.AppliedPlacements).Records;
        Assert.Equal(2, stored.Count);
        var shim = Assert.Single(stored, r => r.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy);
        var outcome = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(shim.Metadata!["runtimeActionResult"])!;
        Assert.Equal(failAction ? RunningGpuPlacementActionStatuses.Unresolved : RunningGpuPlacementActionStatuses.Prepared, outcome.Status);
        Assert.False(outcome.Applied);
        Assert.False(outcome.Triggered);
        Assert.Equal(desired.RuntimeGpuAction!.PolicyValue, runtime.ReadPolicy(process.TargetId));

        var replan = await CreateGpuDesired(fixture.Coordinator, process, fixture.StateStore.Current.AppliedPlacements.Single());
        Assert.NotNull(replan);
        Assert.Equal(1, actions.ApplyCalls);
        Assert.True(GpuShimPolicyRecord.TryRead(replan.Record, out var retained));
        Assert.Equal(new byte[] { 1, 0, 255 }, retained.PreviousValue);
        Assert.Equal(desired.RuntimeGpuAction.PolicyValue, retained.AppliedValue);
        var settledBudget = BuildGpuBudget(fixture, [preferenceDesired, replan], 1, 1);
        Assert.Equal(2, settledBudget.Applied.Single().Records.Count);
        Assert.Equal(2, settledBudget.Desired.Count);
        Assert.Single(BuildGpuBudget(fixture, [], 1, 0).Applied.Single().Records);
        Assert.Equal(2, BuildGpuBudget(fixture, [], 2, 0).Applied.Single().Records.Count);
        var retarget = (await CreateGpuDesired(fixture.Coordinator, process, fixture.StateStore.Current.AppliedPlacements.Single(), 456))!;
        Assert.True(GpuShimPolicyRecord.TryRead(retarget.Record, out var retargeted));
        Assert.Equal(new byte[] { 1, 0, 255 }, retargeted.PreviousValue);
        Assert.Equal(D3d11ProxyShimRuntime.CreateExactPolicyValue(456), retargeted.AppliedValue);
        Assert.Equal(desired.RuntimeGpuAction.PolicyValue, runtime.ReadPolicy(process.TargetId));

        // Normal mode invokes the production native recovery owner, one admitted record per cycle.
        _ = await fixture.RunRealtimeCycleAsync();
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        Assert.Equal(new byte[] { 1, 0, 255 }, runtime.ReadPolicy(process.TargetId));
        Assert.Equal(RecoveryReadStatus.NotFoundOrExited, preferences.ReadValueForRecovery(process.ExecutablePath!).Status);
        Assert.Equal(1, actions.ApplyCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticGpuShimDoesNotWriteOrConfigureWhenCheckpointFailsOrBaselineChanged(bool failCheckpoint)
    {
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions,
            failCheckpoint ? new FailingGpuCheckpointStore() : null);
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        var process = AutomaticGpuProcess();
        var desired = (await CreateGpuDesired(fixture.Coordinator, process, null))!;
        Assert.NotNull(desired);
        Assert.Null(runtime.ReadPolicy(process.TargetId));
        if (failCheckpoint)
            await Assert.ThrowsAsync<IOException>(() => ApplyGpuDesired(fixture, desired));
        else
        {
            Assert.True(runtime.TryWritePolicy(process.TargetId, null, [99]));
            await ApplyGpuDesired(fixture, desired);
            Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        }
        Assert.Equal(0, actions.ApplyCalls);
        Assert.Equal(failCheckpoint ? null : new byte[] { 99 }, runtime.ReadPolicy(process.TargetId));
    }

    [Fact]
    public async Task ActualResultCheckpointFailureKeepsUnresolvedDurableOwnershipAndAllowsRecovery()
    {
        using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
        var commits = 0;
        var ledgerPath = Path.Combine(files.Root, "gpu-owner.json");
        using var store = new JsonHostManagerRollbackStateStore(ledgerPath, TimeProvider.System, TimeSpan.FromMinutes(1),
            () => { if (++commits == 3) throw new IOException("fixture result commit failure"); });
        await store.ReserveNativeHostSessionIncarnationAsync(default);
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions, store);
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        var process = AutomaticGpuProcess();
        var desired = (await CreateGpuDesired(fixture.Coordinator, process, null))!;
        byte[]? pendingImage = null;
        actions.Apply = plan =>
        {
            pendingImage = File.ReadAllBytes(ledgerPath);
            Assert.Contains(RunningGpuPlacementActionStatuses.Unresolved, System.Text.Encoding.UTF8.GetString(pendingImage));
            Assert.Equal(plan.PolicyValue, runtime.ReadPolicy(process.TargetId));
            return new([], "configured without a window", RunningGpuPlacementActionStatuses.Prepared);
        };
        await Assert.ThrowsAnyAsync<IOException>(() => ApplyGpuDesired(fixture, desired));
        Assert.Equal(3, commits);
        Assert.Equal(1, actions.ApplyCalls);
        Assert.NotNull(pendingImage);
        Assert.Equal(pendingImage, File.ReadAllBytes(ledgerPath));
        var owned = Assert.Single((await store.LoadAsync(default)).AppliedPlacements).Records.Single();
        var unresolved = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(owned.Metadata!["runtimeActionResult"])!;
        Assert.Equal(RunningGpuPlacementActionStatuses.Unresolved, unresolved.Status);
        Assert.False(unresolved.Applied);
        Assert.False(unresolved.Triggered);
        Assert.Equal(desired.RuntimeGpuAction!.PolicyValue, runtime.ReadPolicy(process.TargetId));
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Empty((await store.LoadAsync(default)).AppliedPlacements);
        Assert.Null(runtime.ReadPolicy(process.TargetId));
        Assert.Equal(1, actions.ApplyCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticGpuShimUsesSavedRouteAndPermissionBeforeAnyFileEffect(bool available)
    {
        var actions = new RecordingRunningGpuActions { Available = available };
        await using var fixture = await CreateGpuActionFixture(actions);
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        var process = AutomaticGpuProcess();
        var desired = await CreateGpuDesired(fixture.Coordinator, process, null);
        Assert.Equal(available, desired is not null);
        Assert.Equal(1, actions.PrepareCalls);
        Assert.Null(runtime.ReadPolicy(process.TargetId));
        Assert.Equal(0, actions.ApplyCalls);
        Assert.Null(await CreateGpuDesired(fixture.Coordinator,
            process with { Policy = process.Policy with { RuntimeHotSwitchEnabled = false } }, null));
        Assert.Equal(1, actions.PrepareCalls);
    }

    private static async Task<ScoreOnlyCoordinatorFixture> CreateGpuActionFixture(
        RecordingRunningGpuActions actions, IHostManagerRollbackStateStore? store = null, GpuPreferenceValues? preferences = null)
    {
        var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false, optimizationMode: AppOptimizationModes.Normal,
            runningGpuActions: actions, rollbackStateStoreOverride: store, graphicsPreferenceOverride: preferences,
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(
                pid, DateTimeOffset.FromFileTime(133900000000000001))));
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = fixture.RuntimePlan.Version + 1,
            GpuPlacement = fixture.RuntimePlan.GpuPlacement with { GlobalPreciseProviderEnabled = true }
        });
        return fixture;
    }

    private static HostManagerAutomaticPlacementProcess AutomaticGpuProcess()
        => new("gpu-app-target", "software:gpu-app", "GPU app", 42412, 133900000000000001, "gpu-app", @"C:\fixture\gpu-app.exe",
            true, ResolvedGpuPlacementPolicy.FromSoftware(GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software:gpu-app", "GPU app") with
            {
                EnabledMode = GpuPlacementPolicyModes.Auto, SchedulingMode = GpuPlacementSchedulingModes.Precise,
                RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise, RuntimeHotSwitchEnabled = true,
                AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim], TargetGpu = GpuPlacementTargets.IntegratedGpu
            }), 30, new Dictionary<ulong, double> { [123] = 30 }, new Dictionary<ulong, double>());

    private static Task<HostManagerPlacementDesired?> CreateGpuDesired(HostManagerSmartCoordinator coordinator,
        HostManagerAutomaticPlacementProcess process, HostManagerAppliedPlacementReceipt? existing, ulong adapter = 123)
        => (Task<HostManagerPlacementDesired?>)typeof(HostManagerSmartCoordinator)
            .GetMethod("TryCreateGpuShimPlacementDesiredAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, [new HostManagerAutomaticGpuPlacement(process, 30, true, adapter, "fixture"), existing, CancellationToken.None])!;

    private static (IReadOnlyList<HostManagerPlacementDesired> Desired, IReadOnlyList<HostManagerAppliedPlacementReceipt> Applied)
        BuildGpuBudget(ScoreOnlyCoordinatorFixture fixture, IReadOnlyList<HostManagerPlacementDesired> desired, uint recovery, uint apply)
    {
        var placements = fixture.StateStore.Current.AppliedPlacements;
        var observations = typeof(HostManagerSmartCoordinator).GetMethod("CapturePlacementObservations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [placements]);
        var byIdentity = desired.ToDictionary(item => HostManagerPlacementCoordinatorProjection.CreateIdentity(item.Placement, item.Record));
        var bounded = typeof(HostManagerSmartCoordinator).GetMethod("BuildBoundedPlacementCycle", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [placements, desired, byIdentity, observations, recovery, apply])!;
        return ((IReadOnlyList<HostManagerPlacementDesired>)bounded.GetType().GetProperty("Desired")!.GetValue(bounded)!,
            (IReadOnlyList<HostManagerAppliedPlacementReceipt>)bounded.GetType().GetProperty("Applied")!.GetValue(bounded)!);
    }

    private static async Task<bool> ApplyGpuDesired(ScoreOnlyCoordinatorFixture fixture, HostManagerPlacementDesired desired,
        uint? workBudgetMilliseconds = null)
    {
        var inputs = new NativePlacementDesiredInput[1];
        var projected = HostManagerPlacementCoordinatorProjection.ProjectDesired([desired], inputs).Records.Single();
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.NativeActionTransaction, out var permit));
        var action = new NativePlacementAction
        {
            TargetKey = projected.Identity.TargetKey, RecordKey = projected.Identity.RecordKey,
            DeadlineMilliseconds = checked((ulong)Environment.TickCount64
                + (workBudgetMilliseconds is { } budget
                    ? budget + (ulong)fixture.RuntimePlan.HostManager.HotPublish.PlacementCoordinator.WindowExecution.CleanupReserveMilliseconds
                    : (ulong)fixture.RuntimePlan.HostManager.HotPublish.PlacementCoordinator.ActionTimeoutMilliseconds))
        };
        var task = (Task)typeof(HostManagerSmartCoordinator)
            .GetMethod("ApplyAutomaticPlacementAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [permit, fixture.StateStore.Current, action, projected, CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (bool)result.GetType().GetProperty("CanContinue")!.GetValue(result)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GpuRouteInspectionVisitsEveryProcessAfterUnknownOrUnreadableProcess(bool unreadable)
    {
        var actions = new RecordingRunningGpuActions();
        await using var fixture = await CreateGpuActionFixture(actions);
        var template = AutomaticGpuProcess() with
        {
            ObservedGpuUsagePercent = new Dictionary<ulong, double> { [123] = 1 }
        };
        var processes = Enumerable.Range(0, 3).Select(index => template with
        {
            ProcessId = template.ProcessId + index,
            TargetId = template.TargetId + index
        }).ToArray();
        var visited = new List<int>();
        actions.Prepare = request =>
        {
            var id = Assert.Single(request.Processes).ProcessId;
            visited.Add(id);
            if (id != processes[2].ProcessId)
            {
                if (unreadable) throw new UnauthorizedAccessException("fixture process cannot be read");
                return new(null, "fixture unknown") { ApiObservationProcesses = request.Processes };
            }
            return new(new(request, D3d11ProxyShimRuntime.CreateExactPolicyValue(request.TargetAdapterKey),
                new Dictionary<int, GpuGraphicsApi> { [id] = GpuGraphicsApi.D3D11 }), "fixture confirmed");
        };
        var pending = new List<HostManagerAutomaticGpuPlacement>();
        var task = (Task<IReadOnlyList<HostManagerAutomaticPlacementProcess>>)typeof(HostManagerSmartCoordinator)
            .GetMethod("PrepareAutomaticGpuCandidatesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [processes, Array.Empty<HostManagerAppliedPlacementReceipt>(), pending, CancellationToken.None])!;
        var result = await task;
        Assert.Equal(processes.Select(process => process.ProcessId), visited);
        Assert.Equal(new[] { false, false, true }, result.Select(process => process.CanMigrateGpu));
        Assert.Equal(unreadable ? 0 : 2, pending.Count);
        Assert.Equal(0, actions.ApplyCalls);
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
    }

    [Fact]
    public async Task GpuRecognitionUsesObservedProcessesAndKeepsSiblingRenderersIndependent()
    {
        var actions = new RecordingRunningGpuActions();
        await using var fixture = await CreateGpuActionFixture(actions);
        var template = AutomaticGpuProcess();
        var processes = Enumerable.Range(0, 4).Select(index => template with
        {
            ProcessId = template.ProcessId + index,
            TargetId = template.TargetId + index,
            // A score alone is not evidence of a GPU process. Idle observed clients are.
            ObservedGpuUsagePercent = index is 1 or 3
                ? new Dictionary<ulong, double> { [456] = 0 } : new Dictionary<ulong, double>(),
            ObservedDedicatedMemoryBytes = index == 2
                ? new Dictionary<ulong, double> { [456] = 1024 } : new Dictionary<ulong, double>()
        }).ToArray();
        var visited = new List<int>();
        actions.Prepare = request =>
        {
            var id = Assert.Single(request.Processes).ProcessId;
            visited.Add(id);
            Assert.Equal(456UL, request.TargetAdapterKey);
            Assert.Equal(template.SoftwareId, request.SoftwareId);
            if (id == processes[1].ProcessId) return new(null, "unconfirmed sibling");
            return new(new(request, D3d11ProxyShimRuntime.CreateExactPolicyValue(request.TargetAdapterKey),
                new Dictionary<int, GpuGraphicsApi> { [id] = GpuGraphicsApi.D3D11 }), "confirmed sibling");
        };
        var task = (Task<IReadOnlyList<HostManagerAutomaticPlacementProcess>>)typeof(HostManagerSmartCoordinator)
            .GetMethod("PrepareAutomaticGpuCandidatesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [processes, Array.Empty<HostManagerAppliedPlacementReceipt>(), null, CancellationToken.None])!;
        var result = await task;
        Assert.Equal(processes.Skip(1).Select(process => process.ProcessId), visited);
        Assert.Equal(new[] { false, false, true, true }, result.Select(process => process.CanMigrateGpu));
        Assert.Equal(0, actions.ApplyCalls);
    }

    private sealed class RecordingRunningGpuActions : IRunningGpuPlacementActionService
    {
        public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(RunningGpuPlacementActionRequest request,
            RunningGpuApiObservationExecution execution, CancellationToken token)
            => FirstUse is { } firstUse ? firstUse(request, execution, token)
                : throw new InvalidOperationException("This fixture supplies an already recorded API route.");
        public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(RunningGpuPlacementActionRequest request,
            Func<GpuPlacementProcessInstance, CancellationToken, Task<GpuGraphicsApi?>> observeAsync, CancellationToken token)
            => throw new InvalidOperationException("This fixture supplies an already recorded API route.");
        internal bool Available { get; init; }
        internal bool UnknownApi { get; set; }
        internal Func<RunningGpuPlacementActionRequest, RunningGpuApiObservationExecution, CancellationToken,
            Task<RunningGpuPlacementPreparation>>? FirstUse { get; set; }
        internal int PrepareCalls { get; private set; }
        internal Func<RunningGpuPlacementActionRequest, RunningGpuPlacementPreparation>? Prepare { get; set; }
        internal int ApplyCalls { get; private set; }
        internal Func<RunningGpuPlacementActionPlan, RunningGpuPlacementActionResult>? Apply { get; set; }
        internal Func<RunningGpuPlacementActionPlan, RunningGpuPlacementExecution, CancellationToken,
            Task<RunningGpuPlacementActionResult>>? ApplyWithWindows { get; set; }
        public Task<RunningGpuPlacementPreparation> PrepareAsync(RunningGpuPlacementActionRequest request, CancellationToken token)
        {
            PrepareCalls++;
            if (Prepare is not null) return Task.FromResult(Prepare(request));
            if (UnknownApi) return Task.FromResult(new RunningGpuPlacementPreparation(null, "fixture unknown route")
                { ApiObservationProcesses = request.Processes });
            return Task.FromResult(new RunningGpuPlacementPreparation(Available
                ? new(request, D3d11ProxyShimRuntime.CreateExactPolicyValue(request.TargetAdapterKey),
                    request.Processes.ToDictionary(process => process.ProcessId, _ => GpuGraphicsApi.D3D11)) : null, "fixture saved route"));
        }
        public Task<RunningGpuPlacementActionResult> TryApplyAsync(RunningGpuPlacementActionPlan plan,
            RunningGpuPlacementExecution windows, CancellationToken token)
        {
            ApplyCalls++;
            if (ApplyWithWindows is not null) return ApplyWithWindows(plan, windows, token);
            return Task.FromResult(Apply?.Invoke(plan) ?? new([], "fixture no window", RunningGpuPlacementActionStatuses.Prepared));
        }
    }

    private sealed class FailingGpuCheckpointStore : IHostManagerRollbackStateStore
    {
        public Task<HostManagerRollbackStateDocument> LoadAsync(CancellationToken token) => Task.FromResult(HostManagerRollbackStateDocument.Empty);
        public Task<HostManagerRollbackStateDocument> ReserveNativeHostSessionIncarnationAsync(CancellationToken token) => throw new IOException("fixture reserve");
        public Task SaveAsync(HostManagerRollbackStateDocument value, CancellationToken token) => throw new IOException("fixture checkpoint");
    }

    private sealed class GpuPreferenceValues : IWindowsGraphicsPreferenceStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        internal Func<string, RecoveryReadResult<string>>? Read { get; set; }
        internal int Writes { get; private set; }
        public RecoveryReadResult<string> ReadValueForRecovery(string path) => Read is not null ? Read(path) : values.TryGetValue(path, out var value)
            ? RecoveryReadResult<string>.Found(value) : RecoveryReadResult<string>.NotFoundOrExited(0, "missing");
        public void WriteValue(string path, string value) { Writes++; values[path] = value; }
        public void DeleteValue(string path) => values.Remove(path);
        public string BuildPreferIntegratedGpuValue(string? value) => "GpuPreference=1;";
        public string BuildPreferHighPerformanceGpuValue(string? value) => "GpuPreference=2;";
        public string DescribePreference(string? value) => value ?? "";
    }
}
