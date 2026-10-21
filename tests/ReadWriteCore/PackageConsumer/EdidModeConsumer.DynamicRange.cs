using ResourceManager.TestFixtures;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;

internal static partial class EdidModeConsumer
{
    private static async Task RunDynamicRangeRecordedAsync()
    {
        var source = new RecordedSource(DisplayIdDynamicRangeFixture.Bytes());
        using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "dynamic range construction passive");
        await core.DiscoverAsync(); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
        var reads = source.Reads; VerifyDynamicRange(core.ReadCached(field)); Require(reads == source.Reads, "range cached read passive");
        var direct = (await core.CollectAsync([field])).Single(); VerifyDynamicRange(direct);
        Require(direct.ObservedAt == core.ReadCached(field).ObservedAt, "range direct uses original Current");
        source.SetUnknownDynamicRange(true); var bad = await core.CollectAsync([field, rawField]);
        Require(bad.Single(r => r.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null }
            && bad.Single(r => r.Field == rawField).Value?.Bytes is { } raw && raw.AsSpan().SequenceEqual(source.RawBytes), "range bad/raw");
        source.SetUnknownDynamicRange(false); VerifyDynamicRange((await core.CollectAsync([field])).Single());
        source.Fail = true; Require((await core.CollectAsync([field])).Single().Value is null, "range failure clears Current");
        source.Fail = false; VerifyDynamicRange((await core.CollectAsync([field])).Single());
        Console.WriteLine("[DISPLAYID20-DYNAMIC-RANGE-RW] actual-owner/current; clocks/rates/seamless/raw; bad/recovery; no-live-display");
    }

    private static void VerifyDynamicRange(FieldReading reading)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.ObservedAt is not null
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Value?.Structure is not null, "range declared source/current");
        DisplayIdDynamicRangeConsumerAssertions.Verify(reading.Value!.Structure!.Value);
    }

    private sealed partial class RecordedSource
    {
        internal void SetUnknownDynamicRange(bool unknown)
        {
            Require(bytes.Length == 256 && bytes[133] == 0x25, "dynamic range fixture identity");
            bytes[144] = unknown ? (byte)0x85 : (byte)0x81; DisplayIdDynamicRangeFixture.Checksums(bytes);
        }
    }
}
