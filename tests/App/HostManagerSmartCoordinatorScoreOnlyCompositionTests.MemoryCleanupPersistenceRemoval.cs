using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryCleanupRestartIgnoresOldAttemptFilesAndSelectsSameProcess(bool oldFilesExist)
    {
        var request = CreateAutomaticMemoryCleanupRequest(candidateCount: 1);
        var first = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false);
        var root = first.Root;
        try
        {
            first.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
            first.ProcessPolicyWriter.BatchHandler = static _ => throw new IOException("unknown trim outcome");
            var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
            admission.InitializeBudgets(1, 1);
            Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.AutomaticMemoryCleanup, out var permit));
            var result = InvokeExecuteMemoryCleanup(first.Coordinator, permit, request, CancellationToken.None);
            Assert.Equal(HostManagerMemoryCleanupRoundDisposition.BatchOutcomeUnknown, result.Disposition);
            Assert.False(result.HasUnresolvedCurrentEffects);
            Assert.All(LegacyMemoryCleanupPaths(root), path => Assert.False(File.Exists(path)));
            first.PreserveRootOnDispose = true;
        }
        finally
        {
            await first.DisposeAsync();
        }

        var paths = LegacyMemoryCleanupPaths(root);
        var locks = new List<FileStream>();
        const string oldImage = "old cleanup attempts are deliberately unreadable and invalid";
        try
        {
            if (oldFilesExist)
            {
                foreach (var path in paths)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, oldImage);
                    locks.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                }
            }
            await using var restarted = await ScoreOnlyCoordinatorFixture.CreateAsync(
                warm: true, scoreOnlyEnabled: false, rootOverride: root);
            // Release our test handles before the fixture removes its temporary installation.
            try
            {
                await restarted.Coordinator.RunOnceAsync(CancellationToken.None);
                restarted.MemoryCleanupPlanner.PlanResult = CreateAutomaticMemoryCleanupPlan(request);
                restarted.ProcessPolicyWriter.BatchHandler = SuccessfulTrimResults;
                var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
                admission.InitializeBudgets(1, 1);
                Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.AutomaticMemoryCleanup, out var permit));
                var result = InvokeExecuteMemoryCleanup(restarted.Coordinator, permit, request, CancellationToken.None);
                Assert.Equal(HostManagerMemoryCleanupRoundDisposition.CompleteKnownFull, result.Disposition);
                Assert.Equal(Assert.Single(request.Candidates).ProcessId,
                    Assert.Single(restarted.ProcessPolicyWriter.BatchRequests).ProcessId);
                Assert.Equal(request.Candidates, restarted.MemoryCleanupPlanner.LastRequest!.Candidates);
                if (!oldFilesExist) Assert.All(paths, path => Assert.False(File.Exists(path)));
            }
            finally
            {
                foreach (var handle in locks) handle.Dispose();
                locks.Clear();
            }
            if (oldFilesExist) Assert.All(paths, path => Assert.Equal(oldImage, File.ReadAllText(path)));
        }
        finally
        {
            foreach (var handle in locks) handle.Dispose();
        }
    }

    [Fact]
    public async Task ValidationScopeBeginIgnoresLockedCleanupAttemptFiles()
    {
        var identity = new HostManagerComputeProcessIdentity(25_010, 132_537_601_000_000_000);
        using var validation = CoordinatorValidationScopeFixture.Create(identity);
        _ = validation.Authority.Close(validation.CreateCloseRequest());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false, optimizationMode: AppOptimizationModes.Normal,
            processRecoveryRead: static _ => throw new InvalidOperationException("Scope begin must not inspect cleanup attempts."),
            processEffectValidationScopeAuthority: validation.Authority);
        var locks = new List<FileStream>();
        try
        {
            foreach (var path in LegacyMemoryCleanupPaths(fixture.Root))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "unreadable old cleanup attempt");
                locks.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            }
            var opened = await fixture.Coordinator.OpenProcessEffectValidationScopeAsync(new(
                Guid.NewGuid(), $"Global\\ResourceManager-NonAdaptedOptimizationLab-{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow.AddMinutes(10), new string('a', 64),
                [new(identity.ProcessId, checked((long)identity.ProcessStartKey))],
                AllowAutomaticMemoryCleanup: true), CancellationToken.None);
            Assert.Equal("active", opened.State);
            Assert.Equal(0, fixture.ProcessPolicyWriter.RecoveryReadCalls);
            Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
            var closed = await fixture.Coordinator.CloseProcessEffectValidationScopeAsync(
                new(opened.ScopeId!.Value, opened.RunNonce!.Value, new string('a', 64)), CancellationToken.None);
            Assert.Equal("closed", closed.State);
        }
        finally
        {
            foreach (var handle in locks) handle.Dispose();
        }
    }

    private static string[] LegacyMemoryCleanupPaths(string root)
    {
        var canonical = Path.Combine(root, "UserData", "HostManager", "MemoryCleanup",
            "generation-00000001", "host-manager-memory-cleanup-attempts.json");
        var oldTestPath = Path.Combine(root, "memory-cleanup-attempts.json");
        return [canonical, canonical + ".root.manifest", oldTestPath, oldTestPath + ".root.manifest"];
    }

    private static IReadOnlyList<ProcessResourcePolicyBatchWriteResult> SuccessfulTrimResults(
        IReadOnlyList<ProcessResourcePolicyBatchRequest> requests)
        => requests.Select(static item => new ProcessResourcePolicyBatchWriteResult(item.ProcessId,
            [new ProcessResourcePolicyBatchFieldResult(ProcessResourcePolicyBatchFields.TrimWorkingSet,
                Succeeded: true, "trimmed")])).ToArray();
}
