using System.Buffers;
using System.Runtime.CompilerServices;
using ResourceManager.Subscription.Native;

namespace ResourceManager.Subscription;

// Internal storage only. Public consumers retain the original ordinary arrays.
// Complete publishes newly allocated scratch; a failed projection leaves it unpublished.
internal interface INativeSamplingOutputScratch : IDisposable
{
    ulong Length { get; }
    NativeSamplingOutputScratchLease Acquire();

    // Called once right after creation with the user and capacity/live rows that
    // /api/debug/native-memory reports. Storage without such names ignores it.
    void Describe(string workArea, string rows) { }
}

internal abstract class NativeSamplingOutputScratchLease : IDisposable
{
    internal abstract ulong Length { get; }
    internal abstract MemoryHandle Pin();
    internal abstract void Complete();
    public abstract void Dispose();
}

internal readonly struct NativeSamplingOutputLayout
{
    internal const uint Alignment = 8;
    internal int DueCount { get; }
    internal int SourceCount { get; }
    internal int ExpiredCount { get; }
    internal int TriggerCount { get; }
    internal ulong SourceOffset { get; }
    internal ulong ExpiredOffset { get; }
    internal ulong TriggerOffset { get; }
    internal ulong Length { get; }
    internal ulong StorageLength => checked(Length + Alignment - 1);

    internal NativeSamplingOutputLayout(in NativeSamplingSubscriptionCapacity capacity)
    {
        DueCount = checked((int)capacity.DueItemCapacity);
        SourceCount = checked((int)capacity.SourceViewCapacity);
        ExpiredCount = checked((int)capacity.ExpiredSourceCapacity);
        TriggerCount = checked((int)capacity.TriggerCapacity);
        SourceOffset = checked((ulong)DueCount * (ulong)Unsafe.SizeOf<NativeSamplingSubscriptionDueItem>());
        ExpiredOffset = checked(SourceOffset + (ulong)SourceCount * (ulong)Unsafe.SizeOf<NativeSamplingSubscriptionSourceView>());
        TriggerOffset = checked(ExpiredOffset + (ulong)ExpiredCount * (ulong)Unsafe.SizeOf<NativeSamplingSubscriptionExpiredSource>());
        Length = checked(TriggerOffset + (ulong)TriggerCount * (ulong)Unsafe.SizeOf<NativeSamplingSubscriptionTrigger>());
    }
}

internal unsafe ref struct NativeSamplingOutputView
{
    private MemoryHandle pin;
    internal Span<NativeSamplingSubscriptionDueItem> DueItems { get; private set; }
    internal Span<NativeSamplingSubscriptionSourceView> Sources { get; private set; }
    internal Span<NativeSamplingSubscriptionExpiredSource> Expired { get; private set; }
    internal Span<NativeSamplingSubscriptionTrigger> Triggers { get; private set; }

    internal NativeSamplingOutputView(NativeSamplingSubscriptionDueItem[] due,
        NativeSamplingSubscriptionSourceView[] sources, NativeSamplingSubscriptionExpiredSource[] expired,
        NativeSamplingSubscriptionTrigger[] triggers)
    {
        this = default;
        DueItems = due;
        Sources = sources;
        Expired = expired;
        Triggers = triggers;
    }

    internal NativeSamplingOutputView(NativeSamplingOutputScratchLease lease, scoped in NativeSamplingOutputLayout layout)
    {
        this = default;
        if (lease.Length < layout.StorageLength)
            throw new InvalidOperationException("The sampling output allocation is smaller than its layout.");
        pin = lease.Pin();
        try
        {
            if (pin.Pointer == null) throw new InvalidOperationException("The sampling output allocation has no address.");
            var first = (nuint)pin.Pointer;
            var address = checked(first + NativeSamplingOutputLayout.Alignment - 1)
                & ~(nuint)(NativeSamplingOutputLayout.Alignment - 1);
            var skip = checked((ulong)(address - first));
            if (skip > lease.Length || layout.Length > lease.Length - skip)
                throw new InvalidOperationException("The aligned sampling output layout exceeds its allocation.");
            _ = checked(address + checked((nuint)layout.Length));
            DueItems = new Span<NativeSamplingSubscriptionDueItem>((void*)address, layout.DueCount);
            Sources = new Span<NativeSamplingSubscriptionSourceView>(
                (void*)checked(address + checked((nuint)layout.SourceOffset)), layout.SourceCount);
            Expired = new Span<NativeSamplingSubscriptionExpiredSource>(
                (void*)checked(address + checked((nuint)layout.ExpiredOffset)), layout.ExpiredCount);
            Triggers = new Span<NativeSamplingSubscriptionTrigger>(
                (void*)checked(address + checked((nuint)layout.TriggerOffset)), layout.TriggerCount);
        }
        catch
        {
            pin.Dispose();
            this = default;
            throw;
        }
    }

    public void Dispose()
    {
        pin.Dispose();
        this = default;
    }
}
