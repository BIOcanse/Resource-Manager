using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Application.ProcessAttribution;

public enum RuntimeAttributionObservationStatus : byte
{
    NoMatch = 1,
    Unavailable = 2,
    Matched = 3
}

public readonly struct RuntimeAttributionObservation
{
    private RuntimeAttributionObservation(
        RuntimeAttributionObservationStatus status,
        RuntimeSoftwareAttribution? attribution)
    {
        Status = status;
        Attribution = attribution;
    }

    public RuntimeAttributionObservationStatus Status { get; }

    public RuntimeSoftwareAttribution? Attribution { get; }

    public static RuntimeAttributionObservation NoMatch { get; } =
        new(RuntimeAttributionObservationStatus.NoMatch, null);

    public static RuntimeAttributionObservation Unavailable { get; } =
        new(RuntimeAttributionObservationStatus.Unavailable, null);

    public static RuntimeAttributionObservation Matched(RuntimeSoftwareAttribution attribution)
        => new(
            RuntimeAttributionObservationStatus.Matched,
            attribution ?? throw new ArgumentNullException(nameof(attribution)));
}
