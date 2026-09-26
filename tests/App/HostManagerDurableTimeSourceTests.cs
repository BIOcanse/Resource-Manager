using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerDurableTimeSourceTests
{
    [Fact]
    public void ClockRollbackAndRecordFloorCannotMoveDurableTimeBackwards()
    {
        var time = new ManualTimeProvider(
            DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var source = new HostManagerDurableTimeSource(time);

        Assert.Equal<ulong>(10_000, source.NextUtcMilliseconds());

        time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(9_000));
        Assert.Equal<ulong>(10_000, source.NextUtcMilliseconds());
        Assert.Equal<ulong>(12_000, source.NextUtcMilliseconds(12_000));

        time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(11_000));
        Assert.Equal<ulong>(12_000, source.NextUtcMilliseconds());
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(12_000),
            source.NextUtc());
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        public void SetUtcNow(DateTimeOffset value) => current = value;
    }
}
