using ResourceManager.App.Infrastructure.NativeCore;
using System.Runtime.InteropServices;

namespace Resource_Manager_APP.Tests;

internal static class NativeSmartCoordinatorTestPlanning
{
    internal static NativeSmartCoordinatorStatus PlanCurrent(
        this NativeSmartCoordinatorWorkspace workspace,
        in NativeSmartCoordinatorCycleInput input)
        => workspace.Plan(in input);

    internal static unsafe NativeSmartCoordinatorStatus PlanCurrent(
        this NativeSmartCoordinatorSession session,
        in NativeSmartCoordinatorCycleInput input,
        ReadOnlySpan<NativeSmartCoordinatorInputRow> rows,
        Span<NativeSmartCoordinatorAction> actions,
        ref NativeSmartCoordinatorSnapshot snapshot)
    {
        var requirements = session.ScratchRequirements;
        var memory = NativeMemory.AlignedAlloc(checked((nuint)requirements.ByteCount), requirements.Alignment);
        if (memory == null) throw new OutOfMemoryException();
        try { return session.Plan(in input, rows, actions, (IntPtr)memory, requirements.ByteCount, ref snapshot); }
        finally { NativeMemory.AlignedFree(memory); }
    }
}

// Test evidence is copied synchronously from the actual leased production projection.
internal sealed class NativeSmartInputCaptureProbe(
    ResourceManager.App.Infrastructure.Optimization.IHostManagerSmartCoordinatorTransitionProbe? inner = null)
    : ResourceManager.App.Infrastructure.Optimization.IHostManagerSmartCoordinatorTransitionProbe
{
    internal NativeSmartCoordinatorInputRow[] Rows { get; private set; } = [];
    public void ObserveNativeInputs(ReadOnlySpan<NativeSmartCoordinatorInputRow> inputs)
    {
        Rows = inputs.ToArray();
        inner?.ObserveNativeInputs(inputs);
    }
    public void Reach(ResourceManager.App.Infrastructure.Optimization.HostManagerSmartCoordinatorTransitionPoint point)
        => inner?.Reach(point);
    public void ObserveShutdown(ResourceManager.App.Infrastructure.Optimization.HostManagerSmartCoordinatorShutdownPoint point)
        => inner?.ObserveShutdown(point);
}
