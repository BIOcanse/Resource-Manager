global using HostManagerMetricSnapshotCapacityProfile = ResourceManager.ReadWrite.Windows.Metrics.Configuration.MetricSnapshotCapacityProfile;
global using HostManagerMetricSnapshotSourcePolicyProfile = ResourceManager.ReadWrite.Windows.Metrics.Configuration.MetricSnapshotSourcePolicyProfile;
global using HostManagerMetricSnapshotRuleTemplateProfile = ResourceManager.ReadWrite.Windows.Metrics.Configuration.MetricSnapshotRuleTemplateProfile;
global using HostManagerMetricSnapshotHotPublishProfile = ResourceManager.ReadWrite.Windows.Metrics.Configuration.MetricSnapshotHotPublishProfile;
using System.Text.Json.Serialization;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerMetricSnapshotRecreateProfile : MetricSnapshotRecreateProfile
{
    [JsonPropertyOrder(1)]
    [JsonPropertyName("persistence_relative_path")]
    public required string PersistenceRelativePath { get; init; }
}
