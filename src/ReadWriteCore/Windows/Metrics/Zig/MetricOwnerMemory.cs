namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

/// <summary>A borrowed region. Valid only inside the resource's UseAsync callback.</summary>
public readonly record struct MetricOwnerMemory(nint Address, ulong Length, ulong AllocationNumber);

public delegate ValueTask MetricOwnerMemoryUse(MetricOwnerMemory memory, CancellationToken cancellationToken);

/// <summary>
/// A stable Ledger resource identity, not an acquired lease. Implementations acquire
/// and pin in UseAsync locals, await the complete callback/drain, then unpin and
/// end use in finally. Reclaimed work is pushed again under the same UID.
/// </summary>
public interface IMetricOwnerMemoryResource : IDisposable
{
    ulong Length { get; }
    ValueTask UseAsync(MetricOwnerMemoryUse use, CancellationToken cancellationToken = default);
}

/// <summary>Publication admission from the existing subscription shell.</summary>
public delegate bool MetricOwnerPublicationPermit(Action commit);
