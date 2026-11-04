namespace ResourceManager.App.Domain.Metrics;

public enum SamplingObservationStatus : byte
{
    Invalid = 0,
    Current = 1,
    RetainedLastGood = 2,
    Unavailable = 3,
    NotRequested = 4,
    Unsupported = 5,
    Partial = 6,
    Warming = 7
}

