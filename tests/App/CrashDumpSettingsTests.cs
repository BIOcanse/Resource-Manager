using System.Text.Json;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.CrashReports;
using static Resource_Manager_APP.Tests.CrashReportTestSupport;

namespace Resource_Manager_APP.Tests;

// 崩溃转储 settings and the WER LocalDumps desired-vs-actual state (docs/crash_dumps_2026-10-31.md 设置/写入).
public sealed class CrashDumpSettingsTests
{
    [Fact]
    public void DefaultsFollowTheChannelAndAreVisibleInTheSettings()
    {
        var defaults = AppSettingsDefaults.Create().SystemIntegration.CrashDumps;
        Assert.Equal(new AppCrashDumpSettings(AppCrashDumpModes.Default, AppCrashDumpTypes.Mini, 5), defaults);
        // The channel resolution and every bound are the compiled service_recovery.crash_reports point.
        Assert.Equal(AppCrashDumpModes.On, Options.BetaDefaultMode);
        Assert.Equal(AppCrashDumpModes.Ask, Options.StableDefaultMode);
        Assert.Equal(26214400, Options.IssueAttachmentLimitBytes);
        Assert.Equal(["ResourceManager.exe", "ResourceManager.NativeUi.exe", "ResourceManager.Desktop.exe"], Options.Images.Select(static image => image.Image));
    }

    [Fact]
    public void ReportAndDumpFoldersMustBeDistinctPlainDirectoryNames()
    {
        Assert.Throws<InvalidDataException>(() => (Options with { ReportsFolderName = "../reports" }).Validate());
        Assert.Throws<InvalidDataException>(() => (Options with { ReportsFolderName = Options.FolderName }).Validate());
        Assert.Throws<InvalidDataException>(() => (Options with { SubscriberCapacity = 0 }).Validate());
    }

    [Fact]
    public void TheCrashReportsPointIsOverridable()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var value = FreedomPointTestFactory.Point(root, "resource-manager/backend/service_recovery/build/complex/0")["value"]!;
            value["stable_default_mode"] = "off";
            value["issue_attachment_limit_bytes"] = 1000;
        });
        Assert.Equal(AppCrashDumpModes.Off, plan.CrashReports!.StableDefaultMode);
        Assert.Equal(1000, plan.CrashReports.IssueAttachmentLimitBytes);
    }

    [Theory]
    [InlineData(BetaVersion, "default", "on", "channelDefault", "beta")]
    [InlineData(StableVersion, "default", "ask", "channelDefault", "stable")]
    [InlineData(BetaVersion, "off", "off", "user", "beta")]
    [InlineData(StableVersion, "on", "on", "user", "stable")]
    public void ModeResolvesByChannelUnlessTheUserChose(string version, string mode, string effective, string source, string channel)
    {
        var resolved = Environment(version: version).ResolveMode(new AppCrashDumpSettings(mode), Options);
        Assert.Equal((effective, source, channel), (resolved.Effective, resolved.Source, resolved.Channel));
    }

    [Fact]
    public void NormalizeReturnsUnknownValuesToTheDefaults()
    {
        var settings = AppSettingsDefaults.Create();
        settings = settings with
        {
            SystemIntegration = settings.SystemIntegration with { CrashDumps = new("sometimes", "huge", 0) }
        };
        Assert.Equal(new AppCrashDumpSettings(), AppSettingsNormalizer.Normalize(settings).SystemIntegration.CrashDumps);
        settings = settings with
        {
            SystemIntegration = settings.SystemIntegration with { CrashDumps = new("off", "full", 9) }
        };
        Assert.Equal(new AppCrashDumpSettings("off", "full", 9), AppSettingsNormalizer.Normalize(settings).SystemIntegration.CrashDumps);
    }

    [Fact]
    public void MigrationReadsTheSectionAndAnOlderFileGetsTheDefaults()
    {
        var current = JsonSerializer.SerializeToNode(AppSettingsDefaults.Create(), JsonSerializerOptions.Web)!.AsObject();
        Assert.False(AppSettingsMigrator.RequiresRewrite(JsonDocument.Parse(current.ToJsonString()).RootElement));

        current["systemIntegration"]!["crashDumps"] = JsonNode("{\"mode\":\"off\",\"dumpType\":\"full\",\"keepCount\":9}");
        var root = JsonDocument.Parse(current.ToJsonString()).RootElement;
        Assert.Equal(new AppCrashDumpSettings("off", "full", 9), AppSettingsMigrator.MigrateForRead(root).SystemIntegration.CrashDumps);

        // A present but invalid section is rewritten; an older file without it reads the defaults and is written on the next save.
        current["systemIntegration"]!["crashDumps"] = JsonNode("{\"mode\":\"sometimes\",\"dumpType\":\"mini\",\"keepCount\":5}");
        Assert.True(AppSettingsMigrator.RequiresRewrite(JsonDocument.Parse(current.ToJsonString()).RootElement));
        current["systemIntegration"]!.AsObject().Remove("crashDumps");
        root = JsonDocument.Parse(current.ToJsonString()).RootElement;
        Assert.False(AppSettingsMigrator.RequiresRewrite(root));
        Assert.Equal(new AppCrashDumpSettings(), AppSettingsMigrator.MigrateForRead(root).SystemIntegration.CrashDumps);
    }

    [Fact]
    public void APatchChangesOnlyTheNamedField()
    {
        var patched = AppSettingsPatchApplier.Apply(AppSettingsDefaults.Create(),
            JsonDocument.Parse("{\"systemIntegration\":{\"crashDumps\":{\"mode\":\"on\"}}}").RootElement);
        Assert.Equal(new AppCrashDumpSettings("on", "mini", 5), patched.SystemIntegration.CrashDumps);
    }

    [Fact]
    public void OnWritesTheKeysOfEveryImageAndReadsThemBack()
    {
        var registry = new FakeRegistry();
        var settings = DumpSettings(registry);
        settings.Apply(Plan(), () => null);
        var folder = Environment().DumpFolder(Options);
        Assert.Equal(Options.Images.Count, registry.Writes.Count);
        Assert.All(registry.Writes, key => Assert.Equal(new CrashDumpRegistryKey(key.Image, folder, 0, 4453, 5), key));
        Assert.Equal([(folder, true)], registry.Folders);
        var diagnostics = settings.Diagnostics!;
        Assert.Equal(("applied", "on", "channelDefault", "on", "beta"),
            (diagnostics.State, diagnostics.EffectiveMode, diagnostics.ModeSource, diagnostics.DefaultMode, diagnostics.Channel));
        Assert.Equal(Options.KeepCountMaximum, diagnostics.KeepCountMaximum);
        Assert.All(diagnostics.Actual!, entry => Assert.True(entry.Present && entry.OwnedByResourceManager));
        // The same desired state from an unrelated plan rebuild is not written again.
        settings.Apply(Plan(version: 2), () => null);
        Assert.Equal(Options.Images.Count, registry.Writes.Count);
    }

    [Fact]
    public void FullDumpsHaveNoCustomFlagsAndTheKeepCountIsCappedByThePoint()
    {
        var registry = new FakeRegistry();
        DumpSettings(registry).Apply(Plan(new AppCrashDumpSettings("on", "full", 500)), () => null);
        Assert.All(registry.Writes, key => Assert.Equal((2, (int?)null, Options.KeepCountMaximum),
            (key.DumpType, key.CustomDumpFlags, key.DumpCount)));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("ask")]
    public void OffAndAskRemoveOnlyTheKeysResourceManagerWrote(string mode)
    {
        var registry = new FakeRegistry();
        registry.Keys["ResourceManager.exe"] = new("ResourceManager.exe", true, true, "C:\\old", 0, 4453, 5);
        registry.Keys["ResourceManager.NativeUi.exe"] = new("ResourceManager.NativeUi.exe", true, false, "C:\\mine", 2, null, 10);
        var settings = DumpSettings(registry);
        settings.Apply(Plan(new AppCrashDumpSettings(mode)), () => null);
        Assert.Empty(registry.Writes);
        Assert.Empty(registry.Folders);
        Assert.False(registry.Keys.ContainsKey("ResourceManager.exe"));
        Assert.False(registry.Keys["ResourceManager.NativeUi.exe"].OwnedByResourceManager);
        Assert.True(registry.Keys.ContainsKey("ResourceManager.NativeUi.exe"));
        Assert.Equal("applied", settings.Diagnostics!.State);
        Assert.Equal(mode, settings.Diagnostics.EffectiveMode);
    }

    [Fact]
    public void AStableBuildAsksAndWritesNothing()
    {
        var registry = new FakeRegistry();
        var settings = DumpSettings(registry, Environment(version: StableVersion));
        settings.Apply(Plan(), () => null);
        Assert.Empty(registry.Writes);
        Assert.Equal(("ask", "stable"), (settings.Diagnostics!.EffectiveMode, settings.Diagnostics.Channel));
    }

    [Fact]
    public void MismatchFailureAndSkipAreVisible()
    {
        var mismatched = new FakeRegistry { OverrideDumpCount = 1 };
        var settings = DumpSettings(mismatched);
        settings.Apply(Plan(), () => null);
        Assert.Equal("mismatch", settings.Diagnostics!.State);

        var failing = new FakeRegistry { FailWrite = true };
        settings = DumpSettings(failing);
        settings.Apply(Plan(), () => null);
        Assert.Equal("failed", settings.Diagnostics!.State);
        Assert.Equal("registry write denied", settings.Diagnostics.WriteError);
        // A failed attempt is not retried by an unrelated rebuild.
        settings.Apply(Plan(version: 2), () => null);
        Assert.Single(failing.Writes);

        var skipped = new FakeRegistry();
        settings = DumpSettings(skipped);
        settings.Apply(Plan(), () => "not-running-as-installed-service");
        Assert.Empty(skipped.Writes);
        Assert.Empty(skipped.Removals);
        Assert.Equal(("skipped", "not-running-as-installed-service", "on"),
            (settings.Diagnostics!.State, settings.Diagnostics.SkipReason, settings.Diagnostics.EffectiveMode));
    }

    private static System.Text.Json.Nodes.JsonNode JsonNode(string json) => System.Text.Json.Nodes.JsonNode.Parse(json)!;
}
