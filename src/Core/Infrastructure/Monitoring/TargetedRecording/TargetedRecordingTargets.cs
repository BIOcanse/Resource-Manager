using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

/// <summary>One fresh resource breakdown with process detail, for the start check and the target list.</summary>
public interface ITargetedRecordingProcessReader
{
    Task<ResourceBreakdownSnapshot?> ReadOnceAsync(CancellationToken cancellationToken);
}

/// <summary>Registers named demand for one breakdown attempt and releases it (no retained old sample).</summary>
internal sealed class ResourceBreakdownTargetedRecordingProcessReader(IResourceBreakdownPushSource source)
    : ITargetedRecordingProcessReader
{
    public const string Holder = "targeted-recording:targets";

    public Task<ResourceBreakdownSnapshot?> ReadOnceAsync(CancellationToken cancellationToken)
        => source.FetchOnceAsync(Holder, TargetedRecordingTargets.ProcessRequest, cancellationToken);
}

/// <summary>Maps registry software to breakdown segments. The start check, the target list and the running
/// recording all use this one matcher, so a listed target never fails the start check.</summary>
/// <remarks>Breakdown segments carry runtime attribution ids. The same product can have several registry
/// records (an uninstall entry, the portable catalog:&lt;identity&gt; record, a classified source record), and
/// attribution picks one of them. Matching only the requested record id therefore found no process when the
/// processes were attributed to an equivalent record (Google Chrome: catalog:app-google-chrome requested,
/// processes attributed to the installed record).</remarks>
public static class TargetedRecordingTargets
{
    private const string CatalogPrefix = "catalog:";
    private const string ResidualPrefix = "resource-residual:";

    public static readonly ResourceBreakdownSampleRequest ProcessRequest = new(
        [ResourceBreakdownMetricIds.MemoryUsage], new Dictionary<string, string>(),
        ProcessSampleDetailLevel.ResourceTableFull);

    /// <summary>Segment ids that name the requested software: the record id, catalog:&lt;identity&gt;, the
    /// record's source id, and every record sharing its software identity or naming it as source.</summary>
    public static IReadOnlySet<string> EquivalentIds(string requestedId, IReadOnlyList<SoftwareRecord> records)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { requestedId };
        var record = records.FirstOrDefault(item => string.Equals(item.Id, requestedId, StringComparison.OrdinalIgnoreCase));
        if (record is null) return ids;
        ids.Add(record.Id);
        var identity = Identity(record);
        if (identity is not null) ids.Add(CatalogPrefix + identity);
        if (!string.IsNullOrWhiteSpace(record.SourceSoftwareId)) ids.Add(record.SourceSoftwareId);
        foreach (var other in records)
        {
            if (identity is not null && string.Equals(Identity(other), identity, StringComparison.OrdinalIgnoreCase))
                ids.Add(other.Id);
            if (string.Equals(other.SourceSoftwareId, record.Id, StringComparison.OrdinalIgnoreCase))
                ids.Add(other.Id);
        }
        ids.RemoveWhere(IsNotSoftware);
        ids.Add(requestedId);
        return ids;
    }

    /// <summary>Distinct running processes of the matched segments across all bars.</summary>
    public static IReadOnlyList<ResourceProcessSegment> Processes(ResourceBreakdownSnapshot? snapshot,
        IReadOnlySet<string> ids)
    {
        if (snapshot is null || snapshot.Sampling.Status != ResourceBreakdownSamplingStatuses.Ready) return [];
        var seen = new HashSet<(int, long)>();
        var result = new List<ResourceProcessSegment>();
        foreach (var bar in snapshot.Bars)
        foreach (var segment in bar.Software)
        {
            if (!ids.Contains(segment.SoftwareId)) continue;
            foreach (var process in segment.Processes)
            {
                if (process.ProcessId <= 0 || process.AttributionKind != ResourceProcessAttributionKinds.Process) continue;
                if (seen.Add((process.ProcessId, process.ProcessStartKey ?? 0))) result.Add(process);
            }
        }
        return result;
    }

    /// <summary>The software's total value in one bar, summed over the matched segments; null when the bar is
    /// absent or the snapshot is not ready.</summary>
    public static double? Value(ResourceBreakdownSnapshot? snapshot, string metricId, IReadOnlySet<string> ids,
        out string? unit)
    {
        unit = null;
        if (snapshot is null || snapshot.Sampling.Status != ResourceBreakdownSamplingStatuses.Ready) return null;
        var bar = snapshot.Bars.FirstOrDefault(item => string.Equals(item.MetricId, metricId, StringComparison.OrdinalIgnoreCase));
        if (bar is null || bar.ObservationStatus is not (SamplingObservationStatus.Current or SamplingObservationStatus.Partial)) return null;
        unit = bar.Unit;
        var total = 0d;
        foreach (var segment in bar.Software)
            if (ids.Contains(segment.SoftwareId)) total += segment.Value;
        return total;
    }

    /// <summary>Software with at least one running attributed process, one entry per software id. A segment is
    /// assigned to the record with its exact id, else to the first equivalent record, else listed under its own
    /// runtime id. Dependency and support records are not targets.</summary>
    public static IReadOnlyList<TargetedRecordingTarget> List(ResourceBreakdownSnapshot? snapshot,
        IReadOnlyList<SoftwareRecord> records)
    {
        if (snapshot is null || snapshot.Sampling.Status != ResourceBreakdownSamplingStatuses.Ready) return [];
        var byId = new Dictionary<string, SoftwareRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records) byId.TryAdd(record.Id, record);
        var equivalent = new Dictionary<string, SoftwareRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.OrderBy(static item => item.Id, StringComparer.Ordinal))
            foreach (var id in EquivalentIds(record.Id, records))
                equivalent.TryAdd(id, record);

        var targets = new Dictionary<string, (string Name, HashSet<(int, long)> Processes)>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bar in snapshot.Bars)
        foreach (var segment in bar.Software)
        {
            if (IsNotSoftware(segment.SoftwareId)) continue;
            var record = byId.GetValueOrDefault(segment.SoftwareId) ?? equivalent.GetValueOrDefault(segment.SoftwareId);
            var id = record?.Id ?? segment.SoftwareId;
            if (record?.ManagementRole is SoftwareManagementRoles.Dependency or SoftwareManagementRoles.Support)
            {
                excluded.Add(id);
                continue;
            }
            if (!targets.TryGetValue(id, out var target))
            {
                target = (record?.Name ?? segment.Name, new HashSet<(int, long)>());
                targets[id] = target;
            }
            foreach (var process in segment.Processes)
                if (process.ProcessId > 0 && process.AttributionKind == ResourceProcessAttributionKinds.Process)
                    target.Processes.Add((process.ProcessId, process.ProcessStartKey ?? 0));
        }
        return targets.Where(static entry => entry.Value.Processes.Count > 0)
            .Select(static entry => new TargetedRecordingTarget(entry.Key, entry.Value.Name, entry.Value.Processes.Count))
            .OrderBy(static target => target.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static target => target.SoftwareId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The display name of a running runtime-only software id, when no registry record has it.</summary>
    public static string? RuntimeName(ResourceBreakdownSnapshot? snapshot, string softwareId)
        => snapshot?.Bars.SelectMany(static bar => bar.Software)
            .FirstOrDefault(segment => string.Equals(segment.SoftwareId, softwareId, StringComparison.OrdinalIgnoreCase))?.Name;

    private static string? Identity(SoftwareRecord record)
        => !string.IsNullOrWhiteSpace(record.SoftwareIdentityId) ? record.SoftwareIdentityId
            : record.Id.StartsWith(CatalogPrefix, StringComparison.OrdinalIgnoreCase) ? record.Id[CatalogPrefix.Length..] : null;

    private static bool IsNotSoftware(string id)
        => id.StartsWith(ResidualPrefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, RuntimeAttributionIds.Unattributed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, RuntimeAttributionIds.WindowsSystem, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, RuntimeAttributionIds.WindowsComponent, StringComparison.OrdinalIgnoreCase);
}
