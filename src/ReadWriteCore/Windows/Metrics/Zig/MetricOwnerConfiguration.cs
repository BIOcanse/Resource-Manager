using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

/// <summary>Handoff of compiled choices and actual admitted extents; never creates an owner.</summary>
public static class MetricOwnerConfiguration
{
    /// <summary>
    /// Validates before acquiring work or publishing. Native sizing owns the layout equation,
    /// including catalog replacement and the explicit shared payload work pool.
    /// </summary>
    public static MetricOwnerConfig Create(NativeMetricSnapshotConfiguration compiled,
        MetricOwnerLimits limits, MetricOwnerExtents extents, MetricOwnerReaderLimits readers)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(readers);
        limits.Validate(compiled.ResidentByteBudget);
        MetricOwnerLimits.RequirePositive(extents.Sources, MetricOwnerFamily.Sources);
        MetricOwnerLimits.Cap(extents.Sources, compiled.MaximumSourceCount, MetricOwnerFamily.Sources);
        MetricOwnerLimits.Cap(extents.Rules, compiled.MaximumRuleCount, MetricOwnerFamily.Rules);
        MetricOwnerLimits.Cap(extents.Metrics, compiled.MaximumMetricCount, MetricOwnerFamily.Metrics);
        MetricOwnerLimits.Cap(extents.PayloadWorkBytes, limits.WorkByteBudget, MetricOwnerFamily.Work);
        MetricOwnerLimits.Cap(extents.OperationWorkBytes, limits.WorkByteBudget, MetricOwnerFamily.Work);
        MetricOwnerLimits.Cap(extents.ExtraActiveBytes, compiled.ResidentByteBudget, MetricOwnerFamily.Active);
        MetricOwnerLimits.Cap(extents.CommittedPayloadBytes, limits.PayloadByteBudget, MetricOwnerFamily.Payload);
        MetricOwnerLimits.Cap(extents.CommittedPayloadBytes, extents.PublishedPositionBytes, MetricOwnerFamily.Payload);
        MetricOwnerLimits.Cap(extents.SmallOwnerBytes, limits.SmallOwnerByteBudget, MetricOwnerFamily.Active);
        MetricOwnerLimits.Cap(extents.ObjectViewCount, readers.MaximumObjectViewCount, MetricOwnerFamily.Binding);
        MetricOwnerLimits.Cap(readers.ReadCopyByteBudget, compiled.ResidentByteBudget, MetricOwnerFamily.Binding);
        MetricOwnerLimits.Cap(extents.ReadCopyBytes, readers.ReadCopyByteBudget, MetricOwnerFamily.Binding);
        MetricOwnerLimits.Cap(compiled.MaximumFutureSkewMilliseconds, uint.MaxValue, MetricOwnerFamily.Binding);

        // Catch publication/reader/header over-admission before even calling the sizing ABI.
        ValidateRequiredSizes(default, compiled.ResidentByteBudget, limits, extents);
        var config = new MetricOwnerConfig
        {
            AbiVersion = MetricOwnerAbi.Version,
            SourceCapacity = extents.Sources,
            RuleCapacity = extents.Rules,
            MetricCapacity = extents.Metrics,
            MaxValueBytes = limits.MaximumValueBytes,
            MaximumFutureSkewMs = (uint)compiled.MaximumFutureSkewMilliseconds,
            ActiveLimit = compiled.ResidentByteBudget,
            WorkLimit = limits.WorkByteBudget,
            PayloadWorkBytes = extents.PayloadWorkBytes
        };
        var sizes = ZigHardwareMetricRun.RequiredSizes(config);
        ValidateRequiredSizes(sizes, compiled.ResidentByteBudget, limits, extents);
        return config;
    }

    // Native supplies exact sizes; admission adds external table/reader/header extents.
    // CommittedPayloadBytes is already included in PublishedPositionBytes.
    internal static ulong ValidateRequiredSizes(MetricOwnerSizes sizes, ulong residentByteBudget,
        MetricOwnerLimits limits, MetricOwnerExtents extents)
    {
        var work = Math.Max(sizes.Work, extents.OperationWorkBytes);
        MetricOwnerLimits.Cap(work, limits.WorkByteBudget, MetricOwnerFamily.Work);
        MetricOwnerLimits.Cap(sizes.Active, residentByteBudget, MetricOwnerFamily.Active);
        var total = (UInt128)sizes.Active + extents.ExtraActiveBytes + work + extents.PublishedPositionBytes
            + extents.SmallOwnerBytes + extents.ReadCopyBytes;
        if (total > residentByteBudget)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
                Family = MetricOwnerFamily.Active,
                Required = total > ulong.MaxValue ? ulong.MaxValue : (ulong)total,
                Limit = residentByteBudget });
        return (ulong)total;
    }
}
