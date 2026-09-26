using System.Reflection;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public async Task PlacementReservationSelectsItsOwnDesiredWithoutLendingCapacity(uint ordinaryBudget)
    {
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions);
        var admission = CreatePlacementReservationAdmission(ordinaryBudget + 1, out var permit);
        Assert.True(permit.TryReserveExactNewPointOfNoReturn(1, out var reservation));
        using var retained = reservation!;
        var original = (await CreateGpuDesired(fixture.Coordinator, AutomaticGpuProcess(), null))!;
        var reserved = original with { Priority = 1, ActionReservation = retained };
        var preference = new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.GpuPreference,
            "preference", new Dictionary<string, string>
            {
                ["owner"] = "host-manager-automatic-placement-v1",
                ["path"] = AutomaticGpuProcess().ExecutablePath!,
                ["hadValue"] = "false", ["previousValue"] = "", ["appliedValue"] = "GpuPreference=1;"
            });
        var higherPriority = new HostManagerPlacementDesired(original.Placement, preference, 100);
        var secondUnreserved = higherPriority with { Record = preference with { RecordId = "other" }, Priority = 50 };
        var selected = BuildGpuBudget(fixture, [higherPriority, secondUnreserved, reserved], 1,
            admission.NewPointOfNoReturnRemaining).Desired;
        Assert.Contains(reserved, selected);
        Assert.DoesNotContain(secondUnreserved, selected);
        Assert.Equal(ordinaryBudget + 1, (uint)selected.Count);
        Assert.Equal(ordinaryBudget != 0, selected.Contains(higherPriority));
        Assert.Equal(ordinaryBudget, admission.NewPointOfNoReturnRemaining);
        Assert.Equal(0, actions.ApplyCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementReservationApplyContinuesTheSameSingleAction(bool observationAlreadyEntered)
    {
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions);
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        var admission = CreatePlacementReservationAdmission(1, out var permit);
        Assert.True(permit.TryReserveExactNewPointOfNoReturn(1, out var reservation));
        using var retained = reservation!;
        if (observationAlreadyEntered) retained.EnterPointOfNoReturn();
        var desired = (await CreateGpuDesired(fixture.Coordinator, AutomaticGpuProcess(), null))!
            with { ActionReservation = retained };
        await ApplyReservedGpuDesired(fixture, desired, permit, CancellationToken.None);
        Assert.Equal(1, actions.ApplyCalls);
        Assert.Equal(desired.RuntimeGpuAction!.PolicyValue, runtime.ReadPolicy(AutomaticGpuProcess().TargetId));
        Assert.Equal(0u, admission.NewPointOfNoReturnRemaining);
        // Apply must not settle the outer operation's reservation.
        var error = Assert.Throws<InvalidDataException>(() => retained.EnterPointOfNoReturn());
        Assert.Contains("more points", error.Message);
        retained.Dispose();
        Assert.Equal(0u, admission.NewPointOfNoReturnRemaining);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("disposed")]
    [InlineData("recovery")]
    [InlineData("multiple")]
    [InlineData("wrong-permit")]
    public async Task PlacementReservationRejectsInvalidBudgetBeforeSavingOrWriting(string kind)
    {
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions);
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        var admission = CreatePlacementReservationAdmission(2, out var permit);
        HostManagerCycleEffectReservation? reservation;
        if (kind == "foreign")
        {
            _ = CreatePlacementReservationAdmission(1, out var foreign);
            Assert.True(foreign.TryReserveExactNewPointOfNoReturn(1, out reservation));
        }
        else if (kind == "recovery")
            Assert.True(permit.TryReserveOwnedNativeRestore(out reservation));
        else
            Assert.True(permit.TryReserveExactNewPointOfNoReturn(kind == "multiple" ? 2u : 1u, out reservation));
        using var retained = reservation!;
        if (kind == "disposed") retained.Dispose();
        if (kind == "wrong-permit")
            Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.AutomaticMemoryCleanup, out permit));
        var desired = (await CreateGpuDesired(fixture.Coordinator, AutomaticGpuProcess(), null))!
            with { ActionReservation = retained };
        var before = admission.CaptureBudgetSnapshot();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApplyReservedGpuDesired(fixture, desired, permit, CancellationToken.None));
        Assert.Equal(before, admission.CaptureBudgetSnapshot());
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        Assert.Null(runtime.ReadPolicy(AutomaticGpuProcess().TargetId));
        Assert.Equal(0, actions.ApplyCalls);
    }

    [Theory]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "baseline")]
    [InlineData(true, "baseline")]
    [InlineData(false, "checkpoint")]
    [InlineData(true, "checkpoint")]
    public async Task PlacementReservationFailureRefundsOnlyActionsNotAlreadyEntered(bool observationAlreadyEntered, string failure)
    {
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions,
            failure == "checkpoint" ? new FailingGpuCheckpointStore() : null);
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        var admission = CreatePlacementReservationAdmission(1, out var permit);
        Assert.True(permit.TryReserveExactNewPointOfNoReturn(1, out var reservation));
        using var retained = reservation!;
        if (observationAlreadyEntered) permit.EnterSingleNewAction(retained);
        var desired = (await CreateGpuDesired(fixture.Coordinator, AutomaticGpuProcess(), null))!
            with { ActionReservation = retained };
        if (failure == "baseline")
            Assert.True(runtime.TryWritePolicy(AutomaticGpuProcess().TargetId, null, [99]));
        if (failure == "checkpoint")
            await Assert.ThrowsAsync<IOException>(() => ApplyReservedGpuDesired(fixture, desired, permit, CancellationToken.None));
        else
            await ApplyReservedGpuDesired(fixture, desired, permit, new CancellationToken(failure == "cancel"));
        Assert.Equal(0, actions.ApplyCalls);
        Assert.Equal(failure == "baseline" ? new byte[] { 99 } : null,
            runtime.ReadPolicy(AutomaticGpuProcess().TargetId));
        permit.RequireSingleNewActionReservation(retained);
        Assert.Equal(0u, admission.NewPointOfNoReturnRemaining);
        retained.Dispose();
        Assert.Equal(observationAlreadyEntered ? 0u : 1u, admission.NewPointOfNoReturnRemaining);
        Assert.Throws<InvalidOperationException>(() => permit.EnterSingleNewAction(retained));
    }

    [Fact]
    public void PlacementReservationSingleActionEntryDoesNotChangeTheBatchAccountingContract()
    {
        var admission = CreatePlacementReservationAdmission(2, out var permit);
        Assert.True(permit.TryReserveExactNewPointOfNoReturn(1, out var reservation));
        using (reservation)
        {
            permit.RequireSingleNewActionReservation(reservation!);
            Parallel.For(0, 32, _ => permit.EnterSingleNewAction(reservation!));
            Assert.Throws<InvalidDataException>(() => reservation!.EnterPointOfNoReturn());
        }
        Assert.Equal(1u, admission.NewPointOfNoReturnRemaining);
        Assert.True(permit.TryReserveExactNewPointOfNoReturn(1, out var unused));
        unused!.Dispose();
        Assert.Throws<InvalidOperationException>(() => permit.EnterSingleNewAction(unused));
        Assert.Equal(1u, admission.NewPointOfNoReturnRemaining);
    }

    private static HostManagerCycleEffectAdmission CreatePlacementReservationAdmission(
        uint capacity, out HostManagerCycleEffectPermit permit)
    {
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(capacity, 1);
        Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.NativeActionTransaction, out permit));
        return admission;
    }

    private static async Task ApplyReservedGpuDesired(ScoreOnlyCoordinatorFixture fixture,
        HostManagerPlacementDesired desired, HostManagerCycleEffectPermit permit, CancellationToken token)
    {
        var inputs = new NativePlacementDesiredInput[1];
        var projected = HostManagerPlacementCoordinatorProjection.ProjectDesired([desired], inputs).Records.Single();
        Assert.Same(desired.ActionReservation, projected.ActionReservation);
        var action = new NativePlacementAction
        {
            TargetKey = projected.Identity.TargetKey, RecordKey = projected.Identity.RecordKey,
            DeadlineMilliseconds = checked((ulong)Environment.TickCount64
                + (ulong)fixture.RuntimePlan.HostManager.HotPublish.PlacementCoordinator.ActionTimeoutMilliseconds)
        };
        var task = (Task)typeof(HostManagerSmartCoordinator)
            .GetMethod("ApplyAutomaticPlacementAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [permit, fixture.StateStore.Current, action, projected, token])!;
        await task;
    }
}
