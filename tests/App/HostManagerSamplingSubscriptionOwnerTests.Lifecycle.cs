using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSamplingSubscriptionOwnerTests
{
    [Fact]
    public void RetainedRetiredReaderRejectsAThirdWorkspaceAndReturningItAdmitsTheNextPublication()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var first = fixture.Owner.Acquire(1); var firstSession = first.Session;
        PublishSamplingPlan(fixture, 2);
        using var second = fixture.Owner.Acquire(1);
        Assert.NotSame(first.Session, second.Session); Assert.False(first.OwnerToken.IsActive);
        _ = firstSession.Capacity;
        PublishSamplingPlan(fixture, 3);
        using (var unchanged = fixture.Owner.Acquire(1))
        {
            Assert.Same(second.Session, unchanged.Session);
            Assert.Equal(second.WorkspaceIncarnation, unchanged.WorkspaceIncarnation);
            Assert.True(unchanged.OwnerToken.IsActive);
            Assert.Equal(1200, unchanged.HotPublish.DefaultIntervalMilliseconds);
        }
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => firstSession.Capacity);
        PublishSamplingPlan(fixture, 4);
        using var next = fixture.Owner.Acquire(1);
        Assert.NotSame(second.Session, next.Session);
        Assert.Equal(second.WorkspaceIncarnation + 1, next.WorkspaceIncarnation);
        Assert.False(second.OwnerToken.IsActive); Assert.True(next.OwnerToken.IsActive);
        Assert.Equal(1400, next.HotPublish.DefaultIntervalMilliseconds);
    }

    [Fact]
    public async Task StopRevokesBothGenerationsAndDisposeRetainsItsCloseObligationsUntilReadersFinish()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var first = fixture.Owner.Acquire(1);
        PublishSamplingPlan(fixture, 2);
        using var second = fixture.Owner.Acquire(1);
        await fixture.Owner.StopAsync(CancellationToken.None);
        Assert.False(first.OwnerToken.IsActive); Assert.False(second.OwnerToken.IsActive);
        Assert.Throws<InvalidOperationException>(() => fixture.Owner.Acquire(1));
        Assert.Throws<InvalidOperationException>(() => fixture.Owner.Dispose());
        _ = first.Session.Capacity; _ = second.Session.Capacity;
        second.Dispose();
        Assert.Throws<ObjectDisposedException>(() => second.Session.Capacity);
        Assert.Throws<InvalidOperationException>(() => fixture.Owner.Dispose());
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Session.Capacity);
        await fixture.Owner.StopAsync(CancellationToken.None);
        fixture.Owner.Dispose(); fixture.Owner.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Owner.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RestartWaitsForTheStoppedReaderWithoutRevivingItsToken()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var first = fixture.Owner.Acquire(1);
        await fixture.Owner.StopAsync(CancellationToken.None);
        PublishSamplingPlan(fixture, 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Owner.StartAsync(CancellationToken.None));
        Assert.False(first.OwnerToken.IsActive);
        _ = first.Session.Capacity;
        first.Dispose();
        await fixture.Owner.StartAsync(CancellationToken.None);
        using var next = fixture.Owner.Acquire(1);
        Assert.NotSame(first.Session, next.Session); Assert.NotSame(first.OwnerToken, next.OwnerToken);
        Assert.True(next.WorkspaceIncarnation > first.WorkspaceIncarnation); Assert.True(next.OwnerToken.IsActive);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(8)]
    public async Task InvalidRoleDoesNotLeaveAnUnreturnableNativeReference(int role)
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var lease = fixture.Owner.Acquire(1); var session = lease.Session; lease.Dispose();
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Owner.Acquire(role));
        await fixture.Owner.StopAsync(CancellationToken.None);
        Assert.Throws<ObjectDisposedException>(() => session.Capacity);
        fixture.Owner.Dispose();
    }

    [Fact]
    public void UninitializedAndRejectedWorkspaceMetadataStillHasAnExplicitIdempotentClose()
    {
        using var workspace = new NativeSamplingSubscriptionWorkspace();
        Assert.Throws<InvalidOperationException>(() => workspace.GetSession(1));
        Assert.Throws<InvalidOperationException>(() => workspace.Initialize(CompiledHostManagerSamplingSubscriptionPlan.Unpublished));
        workspace.Dispose(); workspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => workspace.GetSession(1));
        Assert.Throws<ObjectDisposedException>(() => workspace.Initialize(HostManagerTestPlanFactory.CreatePlan().SamplingSubscription));
    }

    private static void PublishSamplingPlan(HostManagerSamplingSubscriptionTestFixture fixture, int version)
        => fixture.Provider.Publish(CompiledRuntimePlan.Default with {
            Version = version,
            HostManager = HostManagerTestPlanFactory.CreatePlan(root => {
                root["profile_revision"] = 90 + version;
                root["hot_publish"]!["sampling_subscription"]!["roles"]![0]!["default_interval_ms"] = 1000 + version * 100;
            }, planEpoch: checked((ulong)version))
        });
}
