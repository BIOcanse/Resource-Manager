using System.Globalization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Projects one immutable counter window; never calls Windows or retains a previous value.</summary>
internal sealed class PhysicalMemoryResidualBreakdownProvider(
    PhysicalMemoryResidualCapture capture,
    IResourceResidualBreakdownProvider otherMetrics,
    Action<string> reportState) : IResourceResidualBreakdownProvider
{
    public IReadOnlyList<ResourceProcessSegment> CreateResidualSegments(ResourceResidualBreakdownRequest request)
    {
        if (!request.MetricId.Equals(ResourceBreakdownMetricIds.MemoryUsage, StringComparison.OrdinalIgnoreCase))
            return otherMetrics.CreateResidualSegments(request);
        if (!(request.Value > 0) || !double.IsFinite(request.Value)) return [];
        var parts = new List<(string Kind, string Name, double Bytes)>();
        var counters = capture.Counters;
        var state = "ready";
        if (counters is not null)
        {
            if (counters.NonPagedPoolBytes is { } nonPaged)
            {
                var tagTotal = capture.Tags is null ? 0
                    : capture.Tags.Sum(static tag => (double)tag.NonPagedBytes) + capture.OtherTagBytes;
                if (capture.Tags is null || tagTotal > nonPaged)
                {
                    Add(PhysicalMemoryResidualKinds.NonPagedPool, nonPaged);
                    if (tagTotal > nonPaged) state = "pool-tags-exceed-nonpaged-pool";
                }
                else
                {
                    foreach (var tag in capture.Tags)
                        Add(PhysicalMemoryResidualKinds.PoolTagPrefix + tag.Tag.ToString("X8", CultureInfo.InvariantCulture),
                            tag.NonPagedBytes, TagName(tag.Tag));
                    Add(PhysicalMemoryResidualKinds.OtherPoolTags, capture.OtherTagBytes);
                    Add(PhysicalMemoryResidualKinds.UnclassifiedPool, nonPaged - tagTotal);
                }
            }
            Add(PhysicalMemoryResidualKinds.PagedPoolResident, counters.ResidentPagedPoolBytes);
            Add(PhysicalMemoryResidualKinds.DriverResident, counters.ResidentDriverBytes);
            Add(PhysicalMemoryResidualKinds.CodeResident, counters.ResidentCodeBytes);
        }
        var measured = parts.Sum(static part => part.Bytes);
        // An incompatible window cannot support a partition. Do not fabricate smaller measured values.
        if (measured > request.Value)
        {
            reportState("components-exceed-residual");
            return [Segment(PhysicalMemoryResidualKinds.Unclassified, string.Empty, request.Value)];
        }
        reportState(state);
        Add(PhysicalMemoryResidualKinds.Unclassified, request.Value - measured);
        return parts.Select(part => Segment(part.Kind, part.Name, part.Bytes)).ToArray();

        void Add(string kind, double? bytes, string name = "")
        {
            if (bytes is > 0) parts.Add((kind, name, bytes.Value));
        }
        ResourceProcessSegment Segment(string kind, string name, double bytes) => new(-1, name, null,
            bytes, request.CapacityValue > 0 ? bytes * 100 / request.CapacityValue : 0,
            bytes * 100 / request.Value, AttributionKind: kind);
    }

    private static string TagName(uint tag)
    {
        Span<char> chars = stackalloc char[4];
        for (var index = 0; index < 4; index++)
        {
            var value = (byte)(tag >> (8 * index));
            if (value is < 32 or > 126) return tag.ToString("X8", CultureInfo.InvariantCulture);
            chars[index] = (char)value;
        }
        return new string(chars);
    }
}
