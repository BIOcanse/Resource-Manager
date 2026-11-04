using System.Text.Json.Serialization;
using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

/// <summary>Compiled registry choices. Defaults belong only to the FreedomPoint declaration.</summary>
public sealed record MetricOwnerLimits
{
    [JsonPropertyName("work_byte_budget")]
    public required ulong WorkByteBudget { get; init; }
    [JsonPropertyName("payload_byte_budget")]
    public required ulong PayloadByteBudget { get; init; }
    [JsonPropertyName("maximum_value_bytes")]
    public required uint MaximumValueBytes { get; init; }
    [JsonPropertyName("read_retry_limit")]
    public required uint ReadRetryLimit { get; init; }
    [JsonPropertyName("small_owner_byte_budget")]
    public required ulong SmallOwnerByteBudget { get; init; }

    public MetricOwnerLimits Validate(ulong residentByteBudget)
    {
        RequirePositive(residentByteBudget, MetricOwnerFamily.Active);
        RequirePositive(WorkByteBudget, MetricOwnerFamily.Work);
        Cap(WorkByteBudget, 64UL * 1024 * 1024, MetricOwnerFamily.Work);
        Cap(WorkByteBudget, residentByteBudget, MetricOwnerFamily.Work);
        RequirePositive(PayloadByteBudget, MetricOwnerFamily.Payload);
        Cap(PayloadByteBudget, residentByteBudget, MetricOwnerFamily.Payload);
        RequirePositive(MaximumValueBytes, MetricOwnerFamily.Payload);
        Cap(MaximumValueBytes, PayloadByteBudget, MetricOwnerFamily.Payload);
        // Zero is the package's explicit single-attempt option, with no retries.
        RequirePositive(SmallOwnerByteBudget, MetricOwnerFamily.Active);
        Cap(SmallOwnerByteBudget, residentByteBudget, MetricOwnerFamily.Active);
        return this;
    }

    internal static void Cap(ulong required, ulong limit, MetricOwnerFamily family)
    {
        if (required > limit)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
                Family = family, Required = required, Limit = limit });
    }

    internal static void RequirePositive(ulong value, MetricOwnerFamily family)
    {
        if (value == 0)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.InvalidContract,
                Family = family, Required = 1, Limit = value });
    }
}

/// <summary>Reader caps derived from admitted callers and package-owned Q2 selected-copy sizing.</summary>
public sealed record MetricOwnerReaderLimits
{
    public uint MaximumObjectViewCount { get; }
    public ulong ReadCopyByteBudget { get; }

    private MetricOwnerReaderLimits(uint views, ulong bytes)
        => (MaximumObjectViewCount, ReadCopyByteBudget) = (views, bytes);

    public static MetricOwnerReaderLimits FromAdmission(uint subscriptionReaders, uint oneShotReaders,
        ResourceManager.ReadWrite.Native.SharedSection.SharedSectionReaderRequirements selectedStorage)
    {
        if (subscriptionReaders == 0 && oneShotReaders == 0) return new(0, 0);
        var bytes = selectedStorage.AlignedStorageBytes;
        if (bytes > ulong.MaxValue)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
                Family = MetricOwnerFamily.Binding, Required = ulong.MaxValue, Limit = ulong.MaxValue });
        return FromAdmission(subscriptionReaders, oneShotReaders, (ulong)bytes);
    }

    public static MetricOwnerReaderLimits FromAdmission(uint subscriptionReaders, uint oneShotReaders,
        ulong selectedCopyBytesPerReader)
    {
        var views = (ulong)subscriptionReaders + oneShotReaders;
        MetricOwnerLimits.Cap(views, uint.MaxValue, MetricOwnerFamily.Binding);
        if (views == 0) return new(0, 0);
        MetricOwnerLimits.RequirePositive(selectedCopyBytesPerReader, MetricOwnerFamily.Binding);
        MetricOwnerLimits.Cap(selectedCopyBytesPerReader, ulong.MaxValue / views, MetricOwnerFamily.Binding);
        return new((uint)views, views * selectedCopyBytesPerReader);
    }
}

/// <summary>
/// Actual admitted extents, never maxima to preallocate. PublishedPositionBytes includes
/// committed cold payload, history, provenance and table metadata/alignment once.
/// ReadCopyBytes includes the reader's data and source/position metadata, across all admitted readers.
/// </summary>
public readonly record struct MetricOwnerExtents(
    uint Sources, uint Rules, uint Metrics,
    ulong PayloadWorkBytes, ulong CommittedPayloadBytes, ulong PublishedPositionBytes,
    ulong SmallOwnerBytes, ulong ReadCopyBytes, uint ObjectViewCount)
{
    /// <summary>Native-sized discovery/catalog/learning continuity in the same active lease; zero declares none.</summary>
    public ulong ExtraActiveBytes { get; init; }
    /// <summary>Native-sized complete operation extent in the one work resource; zero declares kernel-only work.</summary>
    public ulong OperationWorkBytes { get; init; }
}
