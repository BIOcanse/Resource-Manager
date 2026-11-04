using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

/// <summary>Flat identity facts supplied by discovery; all decisions remain native.</summary>
public sealed class MetricOwnerCatalogInput
{
    public required ReadOnlyMemory<ulong> SourceIds { get; init; }
    public required ReadOnlyMemory<ulong> SourceIncarnations { get; init; }
    public required ReadOnlyMemory<ulong> SourceCapabilities { get; init; }
    public required ReadOnlyMemory<uint> SourceAvailability { get; init; }
    public required ReadOnlyMemory<ulong> RuleIds { get; init; }
    public required ReadOnlyMemory<ulong> RuleSourceIds { get; init; }
    public required ReadOnlyMemory<ulong> RuleMetricIds { get; init; }
    public required ReadOnlyMemory<ulong> RuleIdentities { get; init; }
    public required ReadOnlyMemory<uint> RuleKinds { get; init; }
    public required ReadOnlyMemory<uint> RuleFlags { get; init; }
    public required ReadOnlyMemory<uint> RulePriorities { get; init; }
    public required ReadOnlyMemory<uint> RuleValueKinds { get; init; }
    public required ReadOnlyMemory<ulong> RuleMinimumBits { get; init; }
    public required ReadOnlyMemory<ulong> RuleMaximumBits { get; init; }
    public required ReadOnlyMemory<ulong> RuleCapabilityMasks { get; init; }
    public required ReadOnlyMemory<ulong> MetricIds { get; init; }
    public required ReadOnlyMemory<ulong> MetricIdentities { get; init; }

    internal unsafe void Replace(nint owner, MetricOwnerMemory work, long utcMs,
        ReadOnlySpan<MetricOwnerSourceBinding> origins = default, bool replaceBindings = false)
    {
        RequireLength(SourceIds.Length, SourceIncarnations.Length, SourceCapabilities.Length, SourceAvailability.Length);
        RequireLength(RuleIds.Length, RuleSourceIds.Length, RuleMetricIds.Length, RuleIdentities.Length,
            RuleKinds.Length, RuleFlags.Length, RulePriorities.Length, RuleValueKinds.Length,
            RuleMinimumBits.Length, RuleMaximumBits.Length, RuleCapabilityMasks.Length);
        RequireLength(MetricIds.Length, MetricIdentities.Length);
        fixed (ulong* sourceIds = SourceIds.Span, sourceIncarnations = SourceIncarnations.Span,
            sourceCapabilities = SourceCapabilities.Span, ruleIds = RuleIds.Span, ruleSources = RuleSourceIds.Span,
            ruleMetrics = RuleMetricIds.Span, ruleIdentities = RuleIdentities.Span,
            metricIds = MetricIds.Span, metricIdentities = MetricIdentities.Span,
            minimum = RuleMinimumBits.Span, maximum = RuleMaximumBits.Span, capabilities = RuleCapabilityMasks.Span)
        fixed (uint* ruleKinds = RuleKinds.Span, ruleFlags = RuleFlags.Span, rulePriorities = RulePriorities.Span,
            valueKinds = RuleValueKinds.Span, availability = SourceAvailability.Span)
        {
            var input = new MetricOwnerCatalog
            {
                Sources = new() { Count = checked((uint)SourceIds.Length), Ids = sourceIds,
                    Incarnations = sourceIncarnations, Capabilities = sourceCapabilities, Availability = availability },
                Rules = new() { Count = checked((uint)RuleIds.Length), Ids = ruleIds, SourceIds = ruleSources,
                    MetricIds = ruleMetrics, Identities = ruleIdentities, Kinds = ruleKinds,
                    Flags = ruleFlags, Priorities = rulePriorities, ValueKinds = valueKinds,
                    MinimumBits = minimum, MaximumBits = maximum, CapabilityMasks = capabilities },
                Metrics = new() { Count = checked((uint)MetricIds.Length), Ids = metricIds, Identities = metricIdentities }
            };
            if (replaceBindings)
            {
                fixed (MetricOwnerSourceBinding* bindings = origins)
                    MetricOwnerException.Check(MetricOwnerAbi.ReplaceCatalogBound(owner, in input, bindings,
                        checked((uint)origins.Length), (byte*)work.Address, work.Length, utcMs, out var failure), failure);
            }
            else
                MetricOwnerException.Check(MetricOwnerAbi.ReplaceCatalog(owner, in input,
                    (byte*)work.Address, work.Length, utcMs, out var failure), failure);
        }
    }

    private static void RequireLength(int expected, params ReadOnlySpan<int> lengths)
    {
        var index = 0;
        while (index < lengths.Length)
            if (lengths[index++] != expected) throw new ArgumentException("Catalog columns have different lengths.");
    }
}
