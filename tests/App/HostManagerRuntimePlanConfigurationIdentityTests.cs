using ResourceManager.App.Infrastructure.NativeCore;
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
            NativeAppliedOwnershipAbi.Version,
            firstHostPlan.HostRecreate.AppliedOwnership,
            firstHostPlan.HotPublish.AppliedOwnership);
        var second = new HostManagerAppliedOwnershipRuntimePlan(
            secondHostPlan,
            NativeAppliedOwnershipAbi.Version,
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
            NativeTransactionJournalAbi.Version,
            firstHostPlan.HostRecreate.TransactionJournal,
            firstHostPlan.HotPublish.TransactionJournal);
        var second = new HostManagerTransactionJournalRuntimePlan(
            secondHostPlan,
            NativeTransactionJournalAbi.Version,
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
