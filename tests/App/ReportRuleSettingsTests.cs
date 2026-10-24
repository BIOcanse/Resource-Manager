using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

// 设置页「报告判定」：default.json 给出规则名和默认值，用户值逐项覆盖，编译器统一校验。
public sealed class ReportRuleSettingsTests
{
    [Fact]
    public async Task SettingsGetPreservesDeliveryFailuresAndReapplyClearsThem()
    {
        await using var fixture = await SettingsFixture.StartAsync();
        Action<CompiledRuntimePlan> failure = _ => throw new InvalidOperationException("delivery-test");
        fixture.Plans.Published += failure;
        using var changed = await fixture.PatchAsync("{\"update\":{\"checkIntervalHours\":12}}");
        Assert.Equal(HttpStatusCode.Accepted, changed.StatusCode);
        var response = (await changed.Content.ReadFromJsonAsync<JsonObject>())!;
        var read = (await fixture.Client.GetFromJsonAsync<JsonObject>("/api/settings/app"))!;
        Assert.Equal("committedWithDeliveryFailures", read["runtimeApplicationDisposition"]!.GetValue<string>());
        Assert.Equal(1, read["runtimeDeliveryFailureCount"]!.GetValue<int>());
        Assert.Equal(response["runtimePlanVersion"]!.GetValue<long>(), read["runtimePlanVersion"]!.GetValue<long>());
        fixture.Plans.Published -= failure;
        using var reapplied = await fixture.Client.PostAsync("/api/settings/app/reapply", null);
        Assert.Equal(HttpStatusCode.OK, reapplied.StatusCode);
        read = (await fixture.Client.GetFromJsonAsync<JsonObject>("/api/settings/app"))!;
        Assert.Equal("committedAndApplied", read["runtimeApplicationDisposition"]!.GetValue<string>());
        Assert.Equal(0, read["runtimeDeliveryFailureCount"]!.GetValue<int>());
        Assert.Equal(12, read["settings"]!["update"]!["checkIntervalHours"]!.GetValue<int>());
    }

    private static readonly string[] RuleNames =
    [
        "cpu_throttle_temperature", "cpu_throttle_usage", "cpu_throttle_frequency",
        "interrupt_longest_event", "interrupt_cpu_share",
        "background_memory_share", "background_memory_bytes",
        "stray_process_memory", "stray_process_idle"
    ];

    [Fact]
    public void DefaultProfilePublishesEveryNamedRuleWithItsEditableFieldsAndDefaults()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        var settings = plan.RuleSettings;

        Assert.Equal(RuleNames, settings.Select(static rule => rule.Name));
        Assert.Equal(plan.Recreate.Rules.Select(static rule => rule.FactKind), settings.Select(static rule => rule.FactKind));
        var temperature = settings[0];
        Assert.Equal("celsius", temperature.DisplayUnit);
        Assert.Equal((40d, 110d), (temperature.ThresholdMinimum, temperature.ThresholdMaximum));
        Assert.Equal(["activationThreshold", "clearThreshold", "requiredConsecutiveHits", "requiredConsecutiveMisses"],
            temperature.EditableFields.ToArray());
        Assert.Equal(new CompiledHostManagerReportRuleValues(90, 85, 6, 3, null, null), temperature.Defaults);
        Assert.Equal(2U, settings.Single(static rule => rule.Name == "cpu_throttle_frequency").Comparison);
        var bytes = settings.Single(static rule => rule.Name == "background_memory_bytes");
        Assert.Equal(("gibibytes", 1024d * 1024 * 1024), (bytes.DisplayUnit, bytes.Scale));
        var strayMemory = settings.Single(static rule => rule.Name == "stray_process_memory");
        Assert.Equal(["activationThreshold", "clearThreshold"], strayMemory.EditableFields.ToArray());
        Assert.Equal(new CompiledHostManagerReportRuleValues(50 * 1024 * 1024, 45 * 1024 * 1024, null, null, null, null),
            strayMemory.Defaults);
        var idle = settings.Single(static rule => rule.Name == "stray_process_idle");
        Assert.Equal(("minutes", 60_000d), (idle.DisplayUnit, idle.Scale));
        Assert.Equal(["activationThreshold", "zeroActivityThreshold", "executableNames"], idle.EditableFields.ToArray());
        Assert.Equal(900_000, idle.Defaults.ActivationThreshold);
        Assert.Null(idle.Defaults.ClearThreshold);
        Assert.Equal(0, idle.Defaults.ZeroActivityThreshold);
        Assert.Equal(10, idle.ZeroActivityMaximum);
        Assert.Equal(24, idle.Defaults.ExecutableNames!.Count);
        Assert.All(settings, static rule => Assert.Equal(1000U, rule.CountMaximum));
    }

    [Fact]
    public void RuleNamesAreNotPartOfTheGenerationSoNamingKeepsPersistedState()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var renamed = HostManagerTestPlanFactory.CreatePlan(root =>
            RulesOf(root)[0]!["name"] = "renamed_rule");

        Assert.Equal(
            baseline.ReportCoordinator.Recreate.Rules.Select(static rule => rule.RuleGeneration),
            renamed.ReportCoordinator.Recreate.Rules.Select(static rule => rule.RuleGeneration));
        Assert.Equal("renamed_rule", renamed.ReportCoordinator.RuleSettings[0].Name);
    }

    [Fact]
    public void AnOverrideChangesOnlyItsOwnRuleAndAnOverrideEqualToTheDefaultChangesNothing()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        var changed = HostManagerTestPlanFactory.CreatePlan(reports: Reports(
            new AppReportRuleOverride("cpu_throttle_temperature", ActivationThreshold: 92, RequiredConsecutiveHits: 4),
            new AppReportRuleOverride("stray_process_idle", ActivationThreshold: 1_800_000, ExecutableNames: ["node", "cargo"])))
            .ReportCoordinator;
        var same = HostManagerTestPlanFactory.CreatePlan(reports: Reports(
            new AppReportRuleOverride("cpu_throttle_usage", ActivationThreshold: 70, ClearThreshold: 60))).ReportCoordinator;

        var temperature = changed.Recreate.Rules[0];
        Assert.Equal((92d, 85d, 4U, 3U), (temperature.ActivationThreshold, temperature.ClearThreshold,
            temperature.RequiredConsecutiveHits, temperature.RequiredConsecutiveMisses));
        var idle = changed.Recreate.Rules[^1];
        Assert.Equal(1_800_000, idle.ActivationThreshold);
        Assert.Equal(["node", "cargo"], idle.ExecutableNames.ToArray());
        for (var index = 0; index < baseline.Recreate.Rules.Length; index++)
        {
            var expectChanged = index is 0 || index == baseline.Recreate.Rules.Length - 1;
            Assert.Equal(expectChanged,
                baseline.Recreate.Rules[index].RuleGeneration != changed.Recreate.Rules[index].RuleGeneration);
        }
        Assert.Equal(
            baseline.Recreate.Rules.Select(static rule => rule.RuleGeneration),
            same.Recreate.Rules.Select(static rule => rule.RuleGeneration));
        // Defaults stay the profile's values; the settings page shows them beside the user's.
        Assert.Equal(90, changed.RuleSettings[0].Defaults.ActivationThreshold);
    }

    public static TheoryData<AppReportRuleOverride[], string, string, string> RejectedOverrides => new()
    {
        { [new("no_such_rule", ActivationThreshold: 1)], "no_such_rule", "rule", "unknown-rule" },
        { [new("cpu_throttle_usage", ActivationThreshold: 80), new("cpu_throttle_usage", ClearThreshold: 50)],
            "cpu_throttle_usage", "rule", "duplicate-rule" },
        { [new("stray_process_memory", RequiredConsecutiveHits: 2)], "stray_process_memory", "requiredConsecutiveHits", "not-editable" },
        { [new("stray_process_idle", ClearThreshold: 5)], "stray_process_idle", "clearThreshold", "not-editable" },
        { [new("cpu_throttle_temperature", ExecutableNames: ["node"])], "cpu_throttle_temperature", "executableNames", "not-editable" },
        { [new("cpu_throttle_temperature", ActivationThreshold: 200)], "cpu_throttle_temperature", "activationThreshold", "out-of-range" },
        { [new("background_memory_bytes", ClearThreshold: 1024)], "background_memory_bytes", "clearThreshold", "out-of-range" },
        { [new("interrupt_cpu_share", RequiredConsecutiveMisses: 1001)], "interrupt_cpu_share", "requiredConsecutiveMisses", "out-of-range" },
        { [new("cpu_throttle_usage", RequiredConsecutiveHits: 0)], "cpu_throttle_usage", "requiredConsecutiveHits", "out-of-range" },
        { [new("stray_process_idle", ActivationThreshold: 30_000)], "stray_process_idle", "activationThreshold", "out-of-range" },
        { [new("stray_process_idle", ZeroActivityThreshold: 11)], "stray_process_idle", "zeroActivityThreshold", "out-of-range" },
        { [new("cpu_throttle_temperature", ClearThreshold: 95)], "cpu_throttle_temperature", "clearThreshold", "clear-on-wrong-side" },
        { [new("cpu_throttle_temperature", ActivationThreshold: 80)], "cpu_throttle_temperature", "clearThreshold", "clear-on-wrong-side" },
        { [new("cpu_throttle_frequency", ClearThreshold: 80)], "cpu_throttle_frequency", "clearThreshold", "clear-on-wrong-side" },
        { [new("stray_process_idle", ExecutableNames: ["node.exe"])], "stray_process_idle", "executableNames", "invalid-executable-names" },
        { [new("stray_process_idle", ExecutableNames: ["Node", "node"])], "stray_process_idle", "executableNames", "invalid-executable-names" },
        { [new("stray_process_idle", ExecutableNames: [" node"])], "stray_process_idle", "executableNames", "invalid-executable-names" },
        { [new("stray_process_idle", ExecutableNames: [@"C:\tools\node"])], "stray_process_idle", "executableNames", "invalid-executable-names" }
    };

    [Theory]
    [MemberData(nameof(RejectedOverrides))]
    public void InvalidValuesAreRejectedWithRuleFieldAndReason(
        AppReportRuleOverride[] overrides, string rule, string field, string reason)
    {
        var error = Assert.Throws<ReportRuleSettingException>(() =>
            HostManagerTestPlanFactory.CreatePlan(reports: new AppReportSettings(overrides)));

        Assert.Equal((rule, field, reason), (error.Rule, error.Field, error.Reason));
        Assert.Equal($"report-rule-invalid:{rule}:{field}:{reason}", error.FailureCode);
    }

    [Fact]
    public void ClearMayEqualActivationAndTheIdleListMayBeEmpty()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(reports: Reports(
            new AppReportRuleOverride("cpu_throttle_frequency", ClearThreshold: 85),
            new AppReportRuleOverride("stray_process_idle", ExecutableNames: []))).ReportCoordinator;

        Assert.Equal(85, plan.Recreate.Rules[2].ClearThreshold);
        Assert.Empty(plan.Recreate.Rules[^1].ExecutableNames);
    }

    [Fact]
    public void AProfileDefaultOutOfRangeIsAProfileErrorNotAUserRejection()
    {
        var error = Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            RulesOf(root)[0]!["activation_threshold"] = 200));
        Assert.Contains("cpu_throttle_temperature", error.Message);
    }

    [Fact]
    public void ProfileRulesMustHaveUniqueNames()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            RulesOf(root)[0]!.AsObject().Remove("name")));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            RulesOf(root)[1]!["name"] = "cpu_throttle_temperature"));
    }

    [Fact]
    public void SettingsFileKeepsOverridesAndRequiresRewriteWithoutTheSection()
    {
        var settings = AppSettingsDefaults.Create() with
        {
            Reports = Reports(new AppReportRuleOverride("stray_process_idle", ActivationThreshold: 600_000, ExecutableNames: ["pwsh"]))
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(settings, JsonSerializerOptions.Web));

        var read = AppSettingsMigrator.MigrateForRead(document.RootElement);
        var item = Assert.Single(read.Reports!.RuleOverrides);
        Assert.Equal(("stray_process_idle", 600_000d), (item.Rule, item.ActivationThreshold));
        Assert.Equal(["pwsh"], item.ExecutableNames);
        Assert.Null(item.ClearThreshold);
        Assert.False(AppSettingsMigrator.RequiresRewrite(document.RootElement));

        var withoutSection = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        withoutSection.Remove("reports");
        using var old = JsonDocument.Parse(withoutSection.ToJsonString());
        Assert.True(AppSettingsMigrator.RequiresRewrite(old.RootElement));
        Assert.Empty(AppSettingsMigrator.MigrateForRead(old.RootElement).Reports!.RuleOverrides);

        withoutSection["reports"] = JsonNode.Parse("""{"ruleOverrides":[{"rule":"x","activationThreshold":"high"}]}""");
        using var malformed = JsonDocument.Parse(withoutSection.ToJsonString());
        Assert.ThrowsAny<JsonException>(() => AppSettingsMigrator.MigrateForRead(malformed.RootElement));
    }

    [Fact]
    public void PatchReplacesTheWholeOverrideList()
    {
        var current = AppSettingsDefaults.Create() with
        {
            Reports = Reports(new AppReportRuleOverride("cpu_throttle_usage", ActivationThreshold: 80))
        };
        using var changes = JsonDocument.Parse(
            """{"reports":{"ruleOverrides":[{"rule":"cpu_throttle_temperature","activationThreshold":95}]}}""");

        var patched = AppSettingsPatchApplier.Apply(current, changes.RootElement);

        var item = Assert.Single(patched.Reports!.RuleOverrides);
        Assert.Equal(("cpu_throttle_temperature", 95d), (item.Rule, item.ActivationThreshold));
    }

    [Fact]
    public async Task HttpListsRulesAppliesAValidOverrideAndRejectsAnInvalidOneWithItsReason()
    {
        await using var fixture = await SettingsFixture.StartAsync();

        var rules = await fixture.Client.GetFromJsonAsync<JsonObject>("/api/settings/report-rules");
        var listed = rules!["rules"]!.AsArray();
        Assert.Equal(RuleNames, listed.Select(static rule => rule!["name"]!.GetValue<string>()));
        Assert.Equal("cpu_sustained_thermal_throttling", listed[0]!["reportType"]!.GetValue<string>());
        Assert.Equal("atOrBelow", listed[2]!["comparison"]!.GetValue<string>());
        Assert.Equal("suspected_stray_process", listed[8]!["reportType"]!.GetValue<string>());
        Assert.Equal(90, listed[0]!["defaults"]!["activationThreshold"]!.GetValue<double>());

        var before = fixture.Plans.Current.HostManager.ReportCoordinator.Recreate.Rules;
        using var accepted = await fixture.PatchAsync(
            """{"reports":{"ruleOverrides":[{"rule":"cpu_throttle_temperature","activationThreshold":95}]}}""");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var after = fixture.Plans.Current.HostManager.ReportCoordinator.Recreate.Rules;
        Assert.Equal(95, after[0].ActivationThreshold);
        Assert.NotEqual(before[0].RuleGeneration, after[0].RuleGeneration);
        Assert.Equal(before.Skip(1).Select(static rule => rule.RuleGeneration), after.Skip(1).Select(static rule => rule.RuleGeneration));

        using var rejected = await fixture.PatchAsync(
            """{"reports":{"ruleOverrides":[{"rule":"cpu_throttle_temperature","activationThreshold":95,"clearThreshold":99}]}}""");
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var result = (await rejected.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("rejectedBeforeCommit", result["runtimeApplicationDisposition"]!.GetValue<string>());
        Assert.Equal("report-rule-invalid:cpu_throttle_temperature:clearThreshold:clear-on-wrong-side",
            result["runtimeFailureCode"]!.GetValue<string>());
        var saved = await fixture.Client.GetFromJsonAsync<JsonObject>("/api/settings/app");
        Assert.Null(saved!["settings"]!["reports"]!["ruleOverrides"]![0]!["clearThreshold"]);
        Assert.Equal(95, fixture.Plans.Current.HostManager.ReportCoordinator.Recreate.Rules[0].ActivationThreshold);
    }

    private static AppReportSettings Reports(params AppReportRuleOverride[] overrides) => new(overrides);

    private static JsonArray RulesOf(JsonObject root)
        => root["host_recreate"]!["report_coordinator"]!["rules"]!.AsArray();

    private sealed class SettingsFixture(WebApplication app, string root) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = TimeSpan.FromSeconds(20)
        };

        public RuntimePlanProvider Plans => app.Services.GetRequiredService<RuntimePlanProvider>();

        public async Task<HttpResponseMessage> PatchAsync(string changes)
        {
            var current = (await Client.GetFromJsonAsync<JsonObject>("/api/settings/app"))!;
            var body = new JsonObject
            {
                ["expectedRevision"] = current["revision"]!.GetValue<string>(),
                ["changes"] = JsonNode.Parse(changes)
            };
            return await Client.PatchAsync("/api/settings/app", JsonContent.Create(body));
        }

        public static async Task<SettingsFixture> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"rm-report-rules-api-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var capabilities = new StartupCapabilitySet("report-rules-test",
                StartupCapability.RuntimeEffectOwners | StartupCapability.MutablePersistence);
            builder.Services.AddResourceManagerApp(["--no-native-ui"], capabilities);
            builder.Services.RemoveAll<IHostedService>();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            try
            {
                typeof(ResourceManagerEndpointRouteBuilderExtensions)
                    .GetMethod("MapSettingsEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [app, capabilities]);
                await app.StartAsync();
                await app.Services.GetRequiredService<IRuntimeSpecializationCoordinator>()
                    .RebuildAsync("test-startup", CancellationToken.None);
                return new SettingsFixture(app, root);
            }
            catch
            {
                await app.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await app.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }
}
