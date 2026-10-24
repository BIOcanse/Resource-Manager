using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class PresentFrameEtwRecoveryTests
{
    [Fact]
    public void ExistingLeaseCanReconcileFailedStartAndAdditionalBorrowersWithoutAllLeasesLeaving()
    {
        var starts = 0;
        using var source = new PresentFrameEtwSource(NullLogger<PresentFrameEtwSource>.Instance,
            _ => { starts++; throw new InvalidOperationException("recorded failed startup"); });
        using var first = source.AcquireSubscription();
        Assert.Equal(1, starts);
        Assert.Null(source.Read(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, starts);
        using (source.AcquireSubscription()) Assert.Equal(3, starts);
        Assert.Null(source.ReadIntervals(DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow));
        Assert.Equal(4, starts);
        first.Dispose();
        Assert.Null(source.Read(TimeSpan.FromSeconds(1)));
        Assert.Equal(4, starts);
    }
}
