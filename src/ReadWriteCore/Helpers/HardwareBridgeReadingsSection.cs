using System.Runtime.InteropServices;
using ResourceManager.Adapter.SharedMemory;

namespace ResourceManager.ReadWrite.Helpers;

public enum HardwareBridgeReadingKey
{
    StapmLimitWatts, StapmValueWatts, FastLimitWatts, SlowLimitWatts,
    TdcLimitAmps, EdcLimitAmps, CurveOptimizerCounts,
    IntegratedGpuCurveOptimizerCounts, MaxBoostFrequencyMhz
}

public enum HardwareBridgeReadingStatus { Missing = 0, Valid = 1, Failed = 2 }

public readonly record struct HardwareBridgeReading(double? Value, string Unit,
    HardwareBridgeReadingStatus Status, long TimestampUnixMilliseconds);

/// <summary>One complete publication from the helper; reading never triggers sampling.</summary>
public readonly record struct HardwareBridgeReadingsSnapshot(
    HardwareBridgeReading StapmLimit, HardwareBridgeReading StapmValue,
    HardwareBridgeReading FastLimit, HardwareBridgeReading SlowLimit,
    HardwareBridgeReading TdcLimit, HardwareBridgeReading EdcLimit,
    HardwareBridgeReading CurveOptimizer, HardwareBridgeReading IntegratedGpuCurveOptimizer,
    HardwareBridgeReading MaxBoostFrequency)
{
    public double? StapmLimitWatts => StapmLimit.Value;
    public double? StapmValueWatts => StapmValue.Value;
    public double? FastLimitWatts => FastLimit.Value;
    public double? SlowLimitWatts => SlowLimit.Value;
    public double? TdcLimitAmps => TdcLimit.Value;
    public double? EdcLimitAmps => EdcLimit.Value;
    public double? CurveOptimizerCounts => CurveOptimizer.Value;
    public double? IntegratedGpuCurveOptimizerCounts => IntegratedGpuCurveOptimizer.Value;
    public double? MaxBoostFrequencyMhz => MaxBoostFrequency.Value;

    public HardwareBridgeReading GetReading(HardwareBridgeReadingKey key) => key switch
    {
        HardwareBridgeReadingKey.StapmLimitWatts => StapmLimit,
        HardwareBridgeReadingKey.StapmValueWatts => StapmValue,
        HardwareBridgeReadingKey.FastLimitWatts => FastLimit,
        HardwareBridgeReadingKey.SlowLimitWatts => SlowLimit,
        HardwareBridgeReadingKey.TdcLimitAmps => TdcLimit,
        HardwareBridgeReadingKey.EdcLimitAmps => EdcLimit,
        HardwareBridgeReadingKey.CurveOptimizerCounts => CurveOptimizer,
        HardwareBridgeReadingKey.IntegratedGpuCurveOptimizerCounts => IntegratedGpuCurveOptimizer,
        HardwareBridgeReadingKey.MaxBoostFrequencyMhz => MaxBoostFrequency,
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    public HardwareBridgeReading GetReading(string field) => GetReading(field switch
    {
        "stapmLimitWatts" => HardwareBridgeReadingKey.StapmLimitWatts,
        "stapmValueWatts" => HardwareBridgeReadingKey.StapmValueWatts,
        "fastLimitWatts" => HardwareBridgeReadingKey.FastLimitWatts,
        "slowLimitWatts" => HardwareBridgeReadingKey.SlowLimitWatts,
        "tdcLimitAmps" => HardwareBridgeReadingKey.TdcLimitAmps,
        "edcLimitAmps" => HardwareBridgeReadingKey.EdcLimitAmps,
        "curveOptimizerCounts" => HardwareBridgeReadingKey.CurveOptimizerCounts,
        "integratedGpuCurveOptimizerCounts" => HardwareBridgeReadingKey.IntegratedGpuCurveOptimizerCounts,
        "maxBoostFrequencyMhz" => HardwareBridgeReadingKey.MaxBoostFrequencyMhz,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown helper reading.")
    });
}

/// <summary>One helper-owned, uniquely named section; one serial writer and read-only consumers.</summary>
public sealed unsafe class HardwareBridgeReadingsSection : IDisposable
{
    // Little endian: sequence at byte 0, followed by nine 24-byte readings.
    // Entry: double value, int64 Unix milliseconds, int32 status, int32 unit code.
    // Unit codes: 1=W, 2=A, 3=档, 4=MHz. The fixed layout belongs to this process pair.
    internal const int HeaderSize = 8, ReadingSize = 24, ReadingCount = 9;
    public const int Capacity = HeaderSize + ReadingSize * ReadingCount;
    private readonly SharedMemoryRegion region;

    private HardwareBridgeReadingsSection(SharedMemoryRegion region) => this.region = region;

    public static HardwareBridgeReadingsSection CreateProducer(string sectionName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Named readings sections require Windows.");
        return new(SharedMemoryRegion.CreateNew(sectionName, Capacity));
    }

    public static HardwareBridgeReadingsSection OpenReader(string sectionName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Named readings sections require Windows.");
        return new(SharedMemoryRegion.OpenExisting(sectionName, Capacity, writable: false));
    }

    /// <summary>Bounded, allocation-free read. Null means no coherent publication.</summary>
    public HardwareBridgeReadingsSnapshot? Read()
    {
        var sequence = (long*)region.Pointer;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = Volatile.Read(ref *sequence);
            if (before == 0 || (before & 1) != 0) continue;
            var entries = (Entry*)((byte*)sequence + HeaderSize);
            var a = Decode(entries[0], 0); var b = Decode(entries[1], 1);
            var c = Decode(entries[2], 2); var d = Decode(entries[3], 3);
            var e = Decode(entries[4], 4); var f = Decode(entries[5], 5);
            var g = Decode(entries[6], 6); var h = Decode(entries[7], 7);
            var i = Decode(entries[8], 8);
            Thread.MemoryBarrier();
            if (before == Volatile.Read(ref *sequence)) return new(a, b, c, d, e, f, g, h, i);
        }
        return null;
    }

    /// <summary>Called serially by the producer. Invalid values replace the reading with typed absence.</summary>
    public void Publish(in HardwareBridgeReadingsSnapshot snapshot)
    {
        if (!region.Writable) throw new InvalidOperationException("This readings section is read-only.");
        var sequence = (long*)region.Pointer;
        Interlocked.Increment(ref *sequence); // odd before modifying payload
        var entries = (Entry*)((byte*)sequence + HeaderSize);
        for (var index = 0; index < ReadingCount; index++)
        {
            var reading = snapshot.GetReading((HardwareBridgeReadingKey)index);
            var status = reading.Status;
            if (reading.Unit != Unit(index) || status is not (HardwareBridgeReadingStatus.Missing
                or HardwareBridgeReadingStatus.Valid or HardwareBridgeReadingStatus.Failed)
                || status == HardwareBridgeReadingStatus.Valid && (reading.Value is not { } value
                    || !double.IsFinite(value) || reading.TimestampUnixMilliseconds <= 0))
                status = HardwareBridgeReadingStatus.Missing;
            entries[index] = new Entry
            {
                Value = status == HardwareBridgeReadingStatus.Valid ? reading.Value.GetValueOrDefault() : 0,
                TimestampUnixMilliseconds = reading.TimestampUnixMilliseconds,
                Status = status, Unit = UnitCode(index)
            };
        }
        Interlocked.Increment(ref *sequence); // complete publication
    }

    private static HardwareBridgeReading Decode(Entry entry, int index)
    {
        var status = entry.Status;
        if (entry.Unit != UnitCode(index) || status is not (HardwareBridgeReadingStatus.Missing
            or HardwareBridgeReadingStatus.Valid or HardwareBridgeReadingStatus.Failed)
            || status == HardwareBridgeReadingStatus.Valid
                && (!double.IsFinite(entry.Value) || entry.TimestampUnixMilliseconds <= 0))
            status = HardwareBridgeReadingStatus.Missing;
        return new(status == HardwareBridgeReadingStatus.Valid ? entry.Value : null,
            Unit(index), status, entry.TimestampUnixMilliseconds);
    }

    public static string Unit(int index) => UnitCode(index) switch
    {
        1 => "W", 2 => "A", 3 => "档", _ => "MHz"
    };

    private static int UnitCode(int index) => index switch
    {
        >= 0 and < 4 => 1, < 6 and >= 4 => 2, < 8 and >= 6 => 3, 8 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void Dispose() => region.Dispose();

    [StructLayout(LayoutKind.Sequential, Pack = 8, Size = ReadingSize)]
    private struct Entry
    {
        public double Value;
        public long TimestampUnixMilliseconds;
        public HardwareBridgeReadingStatus Status;
        public int Unit;
    }
}
