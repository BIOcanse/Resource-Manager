using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

// A smart round opens three regions in call order, each sized from that round's
// actual rows: facts before the native measure, the plan after native reports its
// staging columns and candidate rows, feedback after the plan reports its actions.
internal sealed class NativeSmartFactsLayout
{
    internal int InputCapacity { get; }
    internal int MeasureTableCount { get; }
    internal ulong Inputs { get; }
    internal ulong MeasureTable { get; }
    internal ulong Length { get; }

    internal NativeSmartFactsLayout(uint inputs, ulong measureTableCount)
    {
        ArgumentOutOfRangeException.ThrowIfZero(inputs);
        ArgumentOutOfRangeException.ThrowIfZero(measureTableCount);
        InputCapacity = checked((int)inputs);
        MeasureTableCount = checked((int)measureTableCount);
        ulong end = 0;
        Inputs = NativeSmartColumns.Next(ref end, Unsafe.SizeOf<NativeSmartCoordinatorInputRow>(), InputCapacity);
        MeasureTable = NativeSmartColumns.Next(ref end, sizeof(uint), MeasureTableCount);
        Length = NativeSmartColumns.Round(end);
    }
}

internal sealed class NativeSmartPlanLayout
{
    internal int ActionCapacity { get; }
    internal ulong Actions { get; }
    internal ulong NativeScratch { get; }
    internal ulong NativeScratchByteCount { get; }
    internal uint NativeScratchAlignment { get; }
    internal ulong Length { get; }

    // Zero action rows is representable so native, not the layout, reports a
    // zero action capacity as its own invalid argument.
    internal NativeSmartPlanLayout(uint actions, ulong nativeScratchByteCount, uint nativeScratchAlignment)
    {
        ArgumentOutOfRangeException.ThrowIfZero(nativeScratchByteCount);
        if (nativeScratchAlignment == 0 || (nativeScratchAlignment & (nativeScratchAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(nativeScratchAlignment));
        ActionCapacity = checked((int)actions);
        ulong end = 0;
        Actions = NativeSmartColumns.Next(ref end, Unsafe.SizeOf<NativeSmartCoordinatorAction>(), ActionCapacity);
        NativeScratch = end;
        NativeScratchByteCount = nativeScratchByteCount;
        NativeScratchAlignment = nativeScratchAlignment;
        end = checked(end + nativeScratchAlignment - 1 + nativeScratchByteCount);
        Length = NativeSmartColumns.Round(end);
    }
}

internal sealed class NativeSmartFeedbackLayout
{
    internal int FeedbackCapacity { get; }
    internal ulong Feedback { get; }
    internal ulong Length { get; }

    internal NativeSmartFeedbackLayout(uint feedback)
    {
        ArgumentOutOfRangeException.ThrowIfZero(feedback);
        FeedbackCapacity = checked((int)feedback);
        ulong end = 0;
        Feedback = NativeSmartColumns.Next(ref end, Unsafe.SizeOf<NativeSmartCoordinatorFeedback>(), FeedbackCapacity);
        Length = NativeSmartColumns.Round(end);
    }
}

internal static class NativeSmartColumns
{
    internal static ulong Next(ref ulong end, int width, int count)
    {
        var start = Round(end);
        end = checked(start + (ulong)width * (ulong)count);
        return start;
    }

    internal static ulong Round(ulong end) => checked((end + 7) / 8 * 8);
}

// The caller owns the operation leases. This view pins only one synchronous use;
// a region the round has not opened has no rows, as a plan without actions has none.
internal ref struct NativeSmartScratchView
{
    private ComputeScratchView facts;
    private ComputeScratchView plan;
    private ComputeScratchView feedback;
    private readonly NativeSmartFactsLayout factsLayout;
    private readonly NativeSmartPlanLayout? planLayout;
    private readonly NativeSmartFeedbackLayout? feedbackLayout;

    internal NativeSmartScratchView(
        ComputeScratchLease factsLease, NativeSmartFactsLayout factsLayout,
        ComputeScratchLease? planLease, NativeSmartPlanLayout? planLayout,
        ComputeScratchLease? feedbackLease, NativeSmartFeedbackLayout? feedbackLayout)
    {
        ArgumentNullException.ThrowIfNull(factsLayout);
        Require(factsLease.Length, factsLayout.Length);
        if ((planLease is null) != (planLayout is null) || (feedbackLease is null) != (feedbackLayout is null))
            throw new InvalidOperationException("A smart scratch region is open without its layout.");
        if (planLease is not null) Require(planLease.Length, planLayout!.Length);
        if (feedbackLease is not null) Require(feedbackLease.Length, feedbackLayout!.Length);
        this.factsLayout = factsLayout;
        this.planLayout = planLayout;
        this.feedbackLayout = feedbackLayout;
        facts = new ComputeScratchView(factsLease);
        try
        {
            if (planLease is not null) plan = new ComputeScratchView(planLease);
            if (feedbackLease is not null) feedback = new ComputeScratchView(feedbackLease);
        }
        catch
        {
            plan.Dispose();
            facts.Dispose();
            throw;
        }
    }

    private static void Require(ulong backing, ulong layout)
    {
        if (backing != layout)
            throw new InvalidOperationException("The smart scratch layout does not match its backing region.");
    }

    internal Span<NativeSmartCoordinatorInputRow> Inputs
        => facts.Column<NativeSmartCoordinatorInputRow>(factsLayout.Inputs, factsLayout.InputCapacity);

    internal Span<uint> MeasureTable
        => facts.Column<uint>(factsLayout.MeasureTable, factsLayout.MeasureTableCount);

    internal Span<NativeSmartCoordinatorAction> Actions
        => planLayout is { } layout
            ? plan.Column<NativeSmartCoordinatorAction>(layout.Actions, layout.ActionCapacity)
            : Span<NativeSmartCoordinatorAction>.Empty;

    internal Span<NativeSmartCoordinatorFeedback> Feedback
        => feedbackLayout is { } layout
            ? feedback.Column<NativeSmartCoordinatorFeedback>(layout.Feedback, layout.FeedbackCapacity)
            : Span<NativeSmartCoordinatorFeedback>.Empty;

    internal IntPtr NativeScratchPointer
    {
        get
        {
            var layout = planLayout ?? throw new InvalidOperationException("Smart staging exists only during a plan.");
            return plan.AlignedRegion(layout.NativeScratch, layout.NativeScratchByteCount, layout.NativeScratchAlignment);
        }
    }

    public void Dispose()
    {
        feedback.Dispose();
        plan.Dispose();
        facts.Dispose();
    }
}
