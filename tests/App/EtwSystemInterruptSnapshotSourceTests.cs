using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwSystemInterruptSnapshotSourceTests
{
    [Fact]
    public void ReadReturnsThePublishedSnapshotWithoutCreatingNewObservations()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var source = new EtwSystemInterruptSnapshotSource(
            new IdleKernelEtwSessionBroker(),
            fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);

        var first = source.Read();
        var repeated = source.Read();

        Assert.Same(first, repeated);

        source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        var frozen = source.Read();

        Assert.NotSame(first, frozen);
        Assert.Same(frozen, source.Read());
        Assert.Equal("Frozen", frozen.ProviderState.State);
    }

    [Fact]
    public void LateLeaseReleaseAfterSourceDisposalIsHarmless()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var source = new EtwSystemInterruptSnapshotSource(
            new IdleKernelEtwSessionBroker(),
            fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);
        var lease = source.AcquireSubscription(
            "report-owner",
            TimeSpan.FromSeconds(10));

        source.Dispose();

        var exception = Record.Exception(lease.Dispose);
        Assert.Null(exception);
    }

    private sealed class IdleKernelEtwSessionBroker : IKernelEtwSessionBroker
    {
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
            => throw new InvalidOperationException(
                "The cache-only read test must not start ETW.");

        public KernelEtwSessionSnapshot GetSnapshot()
            => new(
                "Idle",
                "No subscription.",
                0,
                DateTimeOffset.UnixEpoch,
                KernelTraceEventParser.Keywords.None,
                0,
                0);
    }
}
