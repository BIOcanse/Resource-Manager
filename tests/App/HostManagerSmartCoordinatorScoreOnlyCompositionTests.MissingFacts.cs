using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task AdapterExclusionsShareFactReasonCountsAndKeepExactAdapterDetails()
    {
        var source = CreateCompleteProcessFacts(4242, 81, "software:test", 80, 25, 10);
        source = source with
        {
            FactExclusions =
            [
                new(4242, 81, null, SchedulingFactCode.GpuUsage, SchedulingFactExclusionReason.GpuRowMissingOrInvalid, 9_007_199_254_740_993),
                new(4242, 81, null, SchedulingFactCode.GpuUsage, SchedulingFactExclusionReason.GpuRowMissingOrInvalid, ulong.MaxValue)
            ]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsOverride: new RetainedFacts(source));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        var count = Assert.Single(diagnostics.FactExclusionCounts, e => e.Fact == SchedulingFactCode.GpuUsage
            && e.Reason == SchedulingFactExclusionReason.GpuRowMissingOrInvalid);
        Assert.Equal(2, count.Count);
        Assert.Equal(new[] { "9007199254740993", "18446744073709551615" }, diagnostics.FactExclusions
            .Where(e => e.Fact == count.Fact && e.Reason == count.Reason).Select(e => e.AdapterKey));
        Assert.All(diagnostics.FactExclusions, e =>
        {
            Assert.True(Enum.IsDefined(e.Fact));
            Assert.True(Enum.IsDefined(e.Reason));
        });
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("identity")]
    [InlineData("cpu")]
    [InlineData("base-score")]
    [InlineData("overflow")]
    [InlineData("accounting")]
    public async Task OneBadFactLeavesPeerScoredAndPlannedInTheSameRound(string failure)
    {
        var source = CreateCompleteProcessFacts((4242, 100, "software:bad", 20, 10, 5),
            (4243, 101, "software:good", 20, 20, 5));
        var bad = source.Processes[0];
        source = failure switch
        {
            "runtime" => source with { Processes = [bad with { HasHiddenWindow = true }, source.Processes[1]] },
            "identity" => source with { Processes = [bad with { ProcessStartKey = 0 }, source.Processes[1]] },
            "cpu" => source with { Processes = [bad with { CpuUsagePercent = double.NaN }, source.Processes[1]] },
            "base-score" => source with { Processes = [bad with { BaseScore = double.NaN }, source.Processes[1]] },
            "overflow" => source with { OverflowCount = 1 },
            _ => source with { EnumeratedCount = 9 }
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: source, memoryModePolicyEnabled: true,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var authority = fixture.Coordinator.SchedulingAuthority;
        Assert.True(authority.Availability == HostManagerSchedulingAuthorityAvailability.Ready, authority.UnavailableReason);
        Assert.Contains(authority.Compute!.Cpu!.Scores, s => s.ProcessId == 4243);
        Assert.True(fixture.Workspace.Snapshot.CycleSequence > 0); // Native planning also ran, including initial ownership publication.
        if (failure is "runtime" or "identity" or "cpu" or "base-score")
            Assert.DoesNotContain(authority.Compute.Cpu.Scores, s => s.ProcessId == 4242);
        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.Contains(diagnostics.NonAdaptedMemoryDecisions, d => d.ProcessId == 4243);
        Assert.True(diagnostics.FactExclusionCount > 0);
        Assert.Contains(diagnostics.FactExclusions, e => failure is "overflow" or "accounting"
            ? e.Fact == SchedulingFactCode.Inventory : e.ProcessId == 4242);
        Assert.NotNull(diagnostics.SchedulingAuthority.MemoryModes);
    }

    [Fact]
    public async Task NonCurrentGpuDatasetLeavesCpuAndMemoryPlanningRunning()
    {
        var source = CreateCompleteProcessFacts(4243, 101, "software:good", 20, 20, 5);
        source = source with
        {
            RequestedMetricMask = source.RequestedMetricMask | SchedulingProcessMetricMask.GpuUsage,
            CurrentMetricMask = source.CurrentMetricMask & ~SchedulingProcessMetricMask.GpuUsage
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: source, memoryModePolicyEnabled: true,
            processFactsOverride: new RetainedFacts(source));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var authority = fixture.Coordinator.SchedulingAuthority;
        Assert.True(authority.Availability == HostManagerSchedulingAuthorityAvailability.Ready, authority.UnavailableReason);
        Assert.NotNull(authority.Compute!.Cpu);
        Assert.Null(authority.Compute.Gpu);
        Assert.NotNull(authority.MemoryModes);
        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.Contains(diagnostics.FactExclusions, e => e.Fact == SchedulingFactCode.GpuUsage && e.Reason == SchedulingFactExclusionReason.DatasetNotCurrent);
    }

    [Fact]
    public async Task ExclusionDiagnosticsCountAllRowsAndBoundTheExamples()
    {
        var source = CreateScaleProcessFacts(100);
        source = source with { Processes = source.Processes.Select(p => p with { HasHiddenWindow = true }).ToArray() };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, processFactsSnapshot: source);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.True(diagnostics.FactExclusionCount >= 100);
        Assert.Equal(64, diagnostics.FactExclusions.Count);
        Assert.Contains(diagnostics.FactExclusionCounts, c => c.Fact == SchedulingFactCode.RuntimeState && c.Count == 100);
    }
}
