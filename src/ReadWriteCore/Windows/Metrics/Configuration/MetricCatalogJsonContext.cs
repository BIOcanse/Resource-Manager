using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ResourceManager.ReadWrite.Native;
using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.ReadWrite.Windows.Metrics.Configuration;

internal sealed record MetricCatalogDigestInput(uint CatalogManifestVersion,
    ImmutableArray<NativeMetricSnapshotSourcePolicy> SourcePolicies,
    ImmutableArray<NativeMetricSnapshotRuleTemplate> RuleTemplates);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(MetricCatalogDigestInput))]
internal partial class MetricCatalogJsonContext : JsonSerializerContext;
