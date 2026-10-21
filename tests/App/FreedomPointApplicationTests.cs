using System.Text.Json;
using FreedomPoint;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

public sealed class FreedomPointApplicationTests
{
    [Fact]
    public void CompletedActualPlansRetainOneResolvedSdkSnapshot()
    {
        var first = Plan(1, 1);
        var candidate = FreedomPointPlanApplication.Prepare(first, Plan(2, 0.5));
        var tree = candidate.HostManager.FreedomPoints;
        Assert.NotNull(tree.SdkFacts);
        Assert.NotNull(tree.SdkRegistry);
        Assert.Equal(tree.SdkContractFingerprint, tree.SdkFacts.ContractFingerprint);
        var preview = Assert.IsType<ConfigurationChange>(candidate.FreedomPointPreview);
        var change = Assert.Single(preview.Points);
        Assert.Equal(candidate.HostManager.CpuScoring!.BaselineRatio, change.After.Slots[0].Decimal);
        Assert.Equal(1D, change.Before.Slots[0].Decimal);
        FreedomPointPlanApplication.ValidatePublication(first, candidate);
        // Internal compilation evidence does not silently widen the HTTP tree.
        var json = JsonSerializer.Serialize(tree);
        Assert.DoesNotContain("SdkFacts", json);
        Assert.DoesNotContain("SdkRegistry", json);
    }

    [Fact]
    public void StaleCandidateIsRejectedWithoutPublishingOrDelivering()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var first = Plan(1, 1);
        provider.Publish(first);
        var stale = FreedomPointPlanApplication.Prepare(first, Plan(3, 0.5));
        var current = Plan(2, 0.75);
        provider.Publish(current);
        var deliveries = 0;
        provider.Published += _ => deliveries++;
        var before = deployment.CaptureRuntimePlanPublication();
        Assert.Equal("stale_configuration_preview", Assert.Throws<FreedomPointException>(() => provider.Publish(stale)).Code);
        Assert.Same(current, provider.Current);
        Assert.Equal(before.PublicationSequence, deployment.CaptureRuntimePlanPublication().PublicationSequence);
        Assert.Equal(0, deliveries);
    }

    [Fact]
    public void CandidateFactsCannotBeChangedAfterPreviewWasCreated()
    {
        var first = Plan(1, 1);
        var candidate = FreedomPointPlanApplication.Prepare(first, Plan(2, 0.5));
        var changed = candidate with { HostManager = Plan(2, 0.75).HostManager };
        Assert.Equal("stale_configuration_preview", Assert.Throws<FreedomPointException>(
            () => FreedomPointPlanApplication.ValidatePublication(first, changed)).Code);
    }

    [Fact]
    public async Task ActualPublicationLeaseProtectsThePreviousConfigurationUntilRelease()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var first = Plan(1, 1);
        provider.Publish(first);
        var candidate = FreedomPointPlanApplication.Prepare(first, Plan(2, 0.5));
        using var old = provider.AcquirePublicationLease();
        using var entered = new ManualResetEventSlim();
        var publishing = Task.Run(() => { entered.Set(); return provider.Publish(candidate); });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(publishing.IsCompleted);
            Assert.Same(first, provider.Current);
            Assert.Equal(1D, old.Plan.HostManager.CpuScoring!.BaselineRatio);
        }
        finally { old.Dispose(); }
        var published = await publishing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(candidate, published.Plan);
        Assert.Equal(0.5, provider.Current.HostManager.CpuScoring!.BaselineRatio);
        Assert.Equal(1D, old.Plan.HostManager.CpuScoring!.BaselineRatio);
    }

    internal static CompiledRuntimePlan Plan(long version, double ratio)
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(1);
        return CompiledRuntimePlan.Default with
        {
            Version = version,
            HostManager = HostManagerTestPlanFactory.CreatePlan(planEpoch: checked((ulong)version),
                cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology, ratio))
        };
    }
}
