using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MemoryModesConsumeOnlyPhysicalScoreMembers(
        bool removeScalarCpu,
        bool partialPhysicalObservation)
    {
        const int pid = 4243;
        const ulong start = 132_537_599_900_000_000;
        const string software = "software:physical-memory-mode";
        var facts = CreateCompleteProcessFacts(
            (pid, start, software, 30, 99, 25),
            (pid + 1, start + 1, software, 100, 99, 10),
            (pid + 2, start + 2, "software:other-memory-mode", 100, 99, 10));
        if (removeScalarCpu) facts = WithoutScalarCpu(facts);
        var observed = new List<CpuProcessCoreResidency>();
        observed.Add(CpuCoreResidencyTestValues.Process(pid, start, ("core:0", 50)));
        if (!partialPhysicalObservation)
        {
            observed.Add(CpuCoreResidencyTestValues.Process(pid + 1, start + 1, ("core:0", 10)));
            observed.Add(CpuCoreResidencyTestValues.Process(pid + 2, start + 2, ("core:0", 10)));
        }
        CpuCoreResidencySnapshot? current = CpuCoreResidencyTestValues.Create(
            91, DateTimeOffset.UtcNow, observed.ToArray());
        var reader = new RecordingCpuCoreResidencyReader(() => current);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: facts,
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            failFastEffects: true,
            cpuCoreReader: reader);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var authority = fixture.Coordinator.SchedulingAuthority;
        Assert.True(authority.Availability == HostManagerSchedulingAuthorityAvailability.Ready,
            authority.UnavailableReason);
        var cpu = Assert.IsType<HostManagerComputeScoreDomainSnapshot>(authority.Compute!.Cpu);
        Assert.Equal(91UL, cpu.SourceGeneration);
        Assert.Equal(facts.Generation, cpu.SourceIdentity.InventoryGeneration);
        var processScores = cpu.Scores.Where(score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessCpu).ToArray();
        Assert.Equal(partialPhysicalObservation ? 1 : 3, processScores.Length);
        var modes = Assert.IsType<HostManagerMemoryModeDesiredSnapshot>(authority.MemoryModes);
        Assert.Equal(partialPhysicalObservation ? 1 : 2, modes.Software.Length);
        var softwareMode = Assert.Single(modes.Software,
            row => row.SoftwareKey == NativeStableIdentity.CreateCaseInsensitiveKey(software));
        Assert.Equal(partialPhysicalObservation ? 30 : 100, softwareMode.BaseScore);
        var projection = Assert.IsType<HostManagerNonAdaptedMemoryModeProjectionSnapshot>(
            fixture.Coordinator.NonAdaptedMemoryModeProjection);
        Assert.Equal(facts.Generation, projection.InventoryGeneration);
        Assert.Equal(cpu.SourceGeneration, projection.CpuSourceGeneration);
        var batch = Assert.IsType<HostManagerNonAdaptedMemoryExecutionBatch>(
            HostManagerNonAdaptedMemoryModeExecutionAdmission.Admit(
                authority, projection, authority.PlanBinding!, facts));
        Assert.Equal(processScores.Select(row => row.ProcessId).Order(),
            batch.Processes.Select(row => row.ProcessId).Order());

        current = null;
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Null(fixture.Coordinator.SchedulingAuthority.Compute?.Cpu);
        Assert.Null(fixture.Coordinator.NonAdaptedMemoryModeProjection);
        Assert.Equal(2, reader.ReadCount);
        Assert.Empty(reader.Subscriptions);
        Assert.Equal(0, fixture.ProcessFacts.CaptureCalls);
        Assert.Equal(0, fixture.MetricSampler.CaptureCalls);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }
}
