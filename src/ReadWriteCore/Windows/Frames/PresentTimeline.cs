namespace ResourceManager.ReadWrite.Windows.Frames;

/// <summary>
/// The frame ledger's bounds, compiled from the frame_pipeline freedom point. PresentsPerStream is
/// sized by the declaration as present-rate capacity (Hz) x retention, so one stream keeps a whole
/// retention window up to that rate; a faster stream overwrites its oldest Presents and the window
/// reports <see cref="FrameTimingReasons.PresentRateCapacity"/>.
/// </summary>
public sealed record PresentFrameLedgerLimits(TimeSpan Retention, int PresentsPerStream, int MaximumStreams)
{
    public PresentFrameLedgerLimits Validate()
    {
        if (Retention <= TimeSpan.Zero || PresentsPerStream < 2 || MaximumStreams < 1)
            throw new ArgumentOutOfRangeException(nameof(PresentFrameLedgerLimits),
                "The frame ledger needs a positive retention, at least two Presents per stream and one stream.");
        return this;
    }
}

/// <summary>Per-Present GPU frame measurement in UTC ticks, written when every packet of the frame completed.</summary>
internal readonly record struct PresentGpuRecord(long GpuStartTicks, long ReadyTicks, long BusyTicks, long? VideoBusyTicks);

/// <summary>One retained Present: start and stop UTC ticks (stop 0 until observed) and its ring sequence.</summary>
internal readonly record struct PresentRecord(long Sequence, long StartTicks, long StopTicks, PresentGpuRecord? Gpu);

/// <summary>
/// Bounded ring of one stream's Presents in arrival order. Sequences grow by one per Present, so a
/// Present stop or a GPU frame can find its record while the record is still retained.
/// </summary>
internal sealed class PresentTimeline(int capacity)
{
    // Grows by doubling up to the declared capacity; only Presents actually seen take memory.
    private long[] starts = [];
    private long[] stops = [];
    private PresentGpuRecord?[]? gpu;
    private int head;
    private long firstSequence;

    public int Count { get; private set; }
    public int Capacity => capacity;

    /// <summary>Appends a Present. When full the oldest is overwritten and its start is returned in <paramref name="overwrittenStart"/>.</summary>
    public long Append(long startTicks, out long? overwrittenStart)
    {
        overwrittenStart = null;
        if (Count == capacity)
        {
            overwrittenStart = starts[head];
            RemoveFirst();
        }
        else if (Count == starts.Length)
        {
            Grow();
        }

        var slot = (head + Count) % starts.Length;
        starts[slot] = startTicks;
        stops[slot] = 0;
        if (gpu is not null) gpu[slot] = null;
        Count++;
        return firstSequence + Count - 1;
    }

    public long FirstStart => starts[head];
    public long? SecondStart => Count > 1 ? starts[(head + 1) % starts.Length] : null;

    public void RemoveFirst()
    {
        if (gpu is not null) gpu[head] = null;
        head = (head + 1) % starts.Length;
        firstSequence++;
        Count--;
    }

    public bool TrySetStop(long sequence, long stopTicks)
    {
        if (!TrySlot(sequence, out var slot) || stopTicks < starts[slot]) return false;
        stops[slot] = stopTicks;
        return true;
    }

    public bool TrySetGpu(long sequence, PresentGpuRecord record)
    {
        if (!TrySlot(sequence, out var slot)) return false;
        gpu ??= new PresentGpuRecord?[starts.Length];
        gpu[slot] = record;
        return true;
    }

    public bool TryGetStart(long sequence, out long startTicks)
    {
        startTicks = 0;
        if (!TrySlot(sequence, out var slot)) return false;
        startTicks = starts[slot];
        return true;
    }

    /// <summary>Retained Presents whose start lies in [from, through], ordered by start then sequence.</summary>
    public PresentRecord[] Snapshot(long fromTicks, long throughTicks)
    {
        var result = new List<PresentRecord>(Count);
        for (var index = 0; index < Count; index++)
        {
            var slot = (head + index) % starts.Length;
            if (starts[slot] < fromTicks || starts[slot] > throughTicks) continue;
            result.Add(new PresentRecord(firstSequence + index, starts[slot], stops[slot], gpu?[slot]));
        }

        // Real-time sessions deliver per CPU buffer; a render thread that changed cores can arrive out of order.
        result.Sort(static (left, right) => left.StartTicks != right.StartTicks
            ? left.StartTicks.CompareTo(right.StartTicks)
            : left.Sequence.CompareTo(right.Sequence));
        return [.. result];
    }

    public long MaxStart()
    {
        var max = long.MinValue;
        for (var index = 0; index < Count; index++) max = Math.Max(max, starts[(head + index) % starts.Length]);
        return max;
    }

    /// <summary>
    /// Appends every record of <paramref name="other"/> in its arrival order. Returns the latest start whose
    /// predecessor was overwritten here (its interval is lost), or null when nothing was overwritten.
    /// </summary>
    public long? AppendFrom(PresentTimeline other)
    {
        long? lastOverwritten = null;
        for (var index = 0; index < other.Count; index++)
        {
            var slot = (other.head + index) % other.starts.Length;
            var sequence = Append(other.starts[slot], out var overwritten);
            if (overwritten is not null) lastOverwritten = Math.Max(lastOverwritten ?? long.MinValue, FirstStart);
            if (other.stops[slot] != 0) TrySetStop(sequence, other.stops[slot]);
            if (other.gpu?[slot] is { } record) TrySetGpu(sequence, record);
        }

        return lastOverwritten;
    }

    private bool TrySlot(long sequence, out int slot)
    {
        slot = 0;
        var offset = sequence - firstSequence;
        if (offset < 0 || offset >= Count) return false;
        slot = (int)((head + offset) % starts.Length);
        return true;
    }

    private void Grow()
    {
        var length = Math.Min(capacity, Math.Max(1, starts.Length * 2));
        var newStarts = new long[length];
        var newStops = new long[length];
        var newGpu = gpu is null ? null : new PresentGpuRecord?[length];
        for (var index = 0; index < Count; index++)
        {
            var slot = (head + index) % starts.Length;
            newStarts[index] = starts[slot];
            newStops[index] = stops[slot];
            if (newGpu is not null) newGpu[index] = gpu![slot];
        }

        starts = newStarts;
        stops = newStops;
        gpu = newGpu;
        head = 0;
    }
}
