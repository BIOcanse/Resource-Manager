using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAccessRequestOwnerTests
{
    private static readonly DateTimeOffset InitialTime = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public async Task PendingFactIncludesTerminalRetentionAndSurvivesWorkerStop()
    {
        var clock = new ManualTimeProvider(InitialTime);
        var owner = CreateOwner(TrustedAccessTestFactory.Create().Owner, clock, 1);
        var changes = new List<bool>();
        owner.PendingRequestsChanged += () => changes.Add(owner.HasPendingRequests);
        Assert.False(owner.HasPendingRequests);
        var created = await owner.CreateAsync(new TrustedAccessRequestCreateCommand(
            "Pending helper", ["read"], @"C:\Tools\helper.exe", 1234), CancellationToken.None);
        Assert.True(owner.HasPendingRequests);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        Assert.True(owner.HasPendingRequests);
        await owner.DenyAsync(created.RequestId!, TrustedAccessRequestDenialReason.Unsupported, CancellationToken.None);
        Assert.True(owner.HasPendingRequests);
        clock.Advance(TimeSpan.FromMilliseconds(2));
        await using var subscription = owner.SubscribePendingAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        Assert.Empty(subscription.Current);
        Assert.False(owner.HasPendingRequests);
        Assert.Equal(new[] { true, false }, changes);
    }

    [Fact]
    public async Task RequestSnapshotApprovalAndDeliveryAreBoundedAndOneTime()
    {
        var keys = TrustedAccessTestFactory.Create().Owner;
        var owner = CreateOwner(keys, new ManualTimeProvider(InitialTime), lifetimeMilliseconds: 300_000);
        var created = await owner.CreateAsync(
            new TrustedAccessRequestCreateCommand(
                "Desktop helper",
                ["read", "metrics.read"],
                @"C:\Tools\helper.exe",
                1234),
            CancellationToken.None);

        Assert.True(created.Created);
        Assert.Equal(64, created.RequestId!.Length);
        Assert.Equal(64, created.DeliverySecret!.Length);

        await using (var subscriber = owner.SubscribePendingAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await subscriber.MoveNextAsync());
            var pending = Assert.Single(subscriber.Current);
            Assert.Equal(created.RequestId, pending.RequestId);
            Assert.Equal(new[] { "read", "metrics.read" }, pending.RequestedScopes);
            Assert.Equal(@"C:\Tools\helper.exe", pending.ObservedExecutablePath);
            Assert.Equal(1234, pending.ObservedProcessId);
        }

        var approved = await owner.ApproveAsync(
            created.RequestId,
            ["read"],
            ["read"],
            approverIsAdministrator: false,
            cancellationToken: CancellationToken.None);
        Assert.Equal(TrustedAccessRequestStatus.Approved, approved.Status);
        Assert.Equal(new[] { "read" }, approved.Key!.Scopes);

        var delivered = await owner.WaitAsync(
            created.RequestId,
            created.DeliverySecret,
            CancellationToken.None);
        Assert.Equal(TrustedAccessRequestStatus.Approved, delivered.Status);
        Assert.Equal(approved.Key.Id, delivered.Key!.Id);
        Assert.False(string.IsNullOrWhiteSpace(delivered.ApiKey));

        var secondDelivery = await owner.WaitAsync(
            created.RequestId,
            created.DeliverySecret,
            CancellationToken.None);
        Assert.Equal(TrustedAccessRequestStatus.Delivered, secondDelivery.Status);
        Assert.Null(secondDelivery.ApiKey);

        var deniedRequest = await owner.CreateAsync(
            new TrustedAccessRequestCreateCommand(
                "Another helper",
                ["read"],
                @"C:\Tools\other.exe",
                5678),
            CancellationToken.None);
        var denied = await owner.DenyAsync(
            deniedRequest.RequestId!,
            TrustedAccessRequestDenialReason.Unsupported,
            CancellationToken.None);
        Assert.Equal(TrustedAccessRequestStatus.Denied, denied.Status);
        Assert.Equal(TrustedAccessRequestDenialReason.Unsupported, denied.DenialReason);

        var waitDenied = await owner.WaitAsync(
            deniedRequest.RequestId!,
            deniedRequest.DeliverySecret!,
            CancellationToken.None);
        Assert.Equal(TrustedAccessRequestStatus.Denied, waitDenied.Status);
        Assert.Equal(TrustedAccessRequestDenialReason.Unsupported, waitDenied.DenialReason);
    }

    [Fact]
    public async Task ExpiredUndeliveredRequestDoesNotRevokeItsNamedKey()
    {
        var keys = TrustedAccessTestFactory.Create().Owner;
        var clock = new ManualTimeProvider(InitialTime);
        var owner = CreateOwner(keys, clock, lifetimeMilliseconds: 1);
        var created = await owner.CreateAsync(
            new TrustedAccessRequestCreateCommand(
                "Short delivery",
                ["read"],
                @"C:\Tools\short.exe",
                1234),
            CancellationToken.None);
        var approved = await owner.ApproveAsync(
            created.RequestId!,
            ["read"],
            ["read"],
            approverIsAdministrator: false,
            cancellationToken: CancellationToken.None);

        clock.Advance(TimeSpan.FromMilliseconds(2));
        var expired = await owner.WaitAsync(
            created.RequestId!,
            created.DeliverySecret!,
            CancellationToken.None);

        Assert.Equal(TrustedAccessRequestStatus.Expired, expired.Status);
        Assert.Contains((await keys.ListAsync(CancellationToken.None)),
            credential => credential.Id == approved.Key!.Id);
    }

    private static TrustedAccessRequestOwner CreateOwner(
        ITrustedAccessKeyService keys,
        TimeProvider timeProvider,
        int lifetimeMilliseconds)
    {
        var runtimePlan = CompiledRuntimePlan.Default with
        {
            HostManager = CompiledHostManagerPlan.Unpublished with
            {
                TrustedAccessRequestLifetimeMilliseconds = lifetimeMilliseconds
            }
        };
        return new TrustedAccessRequestOwner(keys, new StaticRuntimePlanProvider(runtimePlan), timeProvider);
    }

    private sealed class StaticRuntimePlanProvider(CompiledRuntimePlan plan) : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = plan;

        public RuntimePlanPublicationLease AcquirePublicationLease()
            => RuntimePlanPublicationLease.CreateUntracked(Current, 0);

        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan next)
        {
            Current = next;
            return new(next, 0, []);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current += amount;
    }
}
