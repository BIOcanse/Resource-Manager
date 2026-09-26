using System.Text.Json;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using PolicyFiles = Resource_Manager_APP.Tests.D3d11ProxyShimRuntimeTests.PolicyFiles;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NormalCoordinatorRestoresShimPolicyThroughExistingNativeOwner(bool applied, bool blockWrite)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, policyExecutionEnabled: false,
            automaticMemoryCleanupEnabled: false, optimizationMode: AppOptimizationModes.Normal);
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT")));
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        Assert.True(runtime.TryWritePolicy("gpu-policy", null, [1, 0, 255]));
        var record = Assert.IsType<HostManagerAppliedRecord>(runtime.CapturePolicyRecord("gpu-policy", [2]));
        fixture.StateStore.Current = fixture.StateStore.Current with
        {
            AppliedPlacements = [GpuShimPolicyLedgerTests.Placement(record)]
        };
        if (applied) Assert.True(runtime.TryApplyPolicyRecord(record));
        var path = runtime.GetPolicyPath("gpu-policy");
        if (blockWrite) File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            _ = await fixture.RunRealtimeCycleAsync();
            var workspace = ReadPrivateField<NativePlacementCoordinatorWorkspace>(fixture.Coordinator,
                "placementCoordinatorWorkspace");
            Assert.NotNull(workspace);
            Assert.Equal((uint)NativePlacementKind.GpuShimPolicy, workspace.Actions[0].PlacementKind);
            Assert.Equal((uint)NativePlacementActionDisposition.Restore, workspace.Actions[0].Disposition);
            if (blockWrite)
            {
                Assert.Single(fixture.StateStore.Current.AppliedPlacements);
                Assert.Equal((uint)NativePlacementFeedbackStatus.RetryableFailure, workspace.Feedback[0].Status);
                Assert.Equal(new byte[] { 2 }, runtime.ReadPolicy("gpu-policy"));
            }
            else
            {
                Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
                Assert.Equal((uint)(applied ? NativePlacementFeedbackStatus.Restored
                    : NativePlacementFeedbackStatus.AlreadySatisfied), workspace.Feedback[0].Status);
                Assert.Equal(new byte[] { 1, 0, 255 }, runtime.ReadPolicy("gpu-policy"));
            }
            Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalCoordinatorPersistsMixedShimPolicySettlements(bool originallyAbsent)
    {
        using var files = new PolicyFiles();
        var ledgerPath = Path.Combine(files.Root, "recovery.json");
        HostManagerAppliedRecord invalid;
        HostManagerAppliedRecord blocked;
        using (var reopenedOwner = new JsonHostManagerRollbackStateStore(
                   ledgerPath, TimeProvider.System, TimeSpan.FromMinutes(1)))
        {
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
                warm: true, scoreOnlyEnabled: false, policyExecutionEnabled: false,
                automaticMemoryCleanupEnabled: false, optimizationMode: AppOptimizationModes.Normal,
                rollbackStateStoreOverride: reopenedOwner);
            var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
            byte[]? previous = originallyAbsent ? null : [1, 0, 255];
            Assert.True(runtime.TryWritePolicy("good", null, previous));
            Assert.True(runtime.TryWritePolicy("blocked", null, []));
            var good = Assert.IsType<HostManagerAppliedRecord>(runtime.CapturePolicyRecord("good", []));
            var captured = Assert.IsType<HostManagerAppliedRecord>(runtime.CapturePolicyRecord("a-invalid", [2]));
            blocked = Assert.IsType<HostManagerAppliedRecord>(runtime.CapturePolicyRecord("blocked", [5]));
            invalid = captured with
            {
                Metadata = new Dictionary<string, string>(captured.Metadata!) { ["appliedValue"] = "!" }
            };

            // The fixture owner has not loaded yet; the seed owner releases its actual file lease first.
            using (var seedOwner = new JsonHostManagerRollbackStateStore(
                       ledgerPath, TimeProvider.System, TimeSpan.FromMinutes(1)))
            {
                var state = await seedOwner.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None);
                await seedOwner.SaveAsync(state with
                {
                    AppliedPlacements = [GpuShimPolicyLedgerTests.Placement(good) with
                    {
                        Records = [good, invalid, blocked]
                    }]
                }, CancellationToken.None);
            }
            Assert.True(runtime.TryApplyPolicyRecord(good));
            Assert.True(runtime.TryApplyPolicyRecord(captured));
            Assert.True(runtime.TryApplyPolicyRecord(blocked));
            var blockedPath = runtime.GetPolicyPath("blocked");
            File.SetAttributes(blockedPath, FileAttributes.ReadOnly);
            try
            {
                // The default budget is one record per cycle; retained rows must not starve later rows.
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    _ = await fixture.RunRealtimeCycleAsync();
                    var duringCycle = await reopenedOwner.LoadAsync(CancellationToken.None);
                    Assert.Equal(cycle < 2 ? 3 : 2, Assert.Single(duringCycle.AppliedPlacements).Records.Count);
                }
                var state = await reopenedOwner.LoadAsync(CancellationToken.None);
                var remaining = Assert.Single(state.AppliedPlacements).Records;
                Assert.Equal(2, remaining.Count);
                Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(invalid),
                    JsonSerializer.SerializeToUtf8Bytes(Assert.Single(remaining, r => r.RecordId == "a-invalid")));
                Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(blocked),
                    JsonSerializer.SerializeToUtf8Bytes(Assert.Single(remaining, r => r.RecordId == "blocked")));
                Assert.Equal(previous, runtime.ReadPolicy("good"));
                Assert.Equal(!originallyAbsent, File.Exists(runtime.GetPolicyPath("good")));
                Assert.Equal(new byte[] { 2 }, runtime.ReadPolicy("a-invalid"));
                Assert.Equal(new byte[] { 5 }, runtime.ReadPolicy("blocked"));
                Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
                Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
            }
            finally { File.SetAttributes(blockedPath, FileAttributes.Normal); }
        }
        using var finalOwner = new JsonHostManagerRollbackStateStore(
            ledgerPath, TimeProvider.System, TimeSpan.FromMinutes(1));
        var finalRecords = Assert.Single((await finalOwner.LoadAsync(CancellationToken.None)).AppliedPlacements).Records;
        Assert.Equal(2, finalRecords.Count);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(invalid),
            JsonSerializer.SerializeToUtf8Bytes(Assert.Single(finalRecords, r => r.RecordId == "a-invalid")));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(blocked),
            JsonSerializer.SerializeToUtf8Bytes(Assert.Single(finalRecords, r => r.RecordId == "blocked")));
    }
}
