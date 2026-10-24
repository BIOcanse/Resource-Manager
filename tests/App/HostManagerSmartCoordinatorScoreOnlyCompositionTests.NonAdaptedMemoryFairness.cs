using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task NormalRestoresOwnedParentWithoutAdoptingAnUnownedChildsInheritedMemoryPriority()
    {
        const int parentId = 24_320, childId = 24_321;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var startKey = checked((ulong)startedAt.ToFileTime());
        const string software = "software:memory-inheritance-parent";
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                (parentId, startKey, software, 35D, 1D, 25D),
                (childId, startKey, software + ":child", 35D, 1D, 25D)),
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(pid, startedAt)),
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal);
        _ = await fixture.SeedProcessOwnershipAsync(
            parentId, startKey, software, memoryPolicy: true, actionId: 24_320);
        // The child starts with an inherited value but has no product owner/baseline.
        var states = new[] { parentId, childId }.ToDictionary(pid => pid,
            pid => new ProcessResourcePolicySnapshot(pid, $"inheritance-{pid}",
                $@"c:\tests\inheritance-{pid}.exe", startedAt, "Normal",
                ProcessorAffinityMask: 1, LogicalProcessorCount: 1, MemoryPriority: 3));
        fixture.ProcessPolicyWriter.ProcessReadHandler = pid => states[pid];
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(parentId, request.ProcessId);
            Assert.Equal(5U, request.MemoryPriority);
            Assert.False(request.TrimWorkingSet);
            states[parentId] = states[parentId] with { MemoryPriority = request.MemoryPriority };
            return [new ProcessResourcePolicyBatchWriteResult(parentId,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.MemoryPriority, true, "restored owned parent")])];
        };
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(5U, states[parentId].MemoryPriority);
        Assert.Equal(3U, states[childId].MemoryPriority);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);
    }

    [Fact]
    public async Task UnavailableOwnedMemoryOwnerDoesNotStarveFreshPeersOrFollowingEffectRound()
    {
        const int ownerProcessId = 24_300;
        const int freshPeerCount = 4;
        var processStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var inputs = Enumerable.Range(0, freshPeerCount + 1)
            .Select(index => (
                ProcessId: ownerProcessId + index,
                ProcessStartKey: processStartKey,
                SoftwareId: $"software:unavailable-memory-owner:{index}",
                BaseScore: 35D,
                CpuUsagePercent: 1D,
                MemoryUsagePercent: 25D))
            .ToArray();
        var processFactsBase = CreateCompleteProcessFacts(inputs);
        var processFacts = processFactsBase with
        {
            Processes = processFactsBase.Processes
                .Select(static process => process with
                {
                    SoftwareKind = SoftwareKinds.Other,
                    SoftwareDisplayKind = SoftwareKinds.Other
                })
                .ToArray()
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: processFacts,
            processRecoveryRead: processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, processStartedAt)),
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.MemoryOnly,
            cpuUsagePercent: 1,
            realtimeActionBudget: 1);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            memoryUsagePercent: 100,
            cpuUsagePercent: 1));
        fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
            fixture.ProcessFacts.Snapshot,
            fixture.MetricSampler.Snapshot));
        var seeded = await fixture.SeedProcessOwnershipAsync(
            ownerProcessId,
            processStartKey,
            inputs[0].SoftwareId,
            memoryPolicy: true,
            actionId: 24_300);
        var states = inputs.ToDictionary(
            static input => input.ProcessId,
            input => new ProcessResourcePolicySnapshot(
                input.ProcessId,
                $"unavailable-owner-{input.ProcessId}",
                $@"c:\tests\unavailable-owner-{input.ProcessId}.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: 5));
        var freshWrites = new List<int>();
        fixture.ProcessPolicyWriter.ProcessReadHandler = processId =>
            processId == ownerProcessId ? null : states[processId];
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests
            .Select(request =>
            {
                var target = Assert.IsType<uint>(request.MemoryPriority);
                Assert.Equal(3U, target);
                Assert.False(request.TrimWorkingSet);
                var current = states[request.ProcessId];
                states[request.ProcessId] = current with { MemoryPriority = target };
                freshWrites.Add(request.ProcessId);
                return new ProcessResourcePolicyBatchWriteResult(
                    request.ProcessId,
                    [new ProcessResourcePolicyBatchFieldResult(
                        ProcessResourcePolicyBatchFields.MemoryPriority,
                        Succeeded: true,
                        "memory priority applied")]);
            })
            .ToArray();

        for (var round = 0; round < freshPeerCount; round++)
        {
            var writesBefore = freshWrites.Count;
            _ = await fixture.RunRealtimeCycleAsync();
            Assert.Equal(writesBefore + 1, freshWrites.Count);
            Assert.DoesNotContain(ownerProcessId, freshWrites);
        }

        Assert.Equal(
            inputs.Skip(1).Select(static input => input.ProcessId)
                .OrderBy(static processId => processId).ToArray(),
            freshWrites.OrderBy(static processId => processId).ToArray());
        Assert.True(File.Exists(seeded.PayloadPath));
        var owned = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        Assert.Equal(freshPeerCount + 1, owned.Records.Count);
        Assert.Contains(owned.Records, record =>
            record.Primary.ProcessId == checked((uint)ownerProcessId));

        fixture.PublicResourceManager.PendingNewEffectCandidates = 1;
        var publicTicksBefore = fixture.PublicResourceManager.TickCalls;
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal(publicTicksBefore + 1, fixture.PublicResourceManager.TickCalls);
        Assert.Equal(1U, fixture.PublicResourceManager.NewEffectAttemptCount);
        Assert.DoesNotContain(ownerProcessId, freshWrites);
    }

    [Fact]
    public async Task UnavailableOwnedMemoryRestoreRetainsUnknownOwnerAndContinuesPublicLifecycle()
    {
        const int processId = 24_305;
        var processStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var softwareId = $"software:unavailable-memory-restore:{processId}";
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                processStartKey,
                softwareId,
                baseScore: 35,
                cpuUsagePercent: 1,
                memoryUsagePercent: 25),
            processRecoveryRead: requestedProcessId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(requestedProcessId, processStartedAt)),
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal);
        var seeded = await fixture.SeedProcessOwnershipAsync(
            processId,
            processStartKey,
            softwareId,
            memoryPolicy: true,
            actionId: 24_305);
        fixture.ProcessPolicyWriter.ProcessReadHandler = requestedProcessId =>
        {
            Assert.Equal(processId, requestedProcessId);
            return null;
        };
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException(
                "An unavailable exact process state must not be overwritten.");
        fixture.PublicResourceManager.PendingNewEffectCandidates = 1;
        var publicTicksBefore = fixture.PublicResourceManager.TickCalls;

        _ = await fixture.RunRealtimeCycleAsync();

        var ownership = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        var owner = Assert.Single(ownership.Records);
        Assert.Equal(checked((uint)processId), owner.Primary.ProcessId);
        Assert.True(File.Exists(seeded.PayloadPath));
        var journal = await fixture.ReadJournalSnapshotAsync();
        var record = Assert.Single(journal.Records);
        Assert.Equal(
            (uint)NativeTransactionJournalPhase.EffectInvocationUncertain,
            record.Phase);
        Assert.Equal(1U, journal.Header.EffectInvocationUncertainCount);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(publicTicksBefore + 1, fixture.PublicResourceManager.TickCalls);
        Assert.Equal(1U, fixture.PublicResourceManager.NewEffectAttemptCount);
    }

    [Fact]
    public async Task MissingOwnedMemoryPayloadStillFailsClosedBeforePeerWrites()
    {
        const int ownerProcessId = 24_310;
        const int freshProcessId = 24_311;
        var processStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var processStartKey = checked((ulong)processStartedAt.ToFileTime());
        var ownerSoftwareId = $"software:missing-memory-payload:{ownerProcessId}";
        var freshSoftwareId = $"software:missing-memory-payload:{freshProcessId}";
        var inputs = new[]
        {
            (ownerProcessId, processStartKey, ownerSoftwareId, 35D, 1D, 25D),
            (freshProcessId, processStartKey, freshSoftwareId, 35D, 1D, 25D)
        };
        var factsBase = CreateCompleteProcessFacts(inputs);
        var facts = factsBase with
        {
            Processes = factsBase.Processes
                .Select(static process => process with
                {
                    SoftwareKind = SoftwareKinds.Other,
                    SoftwareDisplayKind = SoftwareKinds.Other
                })
                .ToArray()
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: facts,
            processRecoveryRead: processId =>
                RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                    new ProcessInstanceRecoverySnapshot(processId, processStartedAt)),
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.MemoryOnly,
            cpuUsagePercent: 1);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(
            memoryUsagePercent: 100,
            cpuUsagePercent: 1));
        fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
            fixture.ProcessFacts.Snapshot,
            fixture.MetricSampler.Snapshot));
        var seeded = await fixture.SeedProcessOwnershipAsync(
            ownerProcessId,
            processStartKey,
            ownerSoftwareId,
            memoryPolicy: true,
            actionId: 24_310);
        var states = new Dictionary<int, ProcessResourcePolicySnapshot>
        {
            [ownerProcessId] = new(
                ownerProcessId,
                "missing-memory-payload-owner",
                @"c:\tests\missing-memory-payload-owner.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: 3),
            [freshProcessId] = new(
                freshProcessId,
                "missing-memory-payload-fresh",
                @"c:\tests\missing-memory-payload-fresh.exe",
                processStartedAt,
                "Normal",
                ProcessorAffinityMask: 1,
                LogicalProcessorCount: 1,
                MemoryPriority: 5)
        };
        fixture.ProcessPolicyWriter.ProcessReadHandler = processId => states[processId];
        fixture.ProcessPolicyWriter.BatchHandler = static _ =>
            throw new InvalidOperationException(
                "A shared durable payload failure must stop every memory write.");
        fixture.PublicResourceManager.PendingNewEffectCandidates = 1;
        var publicTicksBefore = fixture.PublicResourceManager.TickCalls;
        Assert.True(File.Exists(seeded.PayloadPath));
        File.Delete(seeded.PayloadPath);

        _ = await Assert.ThrowsAnyAsync<Exception>(
            async () => _ = await fixture.RunRealtimeCycleAsync());

        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(publicTicksBefore, fixture.PublicResourceManager.TickCalls);
        Assert.Equal(0U, fixture.PublicResourceManager.NewEffectAttemptCount);
        var ownership = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None);
        Assert.Contains(ownership.Records, record =>
            record.Primary.ProcessId == checked((uint)ownerProcessId));
    }
}
