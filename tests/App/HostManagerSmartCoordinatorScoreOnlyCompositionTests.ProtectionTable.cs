using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    // scheduling_protection_2026-10-29 §3.2 rule 3: the places that read protection stay the same, protection only
    // gains a source. Native takes each target's level from the one table that resolves process, software, hosted
    // services and kind, so catalog, system-default and shared-service entries reach central scheduling.
    private const ulong ProtectionTableStartKey = 132_537_599_910_000_000;

    [Fact]
    public async Task WindowsServiceWithoutAUserEntryReachesNativeAsNoFreezeFromTheSystemDefault()
    {
        const int pid = 24061;
        var rows = await CaptureProtectionRowsAsync(pid, SoftwareKinds.WindowsService, [], Defaults());

        Assert.Equal(OptimizationProtectionLevels.NoFreeze, ProtectionLevelOf(rows, pid));
    }

    [Fact]
    public async Task CatalogPerProcessNoFreezeEntryReachesNative()
    {
        const int pid = 24062;
        var name = $"cycle-budget-{pid}";
        var entry = ProtectionEntry(OptimizationProtectionTargetTypes.Process, "process-name:" + name,
            OptimizationProtectionSources.SoftwareCatalog, OptimizationProtectionLevels.NoFreeze) with { ProcessName = name };

        var rows = await CaptureProtectionRowsAsync(pid, "general", [], [.. Defaults(), entry]);

        Assert.Equal(OptimizationProtectionLevels.NoFreeze, ProtectionLevelOf(rows, pid));
    }

    [Theory]
    [InlineData(OptimizationProtectionTargetTypes.Software)]
    [InlineData(OptimizationProtectionTargetTypes.SoftwareKind)]
    public async Task UserEntryThatSwitchesADefaultOffReachesNativeAsNone(string targetType)
    {
        const int pid = 24063;
        var defaults = Defaults();
        // A user entry at 0 on the software itself, or the user's change of the kind default (same entry id).
        var off = targetType == OptimizationProtectionTargetTypes.Software
            ? ProtectionEntry(targetType, "software:" + ProtectionTableSoftware(pid),
                OptimizationProtectionSources.ProtectionList, OptimizationProtectionLevels.None)
                with { SoftwareId = ProtectionTableSoftware(pid) }
            : SwitchedOff(KindDefault(defaults, SoftwareKinds.WindowsService));

        var rows = await CaptureProtectionRowsAsync(pid, SoftwareKinds.WindowsService, [],
            [.. defaults.Where(entry => entry.Id != off.Id), off]);

        Assert.Equal(OptimizationProtectionLevels.None, ProtectionLevelOf(rows, pid));
    }

    [Theory]
    [InlineData(true, OptimizationProtectionLevels.NoFreeze)]
    [InlineData(false, OptimizationProtectionLevels.None)]
    public async Task SharedServiceHostResolvesItsMembersProtectionLikeTheirBaseScores(
        bool memberProtected, int expected)
    {
        const int pid = 24064;
        var defaults = Defaults();
        // The host's kind default is switched off, so only a hosted member's own entry can protect it, the same
        // member lookup (windows-service:<name>) the base-score chain uses for a shared host's catalog score.
        var kindOff = SwitchedOff(KindDefault(defaults, SoftwareKinds.WindowsService));
        var member = WindowsServiceSoftwareIds.ForService("BITS");
        ProtectedOptimizationTarget[] entries = memberProtected
            ? [.. defaults.Where(entry => entry.Id != kindOff.Id), kindOff,
                ProtectionEntry(OptimizationProtectionTargetTypes.Software, "software:" + member,
                    OptimizationProtectionSources.SoftwareCatalog, OptimizationProtectionLevels.NoFreeze)
                    with { SoftwareId = member, SoftwareKind = SoftwareKinds.WindowsService }]
            : [.. defaults.Where(entry => entry.Id != kindOff.Id), kindOff];

        var rows = await CaptureProtectionRowsAsync(pid, SoftwareKinds.WindowsService, ["Schedule", "BITS"], entries);

        Assert.Equal(expected, ProtectionLevelOf(rows, pid));
    }

    private static async Task<NativeSmartCoordinatorInputRow[]> CaptureProtectionRowsAsync(
        int pid,
        string softwareKind,
        IReadOnlyList<string> serviceNames,
        IReadOnlyList<ProtectedOptimizationTarget> entries)
    {
        var facts = CreateCompleteProcessFacts(pid, ProtectionTableStartKey, ProtectionTableSoftware(pid), 20, 1, 25);
        facts = facts with
        {
            Processes = [.. facts.Processes.Select(process => process with
            {
                SoftwareKind = softwareKind,
                ServiceNames = serviceNames
            })]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, processFactsSnapshot: facts);
        fixture.ProtectionService.Targets = entries;
        return await CaptureIdentityRowsAsync(fixture);
    }

    private static int ProtectionLevelOf(NativeSmartCoordinatorInputRow[] rows, int pid)
        => Assert.Single(rows, row => row.ProcessId == pid).ProtectionLevel;

    private static string ProtectionTableSoftware(int pid) => $"software:protection-table-{pid}";

    private static ProtectedOptimizationTarget[] Defaults()
        => OptimizationProtectionDefaults.Create(SoftwarePolicyProfile.Empty, DateTimeOffset.UtcNow);

    private static ProtectedOptimizationTarget KindDefault(IEnumerable<ProtectedOptimizationTarget> entries, string kind)
        => Assert.Single(entries, entry => entry.TargetType == OptimizationProtectionTargetTypes.SoftwareKind
            && entry.SoftwareKind == kind);

    // The protection owner's shape for a user change of a default: same entry id, level 0, the default kept beside it.
    private static ProtectedOptimizationTarget SwitchedOff(ProtectedOptimizationTarget entry)
        => entry with
        {
            ProtectionLevel = OptimizationProtectionLevels.None,
            ProtectionSource = OptimizationProtectionSources.ProtectionList,
            DefaultProtectionLevel = entry.ProtectionLevel,
            DefaultProtectionSource = entry.ProtectionSource
        };

    private static ProtectedOptimizationTarget ProtectionEntry(string targetType, string key, string source, int level)
        => new($"protection:{key}", targetType, key, null, null, null,
            DateTimeOffset.UtcNow, source, "test", DateTimeOffset.UtcNow,
            OptimizationProtectionStates.Active, AllowsPlacementAvoidance: true, ProtectionLevel: level);
}
