using ResourceManager.App.Domain.SystemHealth;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

internal sealed class OracleSystemInterruptWindowAggregator
{
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan BucketDuration = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private readonly Dictionary<long, InterruptSlice> slices = [];

    public void Observe(OracleSystemInterruptEventSample sample)
    {
        var sliceKey = sample.ObservedTickMilliseconds / 1000;
        lock (gate)
        {
            if (!slices.TryGetValue(sliceKey, out var slice))
            {
                slice = new InterruptSlice(sliceKey * 1000);
                slices[sliceKey] = slice;
            }

            slice.Observe(sample);
        }
    }

    public SystemInterruptSnapshot CreateSnapshot(
        DateTimeOffset now,
        long nowTickMilliseconds,
        long sourceGeneration,
        long sessionStartedTickMilliseconds,
        int logicalProcessorCount,
        SystemInterruptProviderState providerState)
    {
        var processorCount = Math.Max(1, logicalProcessorCount);
        var startedAt = Math.Max(sessionStartedTickMilliseconds, nowTickMilliseconds - (long)Window.TotalMilliseconds);
        var window = TimeSpan.FromMilliseconds(Math.Max(0, nowTickMilliseconds - startedAt));

        lock (gate)
        {
            PruneCore(nowTickMilliseconds - (long)Window.TotalMilliseconds - 1000);
            var selected = slices.Values
                .Where(slice => slice.StartedAt >= startedAt && slice.StartedAt <= nowTickMilliseconds)
                .OrderBy(static slice => slice.StartedAt)
                .ToArray();
            var maximum = selected
                .Select(static slice => slice.Maximum)
                .Where(static sample => sample.HasValue)
                .MaxBy(static sample => sample!.Value.DurationMilliseconds);
            var totalDuration = selected.Sum(static slice => slice.TotalDurationMilliseconds);
            var buckets = CreateBuckets(now, nowTickMilliseconds, startedAt, processorCount, selected);
            var available = buckets.Count > 0;

            return new SystemInterruptSnapshot(
                now,
                available,
                sourceGeneration,
                window,
                processorCount,
                totalDuration,
                maximum?.DurationMilliseconds ?? 0,
                maximum?.Kind,
                maximum?.ModuleName,
                selected.Sum(static slice => slice.EventCount),
                selected.Sum(static slice => slice.LongEventCount),
                CapacityPercent(totalDuration, window.TotalMilliseconds, processorCount),
                buckets,
                CreateDrivers(window, processorCount, selected),
                available
                    ? providerState
                    : providerState with
                    {
                        State = SystemInterruptProviderStates.Warming,
                        Message = "ETW 系统中断监控正在收集第一个完整 10 秒桶。"
                    });
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            slices.Clear();
        }
    }

    private void PruneCore(long cutoff)
    {
        foreach (var key in slices
            .Where(pair => pair.Value.StartedAt < cutoff)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            slices.Remove(key);
        }
    }

    private static IReadOnlyList<SystemInterruptBucketSnapshot> CreateBuckets(
        DateTimeOffset now,
        long nowTickMilliseconds,
        long startedAt,
        int logicalProcessorCount,
        IReadOnlyList<InterruptSlice> slices)
    {
        var buckets = new List<SystemInterruptBucketSnapshot>();
        var bucketMilliseconds = (long)BucketDuration.TotalMilliseconds;
        // Anchor complete buckets to observed coverage, never to a wall-clock boundary.
        var bucketEnd = startedAt + (nowTickMilliseconds - startedAt) / bucketMilliseconds * bucketMilliseconds;
        while (bucketEnd - bucketMilliseconds >= startedAt && buckets.Count < 6)
        {
            var bucketStart = bucketEnd - bucketMilliseconds;
            var bucketSlices = slices
                .Where(slice => slice.StartedAt >= bucketStart && slice.StartedAt < bucketEnd)
                .ToArray();
            var totalDuration = bucketSlices.Sum(static slice => slice.TotalDurationMilliseconds);
            buckets.Add(new SystemInterruptBucketSnapshot(
                now.AddMilliseconds(bucketStart - nowTickMilliseconds),
                now.AddMilliseconds(bucketEnd - nowTickMilliseconds),
                totalDuration,
                CapacityPercent(totalDuration, BucketDuration.TotalMilliseconds, logicalProcessorCount),
                bucketSlices.Sum(static slice => slice.EventCount)));
            bucketEnd = bucketStart;
        }

        buckets.Reverse();
        return buckets;
    }

    private static IReadOnlyList<SystemInterruptDriverSnapshot> CreateDrivers(
        TimeSpan window,
        int logicalProcessorCount,
        IReadOnlyList<InterruptSlice> slices)
    {
        var drivers = new Dictionary<string, InterruptDriverAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var driver in slices.SelectMany(static slice => slice.Drivers.Values))
        {
            var key = string.IsNullOrWhiteSpace(driver.ModulePath) ? driver.ModuleName : driver.ModulePath;
            if (!drivers.TryGetValue(key, out var aggregate))
            {
                aggregate = new InterruptDriverAccumulator(driver.ModuleName, driver.ModulePath);
                drivers[key] = aggregate;
            }

            aggregate.Add(driver);
        }

        return drivers.Values
            .Select(driver => new SystemInterruptDriverSnapshot(
                driver.ModuleName,
                driver.ModulePath,
                driver.TotalDurationMilliseconds,
                driver.MaximumSingleDurationMilliseconds,
                driver.EventCount,
                driver.LongEventCount,
                CapacityPercent(driver.TotalDurationMilliseconds, window.TotalMilliseconds, logicalProcessorCount)))
            .OrderByDescending(static item => item.TotalDurationMilliseconds)
            .ThenBy(static item => item.ModuleName, StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
    }

    private static double CapacityPercent(double durationMilliseconds, double windowMilliseconds, int logicalProcessorCount)
    {
        var capacityMilliseconds = windowMilliseconds * Math.Max(1, logicalProcessorCount);
        return capacityMilliseconds <= 0
            ? 0
            : Math.Round(Math.Clamp(durationMilliseconds * 100d / capacityMilliseconds, 0, 100), 4);
    }

    private sealed class InterruptSlice(long startedAt)
    {
        public long StartedAt { get; } = startedAt;
        public double TotalDurationMilliseconds { get; private set; }
        public int EventCount { get; private set; }
        public int LongEventCount { get; private set; }
        public OracleSystemInterruptEventSample? Maximum { get; private set; }
        public Dictionary<string, InterruptDriverAccumulator> Drivers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Observe(OracleSystemInterruptEventSample sample)
        {
            TotalDurationMilliseconds += sample.DurationMilliseconds;
            EventCount++;
            if (sample.DurationMilliseconds >= SystemInterruptMeasurementSemantics.LongEventMilliseconds)
            {
                LongEventCount++;
            }

            if (Maximum is null || sample.DurationMilliseconds > Maximum.Value.DurationMilliseconds)
            {
                Maximum = sample;
            }

            var key = string.IsNullOrWhiteSpace(sample.ModulePath) ? sample.ModuleName : sample.ModulePath;
            if (!Drivers.TryGetValue(key, out var driver))
            {
                driver = new InterruptDriverAccumulator(sample.ModuleName, sample.ModulePath);
                Drivers[key] = driver;
            }

            driver.Observe(sample.DurationMilliseconds);
        }
    }

    private sealed class InterruptDriverAccumulator(string moduleName, string? modulePath)
    {
        public string ModuleName { get; } = moduleName;
        public string? ModulePath { get; } = modulePath;
        public double TotalDurationMilliseconds { get; private set; }
        public double MaximumSingleDurationMilliseconds { get; private set; }
        public int EventCount { get; private set; }
        public int LongEventCount { get; private set; }

        public void Observe(double durationMilliseconds)
        {
            TotalDurationMilliseconds += durationMilliseconds;
            MaximumSingleDurationMilliseconds = Math.Max(MaximumSingleDurationMilliseconds, durationMilliseconds);
            EventCount++;
            if (durationMilliseconds >= SystemInterruptMeasurementSemantics.LongEventMilliseconds)
            {
                LongEventCount++;
            }
        }

        public void Add(InterruptDriverAccumulator other)
        {
            TotalDurationMilliseconds += other.TotalDurationMilliseconds;
            MaximumSingleDurationMilliseconds = Math.Max(MaximumSingleDurationMilliseconds, other.MaximumSingleDurationMilliseconds);
            EventCount += other.EventCount;
            LongEventCount += other.LongEventCount;
        }
    }
}

internal readonly record struct OracleSystemInterruptEventSample(
    long ObservedTickMilliseconds,
    string Kind,
    double DurationMilliseconds,
    ulong RoutineAddress,
    string ModuleName,
    string? ModulePath);
