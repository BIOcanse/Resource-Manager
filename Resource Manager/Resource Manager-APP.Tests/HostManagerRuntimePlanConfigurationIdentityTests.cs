using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerRuntimePlanConfigurationIdentityTests
{
    [Fact]
    public void AppliedOwnership_IgnoresUnrelatedHostPlanIdentity()
    {
        var firstHostPlan = HostManagerTestPlanFactory.CreatePlan();
        var secondHostPlan = firstHostPlan with
        {
            PlanEpoch = firstHostPlan.PlanEpoch + 1
        };
        var first = new HostManagerAppliedOwnershipRuntimePlan(
            firstHostPlan,
            firstHostPlan.BuildSpecialize.AppliedOwnershipAbiVersion,
            firstHostPlan.HostRecreate.AppliedOwnership,
            firstHostPlan.HotPublish.AppliedOwnership);
        var second = new HostManagerAppliedOwnershipRuntimePlan(
            secondHostPlan,
            secondHostPlan.BuildSpecialize.AppliedOwnershipAbiVersion,
            secondHostPlan.HostRecreate.AppliedOwnership,
            secondHostPlan.HotPublish.AppliedOwnership);

        Assert.NotEqual(first, second);
        Assert.True(first.HasSameModuleConfiguration(second));
        Assert.False(first.HasSameModuleConfiguration(first with
        {
            AbiVersion = first.AbiVersion + 1
        }));
    }

    [Fact]
    public void TransactionJournal_IgnoresUnrelatedHostPlanIdentity()
    {
        var firstHostPlan = HostManagerTestPlanFactory.CreatePlan();
        var secondHostPlan = firstHostPlan with
        {
            PlanEpoch = firstHostPlan.PlanEpoch + 1
        };
        var first = new HostManagerTransactionJournalRuntimePlan(
            firstHostPlan,
            firstHostPlan.BuildSpecialize.TransactionJournalAbiVersion,
            firstHostPlan.HostRecreate.TransactionJournal,
            firstHostPlan.HotPublish.TransactionJournal);
        var second = new HostManagerTransactionJournalRuntimePlan(
            secondHostPlan,
            secondHostPlan.BuildSpecialize.TransactionJournalAbiVersion,
            secondHostPlan.HostRecreate.TransactionJournal,
            secondHostPlan.HotPublish.TransactionJournal);

        Assert.NotEqual(first, second);
        Assert.True(first.HasSameModuleConfiguration(second));
        Assert.False(first.HasSameModuleConfiguration(first with
        {
            AbiVersion = first.AbiVersion + 1
        }));
    }
}
