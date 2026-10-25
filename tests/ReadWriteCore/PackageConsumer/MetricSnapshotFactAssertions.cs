using ResourceManager.App.Domain.Metrics;

internal static class MetricSnapshotFactAssertions
{
    internal static bool SameCurrent(HardwareMetricSnapshot? left, HardwareMetricSnapshot? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.CapturedAt != right.CapturedAt || left.WorkspaceIdentity != right.WorkspaceIdentity
            || left.ConfigurationGeneration != right.ConfigurationGeneration
            || left.CatalogGeneration != right.CatalogGeneration || left.CommittedGeneration != right.CommittedGeneration
            || left.Items.Count != right.Items.Count || left.Datasets.Count != right.Datasets.Count) return false;
        foreach (var pair in left.Items)
        {
            if (!right.Items.TryGetValue(pair.Key, out var other)) return false;
            var value = pair.Value;
            if (value with { BytesValue = null } != other with { BytesValue = null }) return false;
            if (value.BytesValue is { } bytes ? other.BytesValue is null || !bytes.SequenceEqual(other.BytesValue)
                : other.BytesValue is not null) return false;
        }
        return left.Datasets.All(pair => right.Datasets.TryGetValue(pair.Key, out var other) && other == pair.Value);
    }
}
