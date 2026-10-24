using System.Globalization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task NormalFailedCpuRestoreAllowsPublicResourceEffectsAndKeepsItsExactOwner()
    {
        const int processId = 24_350;
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)started.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processRecoveryRead: id => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(id, started)),
            realtimeActionBudget: 1);
        var payload = HostManagerProcessPolicyRollbackPayloadCodec.Encode(new(
            HostManagerProcessPolicyTransactionFields.CpuPolicy, processId, checked((long)startKey),
            BaselinePriorityClass: 0x20, BaselineMemoryPriority: 0,
            BaselinePowerControlMask: 0, BaselinePowerStateMask: 0, TargetMemoryPriority: 0));
        var seeded = await fixture.SeedProcessOwnershipAsync(
            processId, startKey, "software:failed-cpu-public-progress", false, 110, payload);
        fixture.ProcessPolicyWriter.ProcessReadHandler = id => new(
            id, "failed-cpu", @"c:\tests\failed-cpu.exe", started, "BelowNormal", 1, 1,
            MemoryPriority: 5, PowerThrottlingControlMask: 1, PowerThrottlingStateMask: 1);
        var cpuAttempts = 0;
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(processId, request.ProcessId);
            Assert.Equal(started, request.ExpectedStartedAt);
            Assert.Equal("Normal", request.PriorityClass);
            Assert.Equal(0U, request.PowerControlMask);
            Assert.Equal(0U, request.PowerStateMask);
            Assert.Null(request.MemoryPriority);
            cpuAttempts++;
            return [new(processId,
            [
                new(ProcessResourcePolicyBatchFields.PriorityClass, false, "denied unchanged"),
                new(ProcessResourcePolicyBatchFields.PowerThrottling, false, "denied unchanged")
            ])];
        };
        fixture.PublicResourceManager.PendingNewEffectCandidates = 3;
        var publicAttemptsBefore = fixture.PublicResourceManager.NewEffectAttemptCount;

        for (var cycle = 0; cycle < 6; cycle++)
        {
            var cpuBefore = cpuAttempts;
            var publicBefore = fixture.PublicResourceManager.NewEffectAttemptCount;
            _ = await fixture.RunRealtimeCycleAsync();
            Assert.InRange(cpuAttempts - cpuBefore, 0, 1); // Recovery lane.
            Assert.InRange(fixture.PublicResourceManager.NewEffectAttemptCount - publicBefore, 0U, 1U);
            Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
        }

        Assert.True(cpuAttempts >= 2, "The failed CPU owner must continue receiving recovery opportunities.");
        Assert.Equal(publicAttemptsBefore + 3, fixture.PublicResourceManager.NewEffectAttemptCount);
        Assert.Equal(0, fixture.PublicResourceManager.PendingNewEffectCandidates);
        var retained = Assert.Single((await fixture.NativeTransactions.AppliedOwnership
            .ReadSnapshotAsync(CancellationToken.None)).Records);
        Assert.Equal(seeded.Primary, retained.Primary);
        Assert.Equal((int)NativeTransactionJournalProcessGrade.Level2, retained.CurrentGrades.ProcessGrade);
        Assert.True(File.Exists(seeded.PayloadPath));
    }

    [Fact]
    public async Task NormalFailedLegacyPlacementDoesNotStarveExactOwnedMemoryRestoration()
    {
        const int placementId = 24_360, memoryId = 24_361;
        const string processName = "failed-placement";
        const string executablePath = @"c:\tests\failed-placement.exe";
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)started.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processRecoveryRead: id => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(id, started)),
            realtimeActionBudget: 1);
        // Initialize legacy state before inserting a current owned receipt.
        _ = await fixture.RunRealtimeCycleAsync();
        var targetId = HostManagerTargetIdentity.CreateProcessTargetId(placementId, startKey);
        var record = new HostManagerAppliedRecord(
            HostManagerAppliedRecordKinds.CpuAffinity, "failed-placement-cpu-sets",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["owner"] = "host-manager-automatic-placement-v1",
                ["processId"] = placementId.ToString(CultureInfo.InvariantCulture),
                ["processStartKey"] = startKey.ToString(CultureInfo.InvariantCulture),
                ["processStartedAt"] = started.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
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
                new(targetId, processName, "software:failed-placement", OptimizationResourceKinds.Cpu,
                    [record], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]
        };
        var memoryOwner = await fixture.SeedProcessOwnershipAsync(
            memoryId, startKey, "software:placement-memory-peer", true, 111);
        fixture.ProcessPolicyWriter.PlacementReadHandler = (id, fields) =>
        {
            Assert.Equal(placementId, id);
            Assert.Equal(ProcessPlacementReadFields.DefaultCpuSets, fields);
            return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(new(
                placementId, started, processName, executablePath, null, [1U, 2U], null, null));
        };
        uint memoryPriority = 3;
        fixture.ProcessPolicyWriter.ProcessReadHandler = id =>
        {
            Assert.Equal(memoryId, id);
            return new(id, "memory-peer", @"c:\tests\memory-peer.exe", started, "Normal", 1, 1,
                MemoryPriority: memoryPriority);
        };
        var placementAttempts = 0;
        var memoryAttempts = 0;
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            if (request.ProcessId == placementId)
            {
                Assert.Equal(new uint[] { 1, 2, 3, 4 }, request.CpuSetIds);
                Assert.Null(request.MemoryPriority);
                placementAttempts++;
                return [new(placementId,
                    [new(ProcessResourcePolicyBatchFields.CpuSets, false, "denied unchanged")])];
            }
            Assert.Equal(memoryId, request.ProcessId);
            Assert.Equal(3U, request.ExpectedMemoryPriority);
            Assert.Equal(5U, request.MemoryPriority);
            Assert.Null(request.CpuSetIds);
            memoryAttempts++;
            memoryPriority = 5;
            return [new(memoryId,
                [new(ProcessResourcePolicyBatchFields.MemoryPriority, true, "restored")])];
        };

        for (var cycle = 0; cycle < 4; cycle++)
        {
            var attemptsBefore = placementAttempts + memoryAttempts;
            _ = await fixture.RunRealtimeCycleAsync();
            Assert.InRange(placementAttempts + memoryAttempts - attemptsBefore, 0, 1);
        }

        Assert.True(placementAttempts >= 2, "A retained placement must receive later retry opportunities.");
        Assert.Equal(1, memoryAttempts);
        Assert.Equal(5U, memoryPriority);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership
            .ReadSnapshotAsync(CancellationToken.None)).Records);
        Assert.False(File.Exists(memoryOwner.PayloadPath));
        Assert.Equal(record, Assert.Single(Assert.Single(fixture.StateStore.Current.AppliedPlacements).Records));
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
    }
}
