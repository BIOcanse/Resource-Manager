using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;

namespace ResourceManager.App.Infrastructure.KernelLeak;

// Owned small rings; maximum holders * capacity is validated before allocating.
internal sealed class KernelLeakSeries(KernelLeakOptions options)
{
    private readonly Dictionary<(int Pid, string Start, KernelLeakSampleKind Kind), Ring> holders = new();
    internal int Count => holders.Count;
    internal long Bytes => (long)Count * options.SeriesCapacity * 16;
    internal long PeakBytes { get; private set; }
    internal void Clear() => holders.Clear();
    internal IReadOnlyList<KernelLeakSuspect> Observe(KernelLeakSampleFrame frame)
    {
        if (frame.Samples.Count > options.MaximumHolders) throw new OverflowException("Kernel leak holder capacity exceeded.");
        var live = new HashSet<(int, string, KernelLeakSampleKind)>();
        foreach (var sample in frame.Samples)
        {
            // Only process handle counts are eligible. Reclaimable cache measures never enter the rings.
            if (sample.ResourceKind != KernelLeakSampleKind.ProcessHandles) continue;
            if (sample.ProcessId <= 0 || !ulong.TryParse(sample.StartKey, out var birth) || birth == 0
                || sample.StartKey.Length > 20 || !live.Add((sample.ProcessId, sample.StartKey, sample.ResourceKind)))
                throw new InvalidDataException("Invalid or repeated kernel leak holder identity.");
        }
        foreach (var key in holders.Keys.ToArray()) if (!live.Contains(key)) holders.Remove(key);
        var suspects = new List<KernelLeakSuspect>();
        foreach (var sample in frame.Samples)
        {
            if (sample.ResourceKind != KernelLeakSampleKind.ProcessHandles) continue;
            var key = (sample.ProcessId, sample.StartKey, sample.ResourceKind);
            if (!holders.TryGetValue(key, out var ring))
            {
                if (holders.Count == options.MaximumHolders) throw new OverflowException("Kernel leak holder capacity exceeded.");
                holders.Add(key, ring = new(options.SeriesCapacity));
            }
            ring.Add(frame.CapturedAt.UtcTicks, sample.Value);
            if (ring.Count < options.MinimumSamples || sample.Value <= (ulong)options.HandleFloor) continue;
            var first = ring.At(0);
            var last = ring.At(ring.Count - 1);
            var seconds = (last.Ticks - first.Ticks) / (double)TimeSpan.TicksPerSecond;
            if (seconds < options.MinimumObservationSeconds) continue;
            // Compare conservative edge levels, allowing ordinary-handle noise between them.
            // Integer division floors the percentage; disjoint edges always contain at least one sample.
            var edgeCount = Math.Max(1, ring.Count * options.EdgePercentage / 100);
            var earlyMaximum = first.Value;
            var lateMinimum = last.Value;
            for (var index = 0; index < edgeCount; index++)
            {
                earlyMaximum = Math.Max(earlyMaximum, ring.At(index).Value);
                lateMinimum = Math.Min(lateMinimum, ring.At(ring.Count - 1 - index).Value);
            }
            if (lateMinimum < earlyMaximum || lateMinimum - earlyMaximum < (ulong)options.MinimumGrowth) continue;
            // Published growth/rate retain their first-to-last definition, distinct from the edge veto.
            suspects.Add(new(KernelLeakResourceKind.ProcessHandles, last.Value,
                (last.Value - first.Value) / seconds, seconds, ring.Count, last.Value - first.Value,
                KernelLeakHolder.Unknown(sample.ProcessId, sample.StartKey)));
        }
        PeakBytes = Math.Max(PeakBytes, Bytes);
        return suspects.ToArray();
    }
    private sealed class Ring(int capacity)
    {
        private readonly (long Ticks, ulong Value)[] samples = new (long, ulong)[capacity];
        private int next;
        internal int Count { get; private set; }
        internal (long Ticks, ulong Value) At(int index) => samples[(next - Count + index + capacity) % capacity];
        internal void Add(long ticks, ulong value)
        {
            if (Count > 0 && ticks <= At(Count - 1).Ticks) throw new InvalidDataException("Observation time must increase.");
            samples[next] = (ticks, value); next = (next + 1) % capacity; Count = Math.Min(Count + 1, capacity);
        }
    }
}