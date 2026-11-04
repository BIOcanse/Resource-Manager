using System.Globalization;
using System.Numerics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Endpoints.Transport;

// Published typed readings with attribution, without row/column/view identities.
internal sealed record ResourceFieldFacts(string Field, string Unit, string SourceId,
    DateTimeOffset? CapturedAt, IReadOnlyList<ResourceFieldProcessValue> Processes,
    IReadOnlyList<ResourceFieldSoftwareValue> Software)
{
    public bool? AdditiveAcrossProcesses { get; init; }
}
internal sealed record ResourceFieldProcessValue(int ProcessIndex, ProcessMetricFieldValue Value)
{
    public double? Limit { get; init; }
    public IReadOnlyList<ResourceScopedFieldValue> Scopes { get; init; } = [];
}
internal sealed record ResourceScopedFieldValue(string Scope, ProcessMetricFieldValue Value);
internal sealed record ResourceFieldSoftwareValue(string SoftwareId, ProcessMetricFieldValue Value)
{
    public double? Limit { get; init; }
}

internal sealed partial record ResourceFactsSnapshot
{
    private static IReadOnlyList<ResourceFieldFacts> ProjectFields(ResourceBreakdownSnapshot source,
        IReadOnlyList<string> items, IReadOnlyList<ResourceProcessIdentity> identities,
        IReadOnlyList<ResourceProcessAttribution> attributions)
    {
        var result = new List<ResourceFieldFacts>();
        var publishedProcesses = source.ProcessFields?.Processes
            .GroupBy(process => (process.ProcessId, process.CreationWindowsFileTime100Ns))
            .ToDictionary(group => group.Key, group => group.ToArray()) ?? [];
        foreach (var requestedName in items.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var field = ProcessMetricFieldRegistry.Find(requestedName);
            if (field is null) continue;
            var name = field.Name;
            var snapshot = source.ProcessFields;
            var observation = snapshot?.Sources.FirstOrDefault(part => part.SourceId == field.SourceId);
            var capturedAt = observation?.CapturedAt ?? (snapshot?.Sources.Count == 0 ? snapshot.CapturedAt : null);
            var current = source.Datasets.Any(dataset => dataset.DatasetId == field.DatasetId
                && dataset.Status == ResourceBreakdownSamplingStatuses.Ready && dataset.CapturedAt is not null);
            ProcessMetricFieldValue Empty() => new(name, null, null);
            var values = identities.Select((identity, index) =>
            {
                // Match the creation key as well as PID. Scoped fields retain their declared scope.
                var readings = identity.ProcessId > 0 && identity.ProcessStartKey is not null
                    && publishedProcesses.TryGetValue((identity.ProcessId, identity.ProcessStartKey), out var rows)
                    ? rows.Where(process => field.GpuEngine is null || process.GpuEngine == field.GpuEngine)
                    .SelectMany(process => process.Values.Where(value => value.Field == name).Select(value =>
                        new ResourceScopedFieldValue(process.LogicalProcessor is { } cpu ? $"cpu:{cpu.LogicalProcessorId}"
                            : process.GpuEngine is { } gpu ? $"gpu:{gpu.AdapterLuid:x}:{gpu.PhysicalIndex}:{gpu.EngineIndex}"
                            : process.PhysicalDisk is { } disk ? disk.ToString() : string.Empty, value))).ToArray() : [];
                var matches = readings.Select(reading => reading.Value).ToArray();
                var value = !current || capturedAt is null || matches.Length == 0 ? Empty()
                    : matches.Length == 1 ? matches[0]
                    : Sum(name, matches, field.AdditiveAcrossProcesses == true);
                return new ResourceFieldProcessValue(index, value) { Scopes = current && capturedAt is not null
                    && matches.Length > 1 && field.AdditiveAcrossProcesses != true ? readings : [] };
            }).Where(value => identities[value.ProcessIndex].ProcessId > 0).ToArray();
            var byIndex = values.ToDictionary(value => value.ProcessIndex, value => value.Value);
            var software = field.AdditiveAcrossProcesses == true
                ? attributions.Where(edge => identities[edge.ProcessIndex].ProcessId > 0)
                    .GroupBy(edge => edge.SoftwareId).Select(group => new ResourceFieldSoftwareValue(group.Key,
                        Sum(name, group.Select(edge => byIndex.GetValueOrDefault(edge.ProcessIndex) ?? Empty()).ToArray(), true))).ToArray()
                : [];
            result.Add(new(name, field.Unit, field.SourceId, current ? capturedAt : null, values, software)
            { AdditiveAcrossProcesses = field.AdditiveAcrossProcesses });
        }
        return result;
    }

    private static ProcessMetricFieldValue Sum(string name, IReadOnlyList<ProcessMetricFieldValue> values, bool additive)
    {
        if (additive && values.Count > 0 && values.All(value => value.IntegerValue is not null))
        {
            var sum = values.Aggregate(BigInteger.Zero, (total, value) => total + value.IntegerValue!.Value.ToBigInteger());
            if (UInt128.TryParse(sum.ToString(CultureInfo.InvariantCulture), out var integer))
                return new(name, MetricInteger.FromUnsigned128(integer), null);
        }
        else if (additive && values.Count > 0 && values.All(value => value.NumericValue is not null))
        {
            var sum = values.Sum(value => value.NumericValue!.Value);
            if (double.IsFinite(sum)) return new(name, null, sum);
        }
        return new(name, null, null);
    }
}
