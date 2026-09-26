using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSamplingSubscriptionOwnerTests
{
    [Fact]
    public async Task FailedInitialStartCanBeRetriedAfterAValidPlanIsPublished()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        using var owner = new HostManagerSamplingSubscriptionOwner(
            provider,
            new HostManagerSamplingSubscriptionRuntime(provider, deployment),
            NullLogger<HostManagerSamplingSubscriptionOwner>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.StartAsync(CancellationToken.None));

        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = HostManagerTestPlanFactory.CreatePlan()
        });

        await owner.StartAsync(CancellationToken.None);
        using var lease = owner.Acquire(roleId: 1);
        Assert.NotNull(lease.Session);
        await owner.StopAsync(CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => owner.Acquire(roleId: 1));
    }

    [Fact]
    public async Task StopRevokesAcquisitionAndRestartCreatesANewIncarnation()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = HostManagerTestPlanFactory.CreatePlan()
        });
        using var owner = new HostManagerSamplingSubscriptionOwner(
            provider,
            new HostManagerSamplingSubscriptionRuntime(provider, deployment),
            NullLogger<HostManagerSamplingSubscriptionOwner>.Instance);

        await owner.StartAsync(CancellationToken.None);
        ulong firstIncarnation;
        SamplingOwnerToken firstToken;
        using (var lease = owner.Acquire(roleId: 1))
        {
            firstIncarnation = lease.WorkspaceIncarnation;
            firstToken = lease.OwnerToken;
        }
        await owner.StopAsync(CancellationToken.None);
        Assert.False(firstToken.IsActive);
        Assert.False(firstToken.TryPublish(
            static () => throw new InvalidOperationException(
                "A revoked owner token must not execute its callback.")));

        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2,
            HostManager = HostManagerTestPlanFactory.CreatePlan()
        });
        Assert.Throws<InvalidOperationException>(() => owner.Acquire(roleId: 1));

        await owner.StartAsync(CancellationToken.None);
        using var restarted = owner.Acquire(roleId: 1);
        Assert.True(restarted.WorkspaceIncarnation > firstIncarnation);
        Assert.True(restarted.OwnerToken.IsActive);
        Assert.NotSame(firstToken, restarted.OwnerToken);
    }

    [Fact]
    public async Task HotReplacementWaitsForAcceptedPublicationThenRevokesOldToken()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = HostManagerTestPlanFactory.CreatePlan()
        });
        using var owner = new HostManagerSamplingSubscriptionOwner(
            provider,
            new HostManagerSamplingSubscriptionRuntime(provider, deployment),
            NullLogger<HostManagerSamplingSubscriptionOwner>.Instance);
        await owner.StartAsync(CancellationToken.None);
        using var retainedLease = owner.Acquire(roleId: 1);
        var oldToken = retainedLease.OwnerToken;
        using var publicationEntered = new ManualResetEventSlim(false);
        using var releasePublication = new ManualResetEventSlim(false);
        using var replacementStarted = new ManualResetEventSlim(false);
        var order = 0;
        var publicationExitOrder = 0;
        var replacementReturnOrder = 0;

        var publication = Task.Run(() => oldToken.TryPublish(() =>
        {
            publicationEntered.Set();
            releasePublication.Wait();
            publicationExitOrder = Interlocked.Increment(ref order);
        }));
        Assert.True(publicationEntered.Wait(TimeSpan.FromSeconds(5)));

        var replacement = Task.Run(() =>
        {
            replacementStarted.Set();
            provider.Publish(CompiledRuntimePlan.Default with
            {
                Version = 2,
                HostManager = HostManagerTestPlanFactory.CreatePlan(root =>
                {
                    root["profile_revision"] = 88;
                    var roles = root["hot_publish"]!["sampling_subscription"]!["roles"]!
                        .AsArray();
                    roles[0]!.AsObject()["default_interval_ms"] = 1100;
                })
            });
            replacementReturnOrder = Interlocked.Increment(ref order);
        });
        Assert.True(replacementStarted.Wait(TimeSpan.FromSeconds(5)));

        releasePublication.Set();
        Assert.True(await publication.WaitAsync(TimeSpan.FromSeconds(5)));
        await replacement.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(publicationExitOrder > 0);
        Assert.True(replacementReturnOrder > publicationExitOrder);
        Assert.False(oldToken.IsActive);
        Assert.False(oldToken.TryPublish(static () => { }));
        using var current = owner.Acquire(roleId: 1);
        Assert.True(current.OwnerToken.IsActive);
        Assert.NotSame(oldToken, current.OwnerToken);
        Assert.True(
            current.WorkspaceIncarnation > retainedLease.WorkspaceIncarnation);
    }
}
