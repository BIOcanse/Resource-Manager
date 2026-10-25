using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    /// <summary>
    /// "需要 X" 只说给没装 X 的机器听。组件已经装好、这一项只是还没读出值时，
    /// 原因改成"暂无有效读数"，不再让用户去装一个已经装了的组件。
    /// </summary>
    public static IReadOnlyList<MetricDefinition> WithInstalledComponents(
        IEnumerable<MetricDefinition> definitions,
        IReadOnlySet<string> installedComponentIds)
        => definitions.Select(definition =>
                !definition.Selectable
                && definition.RequiredComponentId is { } componentId
                && installedComponentIds.Contains(componentId)
                    ? definition with
                    {
                        RequiredComponentId = null,
                        DisabledReason = BackendMessage.Create(
                            BackendMessageDomains.Metric,
                            BackendMessageCodes.Metric.NoValidReading)
                    }
                    : definition)
            .ToArray();

    private static void ApplyReadingAvailability(
        List<MetricDefinition> definitions,
        IEnumerable<MetricDeviceFieldBinding> bindings)
    {
        var fields = bindings.ToArray();
        var availableSources = fields
            .Where(static binding => binding.Field.Reading?.SourceAvailability == "available")
            .Select(static binding => binding.SourceId)
            .ToHashSet(StringComparer.Ordinal);
        var aliases = fields.SelectMany(static binding => binding.RequestAliases.Prepend(binding.RequestAddress)
                .Select(alias => (Alias: alias, Binding: binding)))
            .ToLookup(static entry => entry.Alias, static entry => entry.Binding, StringComparer.OrdinalIgnoreCase);
        var readings = fields.Where(static binding => binding.Field.Reading is not null)
            .ToLookup(static binding => binding.Field.Reading!.Id, StringComparer.Ordinal);

        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            var candidates = aliases[definition.Id].ToArray();
            if (candidates.Length == 0) continue;
            if (!candidates.Any(binding => string.Equals(binding.RequestAddress, definition.Id, StringComparison.OrdinalIgnoreCase)))
            {
                candidates = candidates.Concat(candidates.Where(static binding => binding.Field.Reading is not null)
                        .SelectMany(binding => readings[binding.Field.Reading!.Id]))
                    .DistinctBy(static binding => binding.RequestAddress, StringComparer.OrdinalIgnoreCase).ToArray();
                definition = definition with
                {
                    CandidateSources = candidates.OrderBy(static binding => binding.Field.Reading?.SourceRank ?? int.MaxValue)
                        .ThenBy(static binding => binding.RequestAddress, StringComparer.Ordinal)
                        .Select(binding => new MetricSourceCandidate(binding.Field.Name, binding.DeviceIdentity,
                            binding.SourceId, binding.Field.Reading, binding.Field.Reading?.SourceAvailability ?? "unverified",
                            binding.SelectedRequestAliases.Contains(definition.Id, StringComparer.OrdinalIgnoreCase))).ToArray()
                };
                definitions[index] = definition;
            }
            var preferredAvailable = candidates.Any(static binding => binding.Field.Reading is
                { IsPreferredSource: true, SourceAvailability: "available" });
            var unsupported = candidates.All(static binding => binding.Field.Reading?.SourceAvailability == "unsupported");
            // A confirmed field proves this provider can run, not that every
            // sensor it declares exists. Do not infer support from a sibling.
            var sourceAvailable = candidates.Any(binding => availableSources.Contains(binding.SourceId));
            if (!preferredAvailable && !unsupported && !sourceAvailable) continue;

            var selectable = preferredAvailable || (!unsupported && definition.Selectable);
            definitions[index] = definition with
            {
                Selectable = selectable,
                RequiredComponentId = null,
                DisabledReason = selectable ? null : BackendMessage.Create(
                    BackendMessageDomains.Metric,
                    unsupported ? BackendMessageCodes.Metric.NotExposed : BackendMessageCodes.Metric.NoValidReading)
            };
        }
    }
}
