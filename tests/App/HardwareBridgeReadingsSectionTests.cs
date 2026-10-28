using ResourceManager.ReadWrite.Helpers;
using System.IO.MemoryMappedFiles;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeReadingsSectionTests
{
    private static string Name() => $"Local\\ResourceManager.HardwareBridge.Tests.{Guid.NewGuid():N}";

    private static HardwareBridgeReadingsSnapshot Sample(double value, long timestamp = 1700000000000,
        HardwareBridgeReadingStatus status = HardwareBridgeReadingStatus.Valid)
    {
        HardwareBridgeReading Reading(int index) => new(status == HardwareBridgeReadingStatus.Valid ? value : null,
            HardwareBridgeReadingsSection.Unit(index), status, timestamp);
        return new(Reading(0), Reading(1), Reading(2), Reading(3), Reading(4),
            Reading(5), Reading(6), Reading(7), Reading(8));
    }

    [Fact]
    public void ReadCurrent_before_first_publication_is_null_and_read_never_changes_sequence()
    {
        var name = Name();
        using var writer = HardwareBridgeReadingsSection.CreateProducer(name);
        using var reader = HardwareBridgeReadingsSection.OpenReader(name);
        Assert.Null(reader.Read());
        writer.Publish(Sample(45));
        for (var count = 0; count < 100; count++) Assert.Equal(45, reader.Read()!.Value.StapmLimitWatts);
        using var map = MemoryMappedFile.OpenExisting(name);
        using var view = map.CreateViewAccessor();
        Assert.Equal(2, view.ReadInt64(0));
        Assert.Equal(224, HardwareBridgeReadingsSection.Capacity);
    }

    [Fact]
    public void Publication_carries_explicit_unit_status_timestamp_and_replaces_failed_values()
    {
        using var writer = HardwareBridgeReadingsSection.CreateProducer(Name());
        writer.Publish(Sample(45));
        var snapshot = writer.Read()!.Value;
        Assert.Equal("W", snapshot.GetReading(HardwareBridgeReadingKey.StapmValueWatts).Unit);
        Assert.Equal("A", snapshot.GetReading(HardwareBridgeReadingKey.TdcLimitAmps).Unit);
        Assert.Equal("档", snapshot.GetReading(HardwareBridgeReadingKey.CurveOptimizerCounts).Unit);
        Assert.Equal("MHz", snapshot.GetReading(HardwareBridgeReadingKey.MaxBoostFrequencyMhz).Unit);
        writer.Publish(Sample(0, 1700000001000, HardwareBridgeReadingStatus.Failed));
        var failed = writer.Read()!.Value.GetReading(HardwareBridgeReadingKey.StapmLimitWatts);
        Assert.Null(failed.Value);
        Assert.Equal(HardwareBridgeReadingStatus.Failed, failed.Status);
        Assert.Equal(1700000001000, failed.TimestampUnixMilliseconds);
    }

    [Fact]
    public void Incomplete_publication_is_absent_and_bad_entry_is_typed_null()
    {
        var name = Name();
        using var writer = HardwareBridgeReadingsSection.CreateProducer(name);
        using var reader = HardwareBridgeReadingsSection.OpenReader(name);
        writer.Publish(Sample(45));
        using var map = MemoryMappedFile.OpenExisting(name);
        using var view = map.CreateViewAccessor();
        view.Write(0, 3L); // a writer interrupted mid-publication
        Assert.Null(reader.Read());
        view.Write(0, 4L);
        view.Write(8 + 20, 999); // invalid unit, not a consumer-visible number
        Assert.Null(reader.Read()!.Value.StapmLimitWatts);
        view.Write(8 + 20, 1);
        view.Write(8 + 16, 999); // unknown status
        Assert.Equal(HardwareBridgeReadingStatus.Missing, reader.Read()!.Value.StapmLimit.Status);
        Assert.Null(reader.Read()!.Value.StapmLimitWatts);
    }

    [Fact]
    public void Invalid_publication_replaces_previous_numeric_value_and_reader_cannot_publish()
    {
        var name = Name();
        using var producer = HardwareBridgeReadingsSection.CreateProducer(name);
        using var reader = HardwareBridgeReadingsSection.OpenReader(name);
        producer.Publish(Sample(45));
        producer.Publish(Sample(double.NaN));
        Assert.Null(reader.Read()!.Value.StapmLimitWatts);
        producer.Publish(Sample(12, timestamp: 0));
        Assert.Null(reader.Read()!.Value.StapmLimitWatts);
        var wrongUnit = Sample(45) with { StapmLimit = new(45, "A", HardwareBridgeReadingStatus.Valid, 1700000000000) };
        producer.Publish(wrongUnit);
        Assert.Null(reader.Read()!.Value.StapmLimitWatts);
        Assert.Equal(45, reader.Read()!.Value.FastLimitWatts);
        Assert.Throws<InvalidOperationException>(() => reader.Publish(Sample(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HardwareBridgeReadingsSection.Unit(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => HardwareBridgeReadingsSection.Unit(9));
    }

    [Fact]
    public void Fixed_field_lookup_preserves_the_reading_and_disposed_reader_rejects_access()
    {
        var name = Name();
        using var producer = HardwareBridgeReadingsSection.CreateProducer(name);
        producer.Publish(Sample(45));
        var reader = HardwareBridgeReadingsSection.OpenReader(name);
        var snapshot = reader.Read()!.Value;
        Assert.Equal(snapshot.StapmLimit, snapshot.GetReading("stapmLimitWatts"));
        Assert.Equal(snapshot.MaxBoostFrequency, snapshot.GetReading("maxBoostFrequencyMhz"));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetReading("unknown"));
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }

    [Fact]
    public async Task Concurrent_readers_never_observe_values_from_two_publications()
    {
        var name = Name();
        using var writer = HardwareBridgeReadingsSection.CreateProducer(name);
        writer.Publish(Sample(1));
        using var started = new CountdownEvent(4);
        using var release = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            using var reader = HardwareBridgeReadingsSection.OpenReader(name);
            started.Signal();
            release.Wait();
            for (var count = 0; count < 10000; count++)
            {
                if (reader.Read() is not { } current) continue;
                for (var index = 0; index < 9; index++)
                {
                    var reading = current.GetReading((HardwareBridgeReadingKey)index);
                    Assert.Equal(current.StapmLimitWatts, reading.Value);
                    Assert.Equal(current.StapmLimit.TimestampUnixMilliseconds, reading.TimestampUnixMilliseconds);
                }
            }
        })).ToArray();
        started.Wait();
        release.Set();
        for (var count = 2; count < 10000; count++) writer.Publish(Sample(count, 1700000000000 + count));
        await Task.WhenAll(readers);
    }

    [Fact]
    public void Repeated_current_reads_allocate_no_managed_objects()
    {
        using var writer = HardwareBridgeReadingsSection.CreateProducer(Name());
        writer.Publish(Sample(45));
        for (var count = 0; count < 1000; count++) _ = writer.Read();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        double checksum = 0;
        for (var count = 0; count < 10000; count++) checksum += writer.Read()!.Value.StapmLimitWatts!.Value;
        var delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(450000, checksum);
        Assert.Equal(0, delta);
    }
}
