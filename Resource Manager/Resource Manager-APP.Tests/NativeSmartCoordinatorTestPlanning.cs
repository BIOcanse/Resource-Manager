using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

internal static class NativeSmartCoordinatorTestPlanning
{
    internal static NativeSmartCoordinatorStatus PlanCurrent(
        this NativeSmartCoordinatorWorkspace workspace,
        in NativeSmartCoordinatorCycleInput input)
        => workspace.Plan(in input);

    internal static NativeSmartCoordinatorStatus PlanCurrent(
        this NativeSmartCoordinatorSession session,
        in NativeSmartCoordinatorCycleInput input,
        ReadOnlySpan<NativeSmartCoordinatorInputRow> rows,
        Span<NativeSmartCoordinatorAction> actions,
        ref NativeSmartCoordinatorSnapshot snapshot)
        => session.Plan(in input, rows, actions, ref snapshot);
}
