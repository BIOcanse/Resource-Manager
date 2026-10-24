using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    // A recompiled plan with the same content holds new immutable arrays; the owner must
    // compare the module digests, not the plan objects, or every publication recreates it.
    [Fact]
    public async Task RecompiledIdenticalPlanKeepsTheWorkspaceAndAChangedRuleReplacesIt()
    {
        await using var owner = ScratchOwner(new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            OperationScratchFixture.Create, new CachedResourceSource(), out var provider);
        await owner.StartAsync(CancellationToken.None);
        await owner.StopAsync(CancellationToken.None);
        var first = ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")!;

        var recompiled = HostManagerTestPlanFactory.CreatePlan();
        Assert.NotSame(provider.Current.HostManager.ReportCoordinator.Recreate.Rules[0],
            recompiled.ReportCoordinator.Recreate.Rules[0]);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = recompiled });
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Same(first, ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));

        var changed = HostManagerTestPlanFactory.CreatePlan(reports: new AppReportSettings(
            [new AppReportRuleOverride("cpu_throttle_temperature", ActivationThreshold: 95)]));
        provider.Publish(CompiledRuntimePlan.Default with { Version = 3, HostManager = changed });
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.NotSame(first, ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace"));
        Assert.Equal(95, ReportOwnerField<CompiledHostManagerReportCoordinatorPlan>(owner, "appliedPlan")!
            .Recreate.Rules[0].ActivationThreshold);
    }
}
