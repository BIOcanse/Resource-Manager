using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.MemoryProtection;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostMemoryProtectionTests
{
    [Fact]
    public async Task LargestAtMinimumHandlesOnlyOneAndDoesNotMergeSoftwareOrPaths()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CreatePoint();
        using var enabled = await fixture.Patch(new { enabled = true, minimumProcessGrowthBytes = 800 });
        await Until(() => fixture.Source.Active == 1);
        fixture.Source.Publish(960, 96, [Process(20, "200", 800) with { SoftwareId = "same", ExecutablePath = @"C:\fixture\same.exe" },
            Process(21, "201", 700) with { SoftwareId = "same", ExecutablePath = @"C:\fixture\same.exe", ParentProcessId = 20 }]);
        await Until(async () => (await fixture.Status()).Actions.Count == 1);
        Assert.Equal(20, Assert.Single(fixture.Executor.Executed));
        var action = Assert.Single((await fixture.Status()).Actions);
        Assert.Equal(0UL, action.BaselineBytes);
        Assert.Equal(800m, action.GrowthBytes);
        Assert.Equal("process", action.TargetKind);
        Assert.Null(action.TreeRoot);
    }

    [Fact]
    public async Task SmallProcessesUseDirectChildMemoryDiffAndSkipOnlyWhitelistedMembers()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SoftwarePolicies.SaveSoftwarePolicyAsync(GpuPlacementPolicyDefaults.CreateSoftwarePolicy("protected", "Protected")
            with { HostMemoryProtectionNeverTerminate = true }, CancellationToken.None);
        var root = Process(20, "100", 100) with { ExecutablePath = @"C:\fixture\root.exe", ParentProcessId = 10 };
        fixture.Source.Publish(200, 20, [Process(10, "50", 100), root]);
        await fixture.CreatePoint();
        using var enabled = await fixture.Patch(new { enabled = true, minimumProcessGrowthBytes = 1000, protectedProcesses = new[] { "root.exe" } });
        await Until(() => fixture.Source.Active == 1);
        fixture.Source.Publish(960, 96, [Process(10, "50", 100), root,
            Process(21, "200", 200) with { ParentProcessId = 20, SoftwareId = "protected" },
            Process(22, "300", 100) with { ParentProcessId = 21 }, Process(23, "400", 200) with { ParentProcessId = 20 }]);
        await Until(async () => (await fixture.Status()).Actions.Count == 4);
        Assert.Equal(new[] { 23, 22 }, fixture.Executor.Executed.ToArray());
        var records = (await fixture.Status()).Actions;
        Assert.All(records, item => { Assert.Equal("processTree", item.TargetKind); Assert.Equal(20, item.TreeRoot!.ProcessId); });
        Assert.Single(records.Select(item => item.ActionId).Distinct());
        Assert.Equal(2, records.Count(item => item.Outcome == "excluded"));
        Assert.All(fixture.Executor.Options, options => Assert.Equal("terminateOnly", options.ActionMode));
    }

    [Fact]
    public async Task NoPositiveTreeScoreHasExplicitStatusAndNoAction()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CreatePoint();
        using var enabled = await fixture.Patch(new { enabled = true, minimumProcessGrowthBytes = 1000 });
        await Until(() => fixture.Source.Active == 1);
        fixture.Source.Publish(960, 96, [Process(20, "200", 500)]);
        await Until(async () => (await fixture.Status()).DecisionReason == "no-positive-child-memory-growth");
        Assert.Empty(fixture.Executor.Executed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task InvalidMinimumIsRejectedBeforeCommit(long minimum)
    {
        await using var fixture = await Fixture.StartAsync();
        var before = await fixture.Stored();
        using var response = await fixture.Patch(new { minimumProcessGrowthBytes = minimum });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("invalid-minimum-process-growth-bytes", (await response.Content.ReadFromJsonAsync<AppSettingsUpdateResult>())!.RuntimeFailureCode);
        Assert.Equal(before.Revision, (await fixture.Stored()).Revision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\app.exe")]
    [InlineData("*.exe")]
    public async Task InvalidWhitelistIsRejectedBeforeCommit(string? entry)
    {
        await using var fixture = await Fixture.StartAsync();
        var before = await fixture.Stored();
        using var response = await fixture.Patch(new { protectedProcesses = entry is null ? null : new[] { entry } });
        Assert.Equal(entry is null ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, response.StatusCode);
        if (entry is null) Assert.Contains("invalid-protected-processes", await response.Content.ReadAsStringAsync());
        else Assert.Equal("invalid-protected-processes", (await response.Content.ReadFromJsonAsync<AppSettingsUpdateResult>())!.RuntimeFailureCode);
        Assert.Equal(before.Revision, (await fixture.Stored()).Revision);
    }

    [Fact]
    public void DefaultsAndOldSettingsUpgradeExposeExplorerAndOneGiB()
    {
        Assert.Equal(1073741824L, AppSettingsDefaults.Create().MemoryProtection!.MinimumProcessGrowthBytes);
        Assert.Equal(new[] { "explorer.exe" }, AppSettingsDefaults.Create().MemoryProtection!.ProtectedProcesses);
        using var old = JsonDocument.Parse("{\"version\":\"1.0.36\",\"memoryProtection\":{\"thresholdPercent\":90,\"actionMode\":\"terminateOnly\"}}");
        Assert.True(AppSettingsMigrator.RequiresRewrite(old.RootElement));
        var upgraded = AppSettingsMigrator.MigrateForRead(old.RootElement);
        Assert.Equal("1.0.37", upgraded.Version);
        Assert.Equal(new[] { "explorer.exe" }, upgraded.MemoryProtection!.ProtectedProcesses);
        Assert.Equal(1073741824L, upgraded.MemoryProtection.MinimumProcessGrowthBytes);
        Assert.Equal("terminateOnly", upgraded.MemoryProtection.ActionMode);
        using var edited = JsonDocument.Parse("{\"version\":\"1.0.37\",\"memoryProtection\":{\"minimumProcessGrowthBytes\":12,\"protectedProcesses\":[]}}");
        Assert.Empty(AppSettingsMigrator.MigrateForRead(edited.RootElement).MemoryProtection!.ProtectedProcesses);
    }

    [Theory]
    [InlineData("{\"minimumProcessGrowthBytes\":null}", "invalid-minimum-process-growth-bytes")]
    [InlineData("{\"minimumProcessGrowthBytes\":1.5}", "invalid-minimum-process-growth-bytes")]
    [InlineData("{\"minimumProcessGrowthBytes\":\"bad\"}", "invalid-minimum-process-growth-bytes")]
    [InlineData("{\"protectedProcesses\":[1]}", "invalid-protected-processes")]
    [InlineData("{\"protectedProcesses\":\"explorer.exe\"}", "invalid-protected-processes")]
    public async Task WrongNewFieldTypesReturnCodesBeforeCommit(string json, string code)
    {
        await using var fixture = await Fixture.StartAsync();
        var before = await fixture.Stored();
        using var document = JsonDocument.Parse(json);
        using var response = await fixture.Patch(document.RootElement);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync());
        Assert.Equal(before.Revision, (await fixture.Stored()).Revision);
    }
}
