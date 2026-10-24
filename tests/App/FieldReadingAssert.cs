using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// A periodic owner keeps publishing while a test runs, so its current value may already be a newer
/// publication of the same reading by the time the test reads the cache after a delivery.
/// </summary>
internal static class FieldReadingAssert
{
    /// <summary>The cached reading is the published one or a newer publication of the same reading.</summary>
    internal static void CurrentOrNewer(FieldReading published, FieldReading? cached)
    {
        Assert.NotNull(cached);
        Assert.Equal(published.Field, cached.Field);
        Assert.Equal(published.Status, cached.Status);
        Assert.Equal(published.Value, cached.Value);
        Assert.Equal(published.SourceId, cached.SourceId);
        Assert.True(published.ObservedAt is null || cached.ObservedAt >= published.ObservedAt,
            $"The cached reading ({cached.ObservedAt:O}) is older than the published one ({published.ObservedAt:O}).");
    }
}
