using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    /// <summary>
    /// A registered, unprobed reading can be requested once its component is installed.
    /// Missing samples do not prove that the device cannot expose that reading.
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
                        Selectable = definition.CandidateSources.Any(static candidate => candidate.SourceAvailability != "unsupported"),
                        DisabledReason = definition.CandidateSources.Any(static candidate => candidate.SourceAvailability != "unsupported")
                            ? null : BackendMessage.Create(
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
            }
            definition = definition with
            {
                CandidateSources = candidates.OrderBy(static binding => binding.Field.Reading?.SourceRank ?? int.MaxValue)
                        .ThenBy(static binding => binding.RequestAddress, StringComparer.Ordinal)
                        .Select(binding => new MetricSourceCandidate(binding.Field.Name, binding.DeviceIdentity,
                            binding.SourceId, binding.Field.Reading, binding.Field.Reading?.SourceAvailability ?? "unverified",
                            binding.SelectedRequestAliases.Contains(definition.Id, StringComparer.OrdinalIgnoreCase))).ToArray()
            };
            definitions[index] = definition;
            // Managed setting declarations have their own readback contract.
            // A control binding alone is not a telemetry capability declaration.
            if (candidates.All(static binding => binding.Field.Reading is null)) continue;
            var preferredAvailable = candidates.Any(static binding => binding.Field.Reading is
                { IsPreferredSource: true, SourceAvailability: "available" });
            var unsupported = candidates.All(static binding => binding.Field.Reading?.SourceAvailability == "unsupported");
            // Selection permits requesting a bound reading; it is not a claim
            // that an unprobed sensor has already produced a valid sample.
            var sourceAvailable = candidates.Any(binding => availableSources.Contains(binding.SourceId));
            if (!preferredAvailable && !unsupported && !sourceAvailable && definition.RequiredComponentId is not null) continue;

            var selectable = !unsupported;
            definitions[index] = definition with
            {
                Selectable = selectable,
                RequiredComponentId = null,
                DisabledReason = selectable ? null : BackendMessage.Create(
                    BackendMessageDomains.Metric,
                    BackendMessageCodes.Metric.NotExposed)
            };
        }
    }
}
