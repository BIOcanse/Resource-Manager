using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsMetricConfigurationLibraryIntegrationTests
{
    [Fact]
    public void DefaultDataAndCompilerAreLibraryOwnedAndMatchTheActualHostPlan()
    {
        var config = WindowsMetricConfiguration.CreateDefault();
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(WindowsMetricConfiguration).Assembly);
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(WindowsMetricConfigurationCompiler).Assembly);
        Assert.Equal(67UL << 32, config.Configuration.Generation);
        Assert.Equal(23, config.CatalogConfiguration.SourcePolicies.Length);
        Assert.Equal(304, config.CatalogConfiguration.RuleTemplates.Length);
        var npu = config.CatalogConfiguration.RuleTemplates.Where(rule => rule.SourceId == "windows.npu-memory").ToArray();
        Assert.Equal(4, npu.Length);
        Assert.All(npu, rule =>
        {
            Assert.Equal(16U, rule.ScopeExpansion);
            Assert.Equal(64U, rule.MaximumInstanceCount);
            Assert.Equal(3U, rule.ValueKind);
            Assert.Equal(ulong.MaxValue, rule.MaximumValueBits);
        });
        var source = Assert.Single(config.CatalogConfiguration.SourcePolicies, source => source.SourceId == "windows.npu-memory");
        Assert.Equal(22UL, source.SourceHandle);
        Assert.False(source.Required);
        var host = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        Assert.Equal(host.HotPublish.CatalogManifestSha256, config.CatalogConfiguration.CatalogManifestSha256);
        Assert.Equal(NativeMetricSnapshotHostConfiguration.CreateConfiguration(host), config.Configuration);
        Assert.Equal(TimeSpan.FromMilliseconds(50), config.PdhOptions.FrameReuseWindow);
        Assert.Equal(TimeSpan.FromMilliseconds(10000), config.PdhOptions.LastGoodLifetime);
        Assert.Equal(5000, config.PdhOptions.BaselineResetIntervalMilliseconds);
        Assert.Equal(host.HotPublish.RuleTemplates.Select(static row => row.TemplateId), config.CatalogConfiguration.RuleTemplates.Select(static row => row.TemplateId));
    }

    [Fact]
    public void ExplicitHostDefaultExpansionPreservesFullOverridesAndDoesNotChangeLoadBytesContract()
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly.GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(buffer.ToArray(), "unexpanded-marker.json"));
        var full = StrictHostManagerProfileLoader.ReadExpandedDefaultBytes();
        var root = JsonNode.Parse(full)!.AsObject();
        Assert.Equal(304, root["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray().Count);
        var edited = root["hot_publish"]!["metric_snapshot"]!["rule_templates"]![0]!;
        edited["semantic_fingerprint"] = edited["semantic_fingerprint"]!.GetValue<ulong>() + 1;
        var loaded = StrictHostManagerProfileLoader.LoadBytes(JsonSerializer.SerializeToUtf8Bytes(root), "explicit-override.json");
        Assert.Equal(67, loaded.Profile.ProfileRevision);
        root["hot_publish"]!["metric_snapshot"]!.AsObject().Remove("rule_templates");
        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(JsonSerializer.SerializeToUtf8Bytes(root), "missing-custom-value.json"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate-json")]
    [InlineData("missing")]
    [InlineData("duplicate-source")]
    [InlineData("duplicate-rule")]
    [InlineData("duplicate-flag")]
    [InlineData("range")]
    [InlineData("capacity")]
    [InlineData("contracts")]
    [InlineData("placeholder")]
    [InlineData("pdh-duration")]
    public void StrictConfigurationRejectsOriginalInvalidContracts(string violation)
    {
        var bytes = WindowsMetricConfiguration.ReadDefaultProfileBytes();
        if (violation == "duplicate-json")
        {
            var revision = JsonNode.Parse(bytes)!["profile_revision"]!.GetValue<int>();
            var property = $"\"profile_revision\": {revision}";
            var text = Encoding.UTF8.GetString(bytes).Replace(property, $"{property}, {property}", StringComparison.Ordinal);
            Assert.NotEqual(Encoding.UTF8.GetString(bytes), text);
            Assert.Throws<InvalidDataException>(() => WindowsMetricConfiguration.LoadBytes(Encoding.UTF8.GetBytes(text)));
            return;
        }
        var root = JsonNode.Parse(bytes)!.AsObject();
        var hot = root["hot_publish"]!.AsObject();
        var rule = hot["rule_templates"]![0]!.AsObject();
        switch (violation)
        {
            case "unknown": root["unexpected"] = true; break;
            case "missing": root.Remove("build_capacity"); break;
            case "duplicate-source": hot["source_policies"]!.AsArray().Add(hot["source_policies"]![0]!.DeepClone()); break;
            case "duplicate-rule": hot["rule_templates"]!.AsArray().Add(rule.DeepClone()); break;
            case "duplicate-flag": rule["metric_flags"] = new JsonArray("nonnegative", "nonnegative"); break;
            case "range": rule["value_kind"] = "boolean"; rule["minimum_value_bits"] = 0; rule["maximum_value_bits"] = 2; break;
            case "capacity": root["recreate"]!["capacity"]!["maximum_rule_count"] = 1; break;
            case "contracts": root["recreate"]!["observation_contract_version"] = 0; break;
            case "placeholder": rule["metric_id_template"] = "cpu.{unknown}.usage"; break;
            case "pdh-duration": root["pdh"]!["frame_reuse_window_ms"] = -1; break;
        }
        Assert.Throws<InvalidDataException>(() => WindowsMetricConfiguration.LoadBytes(JsonSerializer.SerializeToUtf8Bytes(root)));
    }

    [Fact]
    public void CanonicalOrderingPreservesManifestIdentityAndExplicitRuleOverrideChangesIt()
    {
        var baseline = WindowsMetricConfiguration.CreateDefault();
        var root = JsonNode.Parse(WindowsMetricConfiguration.ReadDefaultProfileBytes())!.AsObject();
        var rules = root["hot_publish"]!["rule_templates"]!.AsArray();
        var reversed = rules.Reverse().Select(static row => row!.DeepClone()).ToArray();
        root["hot_publish"]!["rule_templates"] = new JsonArray(reversed);
        var reordered = WindowsMetricConfiguration.LoadBytes(JsonSerializer.SerializeToUtf8Bytes(root));
        Assert.Equal(baseline.CatalogConfiguration.CatalogManifestSha256, reordered.CatalogConfiguration.CatalogManifestSha256);
        var rule = root["hot_publish"]!["rule_templates"]![0]!;
        rule["semantic_fingerprint"] = rule["semantic_fingerprint"]!.GetValue<ulong>() + 1;
        var edited = WindowsMetricConfiguration.LoadBytes(JsonSerializer.SerializeToUtf8Bytes(root));
        Assert.NotEqual(baseline.CatalogConfiguration.CatalogManifestSha256, edited.CatalogConfiguration.CatalogManifestSha256);
    }
}
