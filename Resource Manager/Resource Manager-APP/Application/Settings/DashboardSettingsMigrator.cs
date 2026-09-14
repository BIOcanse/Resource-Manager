using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public sealed class DashboardSettingsMigrator
{
    public DashboardSettings ResolveForRead(
        DashboardSettingsUpdateResult result,
        HardwareMetricSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(result);
        var hasStoredSettings = result.Source.Kind
            is not DashboardSettingsSourceKind.BundledFirstRun
            and not DashboardSettingsSourceKind.RecoveredDefaultsAfterCorruption;
        if (snapshot is null)
        {
            return hasStoredSettings
                ? MigrateForRead(result.Settings)
                : DashboardSettingsDefaults.Create();
        }

        return hasStoredSettings
            ? MigrateForRead(result.Settings, snapshot)
            : DashboardSettingsDefaults.Create(snapshot);
    }

    public DashboardSettings MigrateForRead(DashboardSettings settings)
    {
        if (settings.Version < DashboardSettingsDefaults.MinimumSupportedVersion
            || settings.Version > DashboardSettingsDefaults.CurrentVersion)
        {
            return DashboardSettingsDefaults.Create();
        }

        return DashboardSettingsNormalizer.Normalize(settings);
    }

    public DashboardSettings MigrateForRead(
        DashboardSettings settings,
        HardwareMetricSnapshot snapshot)
    {
        if (settings.Version < DashboardSettingsDefaults.MinimumSupportedVersion
            || settings.Version > DashboardSettingsDefaults.CurrentVersion)
        {
            return DashboardSettingsDefaults.Create(snapshot);
        }

        var normalized = DashboardSettingsNormalizer.Normalize(settings);
        return DashboardSettingsBindingResolver.Resolve(normalized, snapshot);
    }

    public DashboardSettings SanitizeForSave(
        DashboardSettings settings,
        HardwareMetricSnapshot snapshot)
    {
        return DashboardSettingsBindingResolver.Resolve(
            DashboardSettingsNormalizer.Normalize(settings),
            snapshot);
    }

    public DashboardSettings SanitizeForSave(DashboardSettings settings)
    {
        return DashboardSettingsNormalizer.Normalize(settings);
    }

    public bool AreEquivalent(DashboardSettings left, DashboardSettings right)
    {
        if (left.Version != right.Version
            || left.Cards.Count != right.Cards.Count
            || left.ResourceBars.Count != right.ResourceBars.Count
            || (left.ResourceTableColumns?.Count ?? 0) != (right.ResourceTableColumns?.Count ?? 0)
            || (left.ResourceTableProcessColumns?.Count ?? 0) != (right.ResourceTableProcessColumns?.Count ?? 0))
        {
            return false;
        }

        for (var index = 0; index < left.Cards.Count; index++)
        {
            var leftCard = left.Cards[index];
            var rightCard = right.Cards[index];
            if (!string.Equals(leftCard.Id, rightCard.Id, StringComparison.Ordinal)
                || !string.Equals(leftCard.Main, rightCard.Main, StringComparison.Ordinal)
                || !leftCard.Small.SequenceEqual(rightCard.Small, StringComparer.Ordinal)
                || !BindingsEquivalent(leftCard.MainBinding, rightCard.MainBinding)
                || !BindingListsEquivalent(
                    leftCard.SmallBindings,
                    rightCard.SmallBindings,
                    leftCard.Small.Count))
            {
                return false;
            }
        }

        for (var index = 0; index < left.ResourceBars.Count; index++)
        {
            var leftBar = left.ResourceBars[index];
            var rightBar = right.ResourceBars[index];
            if (!string.Equals(leftBar.Id, rightBar.Id, StringComparison.Ordinal)
                || !string.Equals(leftBar.MetricId, rightBar.MetricId, StringComparison.Ordinal)
                || !string.Equals(leftBar.ScaleMode, rightBar.ScaleMode, StringComparison.Ordinal)
                || !BindingsEquivalent(leftBar.Binding, rightBar.Binding))
            {
                return false;
            }
        }

        return ColumnsEquivalent(left.ResourceTableColumns ?? [], right.ResourceTableColumns ?? [])
            && ColumnsEquivalent(left.ResourceTableProcessColumns ?? [], right.ResourceTableProcessColumns ?? []);
    }

    private static bool BindingListsEquivalent(
        IReadOnlyList<DashboardMetricBinding?>? left,
        IReadOnlyList<DashboardMetricBinding?>? right,
        int slotCount)
    {
        var leftValues = left ?? [];
        var rightValues = right ?? [];
        for (var index = 0; index < slotCount; index++)
        {
            var leftValue = index < leftValues.Count ? leftValues[index] : null;
            var rightValue = index < rightValues.Count ? rightValues[index] : null;
            if (!BindingsEquivalent(leftValue, rightValue))
            {
                return false;
            }
        }

        return leftValues.Skip(slotCount).All(static value => value is null)
            && rightValues.Skip(slotCount).All(static value => value is null);
    }

    private static bool BindingsEquivalent(
        DashboardMetricBinding? left,
        DashboardMetricBinding? right)
    {
        return left is null && right is null
            || left is not null
            && right is not null
            && string.Equals(
                left.ScopeKind,
                right.ScopeKind,
                StringComparison.Ordinal)
            && string.Equals(
                left.ScopeKey,
                right.ScopeKey,
                StringComparison.Ordinal);
    }

    private static bool ColumnsEquivalent(
        IReadOnlyList<ResourceTableColumnSettings> leftColumns,
        IReadOnlyList<ResourceTableColumnSettings> rightColumns)
    {
        if (leftColumns.Count != rightColumns.Count)
        {
            return false;
        }

        for (var index = 0; index < leftColumns.Count; index++)
        {
            var leftColumn = leftColumns[index];
            var rightColumn = rightColumns[index];
            if (!string.Equals(leftColumn.Id, rightColumn.Id, StringComparison.Ordinal)
                || leftColumn.Visible != rightColumn.Visible
                || Math.Abs((leftColumn.Width ?? 0) - (rightColumn.Width ?? 0)) > 0.1
                || !BindingsEquivalent(leftColumn.Binding, rightColumn.Binding))
            {
                return false;
            }
        }

        return true;
    }
}
