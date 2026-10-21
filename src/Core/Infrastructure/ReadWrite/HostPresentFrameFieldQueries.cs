using ResourceManager.ReadWrite.Windows.Frames;

namespace ResourceManager.App.Infrastructure.ReadWrite;

/// <summary>
/// Creates one caller-scoped ReadWriteCore frame owner over Host's existing Present source.
/// This factory does not discover a query, acquire ETW, or retain owners it returns.
/// </summary>
public sealed class HostPresentFrameFieldQueries
{
    private readonly IFrameTimingObservationSource source;

    public HostPresentFrameFieldQueries(IFrameTimingObservationSource source)
        => this.source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Create an owner for the caller's already-validated exact process/stream/window query.</summary>
    public WindowsPresentFrameFieldOwner Create(PresentFrameFieldQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new(source, query);
    }
}
