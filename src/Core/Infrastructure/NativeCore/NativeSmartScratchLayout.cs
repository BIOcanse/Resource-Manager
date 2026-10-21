using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeSmartScratchLayout
{
    internal int InputCapacity { get; }
    internal int ActionCapacity { get; }
    internal int FeedbackCapacity { get; }
    internal ulong Inputs { get; }
    internal ulong Actions { get; }
    internal ulong Feedback { get; }
    internal ulong Length { get; }

    internal NativeSmartScratchLayout(uint inputs, uint actions, uint feedback)
    {
        ArgumentOutOfRangeException.ThrowIfZero(inputs);
        ArgumentOutOfRangeException.ThrowIfZero(actions);
        ArgumentOutOfRangeException.ThrowIfZero(feedback);
        InputCapacity = checked((int)inputs);
        ActionCapacity = checked((int)actions);
        FeedbackCapacity = checked((int)feedback);
        ulong end = 0;
        ulong Column(int width, int count)
        {
            var start = checked((end + 7) / 8 * 8);
            end = checked(start + (ulong)width * (ulong)count);
            return start;
        }
        Inputs = Column(Unsafe.SizeOf<NativeSmartCoordinatorInputRow>(), InputCapacity);
        Actions = Column(Unsafe.SizeOf<NativeSmartCoordinatorAction>(), ActionCapacity);
        Feedback = Column(Unsafe.SizeOf<NativeSmartCoordinatorFeedback>(), FeedbackCapacity);
        Length = checked((end + 7) / 8 * 8);
    }
}

// The caller owns the operation lease. This view pins only one synchronous use.
internal ref struct NativeSmartScratchView
{
    private ComputeScratchView view;
    private readonly NativeSmartScratchLayout layout;

    internal NativeSmartScratchView(ComputeScratchLease lease, NativeSmartScratchLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (lease.Length != layout.Length)
            throw new InvalidOperationException("The smart scratch layout does not match its backing region.");
        this.layout = layout;
        view = new ComputeScratchView(lease);
    }

    internal Span<NativeSmartCoordinatorInputRow> Inputs
        => view.Column<NativeSmartCoordinatorInputRow>(layout.Inputs, layout.InputCapacity);

    internal Span<NativeSmartCoordinatorAction> Actions
        => view.Column<NativeSmartCoordinatorAction>(layout.Actions, layout.ActionCapacity);

    internal Span<NativeSmartCoordinatorFeedback> Feedback
        => view.Column<NativeSmartCoordinatorFeedback>(layout.Feedback, layout.FeedbackCapacity);

    public void Dispose() => view.Dispose();
}
