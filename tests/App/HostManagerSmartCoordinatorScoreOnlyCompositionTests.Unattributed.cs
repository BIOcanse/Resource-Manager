using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task UnattributedProcessWithSampledFactsRemainsEligibleForSmartScheduling()
    {
        var source = CreateCompleteProcessFacts(78776, 81, "unattributed", 30, 25, 10);
        source = source with { Processes = [source.Processes[0] with { SoftwareKind = SoftwareKinds.Unattributed }] };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, processFactsOverride: new RetainedFacts(source));
        await fixture.Coordinator.RunOnceAsync(default);
        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(default);
        Assert.Contains(fixture.Coordinator.SchedulingAuthority.Compute!.Cpu!.Scores, score => score.ProcessId == 78776);
        Assert.DoesNotContain(diagnostics.FactExclusions, exclusion => exclusion.ProcessId == 78776
            && exclusion.Reason == SchedulingFactExclusionReason.LiveIdentityWithoutDecisionFacts);
    }
}
