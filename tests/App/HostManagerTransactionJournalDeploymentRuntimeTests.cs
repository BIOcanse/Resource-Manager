using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerTransactionJournalDeploymentRuntimeTests
{
    [Fact]
    public void Runtime_CapturesOnePublishedPlanAndSettlesOnlyTransactionJournal()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = hostPlan
        });
        var runtime = new HostManagerTransactionJournalDeploymentRuntime(
            provider,
            deployment);

        var desired = runtime.CaptureDesired();

        Assert.Same(hostPlan, desired.HostPlan);
        Assert.Equal(
            hostPlan.BuildSpecialize.TransactionJournalAbiVersion,
            desired.AbiVersion);
        Assert.Same(hostPlan.HostRecreate.TransactionJournal, desired.Recreate);
        Assert.Same(hostPlan.HotPublish.TransactionJournal, desired.HotPublish);

        var attempt = runtime.BeginInitialCreate(desired.HostPlan);
        Assert.Equal(HostManagerModuleKind.TransactionJournal, attempt.Module);
        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Applied,
            runtime.CompleteSucceeded(attempt));
        Assert.Equal(
            HostManagerDeploymentStatus.InSync,
            deployment.Snapshot.TransactionJournal.Status);
        Assert.Equal(
            HostManagerDeploymentStatus.Initializing,
            deployment.Snapshot.SmartCoordinator.Status);
    }

    [Fact]
    public void Runtime_RejectsAnUnpublishedHostPlan()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new StaticRuntimePlanProvider(CompiledRuntimePlan.Default);
        var runtime = new HostManagerTransactionJournalDeploymentRuntime(
            provider,
            deployment);

        Assert.Throws<InvalidOperationException>(runtime.CaptureDesired);
    }

    private sealed class StaticRuntimePlanProvider(CompiledRuntimePlan current)
        : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; } = current;

        public RuntimePlanPublicationLease AcquirePublicationLease()
            => RuntimePlanPublicationLease.CreateUntracked(Current, 1);

        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan) =>
            throw new NotSupportedException();
    }
}
