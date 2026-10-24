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
    public async Task NormalFailedMemoryRestoreDoesNotStarveMemoryCpuOrPublicLifecycle()
    {
        const int failedId = 24_330, memoryPeerId = 24_331, cpuPeerId = 24_332;
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)started.ToFileTime());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processRecoveryRead: id => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(id, started)),
            realtimeActionBudget: 1);
        await fixture.SeedProcessOwnershipAsync(failedId, startKey, "software:normal-failure", true, 100);
        await fixture.SeedProcessOwnershipAsync(memoryPeerId, startKey, "software:normal-memory-peer", true, 101);
        var payload = HostManagerProcessPolicyRollbackPayloadCodec.Encode(new(
            HostManagerProcessPolicyTransactionFields.CpuPolicy, cpuPeerId, checked((long)startKey),
            BaselinePriorityClass: 0x20, BaselineMemoryPriority: 0,
            BaselinePowerControlMask: 0, BaselinePowerStateMask: 0, TargetMemoryPriority: 0));
        await fixture.SeedProcessOwnershipAsync(cpuPeerId, startKey, "software:normal-cpu-peer", false, 102, payload);
        var states = new[] { failedId, memoryPeerId, cpuPeerId }.ToDictionary(id => id,
            id => new ProcessResourcePolicySnapshot(id, "normal-peer", @"c:\tests\normal-peer.exe", started,
                id == cpuPeerId ? "BelowNormal" : "Normal", 1, 1,
                MemoryPriority: id == cpuPeerId ? 5U : 3U,
                PowerThrottlingControlMask: id == cpuPeerId ? 1U : 0U,
                PowerThrottlingStateMask: id == cpuPeerId ? 1U : 0U));
        fixture.ProcessPolicyWriter.ProcessReadHandler = id => states[id];
        var attempts = new List<int>();
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            attempts.Add(request.ProcessId);
            if (request.ProcessId == failedId)
                return [new(failedId, [new(ProcessResourcePolicyBatchFields.MemoryPriority, false, "denied unchanged")])];
            states[request.ProcessId] = states[request.ProcessId] with
            {
                MemoryPriority = request.MemoryPriority ?? states[request.ProcessId].MemoryPriority,
                PriorityClass = request.PriorityClass ?? states[request.ProcessId].PriorityClass,
                PowerThrottlingControlMask = request.PowerControlMask ?? states[request.ProcessId].PowerThrottlingControlMask,
                PowerThrottlingStateMask = request.PowerStateMask ?? states[request.ProcessId].PowerThrottlingStateMask
            };
            var fields = new List<ProcessResourcePolicyBatchFieldResult>();
            if (request.MemoryPriority is not null) fields.Add(new(ProcessResourcePolicyBatchFields.MemoryPriority, true, "restored"));
            if (request.PriorityClass is not null) fields.Add(new(ProcessResourcePolicyBatchFields.PriorityClass, true, "restored"));
            if (request.PowerControlMask is not null) fields.Add(new(ProcessResourcePolicyBatchFields.PowerThrottling, true, "restored"));
            return [new(request.ProcessId, fields)];
        };
        var publicTicks = fixture.PublicResourceManager.TickCalls;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var before = attempts.Count;
            _ = await fixture.RunRealtimeCycleAsync();
            Assert.Equal(before + 1, attempts.Count);
            Assert.Equal(publicTicks + cycle + 1, fixture.PublicResourceManager.TickCalls);
        }
        Assert.Equal(new[] { failedId, memoryPeerId, cpuPeerId }, attempts);
        Assert.Equal(3U, states[failedId].MemoryPriority);
        Assert.Equal(5U, states[memoryPeerId].MemoryPriority);
        Assert.Equal("Normal", states[cpuPeerId].PriorityClass);
        Assert.Equal(0U, states[cpuPeerId].PowerThrottlingStateMask);
        var retained = Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None)).Records);
        Assert.Equal((uint)failedId, retained.Primary.ProcessId);
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
    }

    [Fact]
    public async Task ExpiredScopeRestoresExactOwnedMemoryThroughPriorHandoff()
    {
        const int processId = 24_340;
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)started.ToFileTime());
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var validation = CoordinatorValidationScopeFixture.Create(new(processId, startKey), time);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processEffectValidationScopeAuthority: validation.Authority,
            processRecoveryRead: id => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(id, started)));
        await fixture.SeedProcessOwnershipAsync(processId, startKey, "software:expired-memory-owner", true, 104);
        var owned = Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None)).Records);
        var source = owned.OriginalBinding.ActionIdentity;
        var identity = new NativeTransactionJournalIdentity
        {
            ConfigurationGeneration = source.ConfigurationGeneration, PlanEpoch = source.PlanEpoch,
            ActionId = source.ActionId, HostSessionIncarnation = source.HostSessionIncarnation,
            TargetId = source.TargetId, SoftwareId = source.SoftwareId,
            ProcessId = source.ProcessId, ProcessStartKey = source.ProcessStartKey
        };
        Assert.True(validation.Authority.TryBeginAdmission(validation.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
            "fresh-apply-pre-ponr", new(processId, startKey), out var permit));
        Assert.True(validation.Authority.TryDeclareNativeTransactionJournal(permit!, identity));
        Assert.True(validation.Authority.TryCommitNativeTransactionJournal(permit!, identity));
        time.Advance(TimeSpan.FromMinutes(11));
        var expired = validation.Authority.Capture();
        Assert.Equal(HostManagerProcessEffectValidationScopeState.ExpiredBlocked, expired.State);
        Assert.False(HostManagerProcessEffectValidationCyclePolicy.AllowsFreshProcessMemory(expired));
        Assert.False(validation.Authority.TryBeginAdmission(expired,
            HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
            "fresh-after-expiry", new(processId, startKey), out _));
        uint current = 3;
        fixture.ProcessPolicyWriter.ProcessReadHandler = id => new(id, "expired-owner", @"c:\tests\expired-owner.exe",
            started, "Normal", 1, 1, MemoryPriority: current);
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(processId, request.ProcessId);
            Assert.Equal(5U, request.MemoryPriority);
            current = 5;
            return [new(processId, [new(ProcessResourcePolicyBatchFields.MemoryPriority, true, "restored")])];
        };
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(5U, current);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None)).Records);
        Assert.Contains(validation.Authority.GetStatus().AuditRecords, record => record.Decision == "allowedPriorHandoffRecovery");
    }
}
