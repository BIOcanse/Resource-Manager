using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Software;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// scheduling_protection_2026-10-29 §4.1 with the 10-29 ruling 「取消2级保护机制」「只需要留一级保护」: the catalog gives
/// base scores ("not suppressed" is the high tier) and no-freeze defaults; Windows processes get the system default.
/// </summary>
public sealed class CatalogProtectionTests
{
    [Fact]
    public async Task ShippedCatalogReadsBaseScoresNoFreezeAndEveryProcessEntry()
    {
        var profile = await new JsonSoftwarePolicyProfileProvider().GetProfileAsync(default);

        Assert.Equal(90, profile.Groups["adapted.resource-manager"].BaseScore);
        Assert.Equal(90, profile.Groups["system.critical-services"].BaseScore);
        Assert.Equal(30, profile.Groups["system.background-telemetry-ads"].BaseScore);
        Assert.Equal(90, profile.Groups["anti-cheat"].BaseScore);
        Assert.True(profile.Groups["anti-cheat"].NoFreeze);
        Assert.True(profile.Groups["meeting-capture-password"].NoFreeze);
        Assert.False(profile.Groups["system.critical-services"].NoFreeze);
        Assert.Null(profile.Groups["game.launchers"].BaseScore);

        // Every shipped per-process entry is valid (known group, one matcher): none is dropped by the reader.
        using var stream = typeof(JsonSoftwarePolicyProfileProvider).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.SoftwareProfiles.official.json")!;
        using var raw = JsonDocument.Parse(stream);
        Assert.Equal(raw.RootElement.GetProperty("processAssignments").GetArrayLength(), profile.ProcessAssignments.Count);
        Assert.All(profile.Assignments.Values, assignment => Assert.True(profile.Groups.ContainsKey(assignment.AssignedGroupId)));
        foreach (var group in raw.RootElement.GetProperty("softwareGroups").EnumerateArray())
        {
            Assert.False(group.TryGetProperty("defaultPolicy", out _));
            if (group.TryGetProperty("constraints", out var constraints))
            {
                Assert.False(constraints.TryGetProperty("blockedPolicies", out _));
                Assert.False(constraints.TryGetProperty("blockedAutomaticPolicies", out _));
            }
        }
    }

    [Fact]
    public async Task ShippedCatalogDefaultsAppearInTheListWithTheirSources()
    {
        var service = CreateService(new MemoryStore(), await new JsonSoftwarePolicyProfileProvider().GetProfileAsync(default));
        var list = await service.GetProtectedTargetsAsync(default);

        Assert.Equal(list.Count, list.Select(static item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(list.Count, service.ProtectedTargetCount);
        Assert.All(list, item => Assert.Equal(OptimizationProtectionLevels.NoFreeze, item.ProtectionLevel));
        Assert.Equal(
            [SoftwareKinds.WindowsComponent, SoftwareKinds.WindowsService, SoftwareKinds.WindowsSystem],
            list.Where(static item => item.ProtectionSource == OptimizationProtectionSources.SystemDefault)
                .Select(static item => item.SoftwareKind!).Order(StringComparer.Ordinal));

        var antiCheatService = Assert.Single(list, item => item.SoftwareId == WindowsServiceSoftwareIds.ForService("EasyAntiCheat_EOS"));
        Assert.Equal((OptimizationProtectionSources.SoftwareCatalog, "anti-cheat"), (antiCheatService.ProtectionSource, antiCheatService.PolicyGroupId));
        var antiCheatProcess = Assert.Single(list, item => item.ProcessName == "SGuard64");
        Assert.Equal(OptimizationProtectionTargetTypes.Process, antiCheatProcess.TargetType);
        Assert.Contains(list, item => item.PolicyGroupId == "meeting-capture-password");

        // Groups that only give a base score add no protection entry (RM itself, critical services, dwm).
        Assert.DoesNotContain(list, item => item.SoftwareId == RuntimeAttributionIds.ResourceManagerSelf);
        Assert.DoesNotContain(list, item => item.SoftwareId == WindowsServiceSoftwareIds.ForService("Audiosrv"));
        Assert.DoesNotContain(list, item => item.ProcessName == "dwm");
    }

    [Fact]
    public async Task TableResolvesTheMostSpecificTargetAndUserChangesWin()
    {
        var store = new MemoryStore();
        var service = CreateService(store, await new JsonSoftwarePolicyProfileProvider().GetProfileAsync(default));
        var list = await service.GetProtectedTargetsAsync(default);
        var table = OptimizationProtectionTable.Create(list);

        // An anti-cheat process attributed to a game takes the anti-cheat entry (process before software).
        Assert.Equal(OptimizationProtectionLevels.NoFreeze,
            table.Resolve("game-x", SoftwareKinds.Game, ["SGuard64"], []).ProtectionLevel);
        Assert.Equal(OptimizationProtectionLevels.NoFreeze,
            table.Resolve("game-x", SoftwareKinds.Game, [], [@"D:\Games\X\EasyAntiCheat\EasyAntiCheat.exe"]).ProtectionLevel);
        Assert.Same(OptimizationProtectionMatch.None, table.Resolve("game-x", SoftwareKinds.Game, ["game"], []));
        // Windows processes take the system default; a shared host takes its hosted services' entries.
        var system = table.Resolve(RuntimeAttributionIds.WindowsSystem, SoftwareKinds.WindowsSystem, ["conhost"], []);
        Assert.Equal((OptimizationProtectionLevels.NoFreeze, OptimizationProtectionSources.SystemDefault),
            (system.ProtectionLevel, system.ProtectionSource));
        var hosted = table.Resolve("windows-service-group:x-2", SoftwareKinds.Other, ["svchost"], [], ["vgc", "Foo"]);
        Assert.Equal(OptimizationProtectionSources.SoftwareCatalog, hosted.ProtectionSource);

        // The user switches the game's protection on, the anti-cheat process off, and the service default off.
        var antiCheat = Assert.Single(list, item => item.ProcessName == "SGuard64");
        var services = Assert.Single(list, item => item.SoftwareKind == SoftwareKinds.WindowsService
            && item.ProtectionSource == OptimizationProtectionSources.SystemDefault);
        Assert.NotNull(await service.SetSoftwareProtectionAsync("game-x", true, default));
        Assert.Equal(OptimizationProtectionLevels.None,
            (await service.UpdateProtectedTargetLevelAsync(antiCheat.Id, OptimizationProtectionLevels.None, default))!.ProtectionLevel);
        await service.UpdateProtectedTargetLevelAsync(services.Id, OptimizationProtectionLevels.None, default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.UpdateProtectedTargetLevelAsync(services.Id, 2, default));
        Assert.Null(await service.UpdateProtectedTargetLevelAsync("missing", OptimizationProtectionLevels.NoFreeze, default));
        Assert.Equal(3, store.Targets.Count);

        // The changes persist and win after a restart.
        var reloaded = CreateService(store, await new JsonSoftwarePolicyProfileProvider().GetProfileAsync(default));
        table = OptimizationProtectionTable.Create(await reloaded.GetProtectedTargetsAsync(default));
        var offProcess = table.Resolve("game-x", SoftwareKinds.Game, ["SGuard64"], []);
        Assert.Equal((OptimizationProtectionLevels.None, OptimizationProtectionSources.ProtectionList),
            (offProcess.ProtectionLevel, offProcess.ProtectionSource));
        Assert.Equal(OptimizationProtectionLevels.NoFreeze, table.Resolve("game-x", SoftwareKinds.Game, ["game"], []).ProtectionLevel);
        Assert.Equal(OptimizationProtectionLevels.None,
            table.Resolve("windows-service:spooler", SoftwareKinds.WindowsService, ["spoolsv"], []).ProtectionLevel);
        Assert.Equal(OptimizationProtectionLevels.NoFreeze,
            table.Resolve(WindowsServiceSoftwareIds.ForService("vgc"), SoftwareKinds.WindowsService, ["vgc"], []).ProtectionLevel);

        // Removing the user's entry brings the default back.
        Assert.True(await reloaded.RemoveProtectedTargetAsync(services.Id, default));
        Assert.False(await reloaded.RemoveProtectedTargetAsync(services.Id, default));
        Assert.Equal(services, Assert.Single(await reloaded.GetProtectedTargetsAsync(default), item => item.Id == services.Id)
            with { ProtectedAt = services.ProtectedAt });
    }

    [Fact]
    public async Task SoftwareSwitchStoresOffOnlyWhenADefaultExists()
    {
        var store = new MemoryStore();
        var profile = await new JsonSoftwarePolicyProfileProvider().GetProfileAsync(default);
        var service = CreateService(store, profile);
        var serviceId = WindowsServiceSoftwareIds.ForService("EasyAntiCheat");

        // A software known only from the catalog can be switched; off keeps the default switched off.
        var off = (await service.SetSoftwareProtectionAsync(serviceId, false, default))!;
        Assert.Equal((OptimizationProtectionLevels.None, OptimizationProtectionSources.SoftwareDetail,
                (int?)OptimizationProtectionLevels.NoFreeze, OptimizationProtectionSources.SoftwareCatalog),
            (off.ProtectionLevel, off.ProtectionSource, off.DefaultProtectionLevel, off.DefaultProtectionSource));
        Assert.Single(store.Targets);
        Assert.Equal(OptimizationProtectionLevels.None, Assert.Single(store.Targets).ProtectionLevel);
        Assert.DoesNotContain(serviceId, OptimizationProtectionTable.SoftwareProtectedFromReports(
            await service.GetProtectedTargetsAsync(default)));

        // Without a default, off removes the user's entry.
        await service.SetSoftwareProtectionAsync("game-x", true, default);
        Assert.Contains("game-x", OptimizationProtectionTable.SoftwareProtectedFromReports(
            await service.GetProtectedTargetsAsync(default)));
        Assert.Null(await service.SetSoftwareProtectionAsync("game-x", false, default));
        Assert.DoesNotContain(store.Targets, item => item.SoftwareId == "game-x");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetSoftwareProtectionAsync("unknown", true, default));
    }

    [Fact]
    public async Task CatalogBaseScoresResolveByGroupAndProcess()
    {
        var profile = await new JsonSoftwarePolicyProfileProvider().GetProfileAsync(default);

        var audio = profile.ResolveSoftwareBaseScore(WindowsServiceSoftwareIds.ForService("Audiosrv"))!;
        Assert.Equal((90d, "system.critical-services", "service:audiosrv"), (audio.BaseScore, audio.GroupId, audio.ProcessEntryKey));
        Assert.Equal(30, profile.ResolveSoftwareBaseScore(WindowsServiceSoftwareIds.ForService("DiagTrack"))!.BaseScore);
        var self = profile.ResolveSoftwareBaseScore(RuntimeAttributionIds.ResourceManagerSelf)!;
        Assert.Equal((90d, "adapted.resource-manager", (string?)null), (self.BaseScore, self.GroupId, self.ProcessEntryKey));
        Assert.Null(profile.ResolveSoftwareBaseScore("unknown"));

        Assert.Equal(90, profile.ResolveProcessBaseScore("dwm", @"C:\Windows\System32\dwm.exe")!.BaseScore);
        Assert.Equal(30, profile.ResolveProcessBaseScore("SearchHost.exe", null)!.BaseScore);
        Assert.Equal(90, profile.ResolveProcessBaseScore("EasyAntiCheat_EOS", null)!.BaseScore);
        Assert.Equal(90, profile.ResolveProcessBaseScore(null, @"C:\Program Files (x86)\GameGuard\GameMon.des")!.BaseScore);
        Assert.Equal(90, profile.ResolveProcessBaseScore("svchost", null, ["DiagTrack", "Audiosrv"])!.BaseScore);
        Assert.Null(profile.ResolveProcessBaseScore("svchost", null));
        Assert.Null(profile.ResolveProcessBaseScore("notepad", null));
    }

    [Fact]
    public void ReaderKeepsValidProcessEntriesAndPathEntriesWinOverNames()
    {
        const string json = """
            {
              "softwareGroups": [
                { "groupId": "low", "displayName": "Low", "baseScore": 30 },
                { "groupId": "bad-score", "displayName": "Bad", "baseScore": 150, "noFreeze": true }
              ],
              "processAssignments": [
                { "processName": "tool.exe", "assignedGroupId": "low" },
                { "processName": "tool", "baseScore": 70 },
                { "processName": "tool", "executablePath": "C:\\Tools\\tool.exe", "baseScore": 55 },
                { "processName": "both", "serviceName": "Both", "assignedGroupId": "low" },
                { "serviceName": "Svc", "executablePath": "C:\\svc.exe", "assignedGroupId": "low" },
                { "processName": "unknown-group", "assignedGroupId": "missing", "baseScore": 40 },
                { "processName": "nothing" },
                { "processName": "out-of-range", "baseScore": -1 },
                { "serviceName": "Svc", "assignedGroupId": "bad-score" }
              ]
            }
            """;
        var profile = JsonSoftwarePolicyProfileProvider.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Null(profile.Groups["bad-score"].BaseScore);
        Assert.True(profile.Groups["bad-score"].NoFreeze);
        Assert.Equal(
            ["process-name:tool", @"process-path:c:\tools\tool.exe", "service:svc"],
            profile.ProcessAssignments.Select(static entry => entry.Key));
        Assert.Equal(30, profile.ResolveProcessBaseScore("tool", null)!.BaseScore);
        Assert.Equal(55, profile.ResolveProcessBaseScore("tool", @"C:\Tools\tool.exe")!.BaseScore);
        Assert.Null(profile.ResolveProcessBaseScore(null, null, ["Svc"]));
        var protection = OptimizationProtectionDefaults.Create(profile, DateTimeOffset.UnixEpoch);
        Assert.Contains(protection, item => item.SoftwareId == WindowsServiceSoftwareIds.ForService("Svc"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(7, 1)]
    public void StoredLevelsReadAsNoneOrNoFreeze(int stored, int expected)
    {
        Assert.Equal(expected, OptimizationProtectionLevels.Normalize(stored));
        Assert.Equal(stored is 0 or 1, OptimizationProtectionLevels.IsValid(stored));
    }

    private static OptimizationProtectionService CreateService(MemoryStore store, SoftwarePolicyProfile profile)
        => new(store, new KnownSoftware(), new FixedSoftwarePolicyProfileProvider(profile));

    private sealed class MemoryStore : IOptimizationProtectionStore
    {
        public IReadOnlyList<ProtectedOptimizationTarget> Targets { get; private set; } = [];

        public Task<IReadOnlyList<ProtectedOptimizationTarget>> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(Targets);

        public Task SaveAsync(IReadOnlyList<ProtectedOptimizationTarget> targets, CancellationToken cancellationToken)
        {
            Targets = targets.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class KnownSoftware : ISoftwareRegistryView
    {
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SoftwareRecord>>(
                [new("game-x", "Game X", SoftwareKinds.Game, "ready", [], [], new(false, "None", "missing_uninstall_entry"), null)]);

        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken)
            => GetSoftwareAsync(cancellationToken);
    }
}

internal sealed class FixedSoftwarePolicyProfileProvider(SoftwarePolicyProfile profile) : ISoftwarePolicyProfileProvider
{
    public static FixedSoftwarePolicyProfileProvider Empty { get; } = new(SoftwarePolicyProfile.Empty);

    public Task<SoftwarePolicyProfile> GetProfileAsync(CancellationToken cancellationToken) => Task.FromResult(profile);
}

internal static class ProtectionTestEntries
{
    /// <summary>The user's entries of a protection list (the defaults are always there too).</summary>
    public static ProtectedOptimizationTarget[] User(IEnumerable<ProtectedOptimizationTarget> list)
        => list.Where(static item => !OptimizationProtectionSources.IsDefault(item.ProtectionSource)).ToArray();
}
