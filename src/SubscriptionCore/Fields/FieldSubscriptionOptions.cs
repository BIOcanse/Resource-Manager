namespace ResourceManager.Subscription;

/// <summary>How a subscription obtains the values delivered on its own period.</summary>
public enum FieldSubscriptionSamplingMode
{
    HighestFrequencyShared,
    FreshOnTrigger
}

/// <summary>Alignment changes only the starting phase of subscriptions bound to
/// the same collection item. It never rounds their periods or invents observation times.</summary>
public sealed record FieldSubscriptionOptions(
    FieldSubscriptionSamplingMode SamplingMode = FieldSubscriptionSamplingMode.HighestFrequencyShared,
    bool AlignSameItemPhase = false)
{
    public static FieldSubscriptionOptions Default { get; } = new();

    internal void Validate()
    {
        if (SamplingMode is not (FieldSubscriptionSamplingMode.HighestFrequencyShared
            or FieldSubscriptionSamplingMode.FreshOnTrigger))
            throw new ArgumentOutOfRangeException(nameof(SamplingMode));
    }
}
