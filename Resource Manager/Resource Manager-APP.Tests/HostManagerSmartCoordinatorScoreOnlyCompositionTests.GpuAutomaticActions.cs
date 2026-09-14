using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
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
            .Invoke(coordinator, [new HostManagerAutomaticGpuPreferencePlacement(process, 30, true, adapter, "fixture"), existing, CancellationToken.None])!;

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

    private static async Task ApplyGpuDesired(ScoreOnlyCoordinatorFixture fixture, HostManagerPlacementDesired desired)
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
                + (ulong)fixture.RuntimePlan.HostManager.HotPublish.PlacementCoordinator.ActionTimeoutMilliseconds)
        };
        var task = (Task)typeof(HostManagerSmartCoordinator)
            .GetMethod("ApplyAutomaticPlacementAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [permit, fixture.StateStore.Current, action, projected, CancellationToken.None])!;
        await task;
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
        internal int ApplyCalls { get; private set; }
        internal Func<RunningGpuPlacementActionPlan, RunningGpuPlacementActionResult>? Apply { get; set; }
        internal Func<RunningGpuPlacementActionPlan, RunningGpuPlacementExecution, CancellationToken,
            Task<RunningGpuPlacementActionResult>>? ApplyWithWindows { get; set; }
        public Task<RunningGpuPlacementPreparation> PrepareAsync(RunningGpuPlacementActionRequest request, CancellationToken token)
        {
            PrepareCalls++;
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
