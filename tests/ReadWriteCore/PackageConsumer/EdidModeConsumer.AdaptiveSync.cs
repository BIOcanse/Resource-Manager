using ResourceManager.TestFixtures;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;

internal static partial class EdidModeConsumer
{
    private static async Task RunAdaptiveSyncRecordedAsync(int variant)
    {
        var source = new RecordedSource(DisplayIdAdaptiveSyncFixture.Bytes(variant));
        using var owner = source.CreateOwner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "dynamic range construction passive");
        await core.DiscoverAsync(); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
        var reads = source.Reads; VerifyAdaptiveSync(core.ReadCached(field), variant); Require(reads == source.Reads, "range cached read passive");
        var direct = (await core.CollectAsync([field])).Single(); VerifyAdaptiveSync(direct, variant);
        Require(direct.ObservedAt == core.ReadCached(field).ObservedAt, "range direct uses original Current");
        source.SetUnknownAdaptiveSync(true); var bad = await core.CollectAsync([field, rawField]);
        Require(bad.Single(r => r.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null }
            && bad.Single(r => r.Field == rawField).Value?.Bytes is { } raw && raw.AsSpan().SequenceEqual(source.RawBytes), "range bad/raw");
        source.SetUnknownAdaptiveSync(false); VerifyAdaptiveSync((await core.CollectAsync([field])).Single(), variant);
        source.Fail = true; Require((await core.CollectAsync([field])).Single().Value is null, "range failure clears Current");
        source.Fail = false; VerifyAdaptiveSync((await core.CollectAsync([field])).Single(), variant);
        Console.WriteLine("[DISPLAYID-ADAPTIVE-SYNC-RW] variant=" + variant + "; actual-owner/current; base-rates/flags/exact-duration/raw; bad/recovery; recorded-only");
    }

    private static void VerifyAdaptiveSync(FieldReading reading, int variant)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.ObservedAt is not null
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Value?.Structure is not null, "range declared source/current");
        DisplayIdAdaptiveSyncConsumerAssertions.Verify(reading.Value!.Structure!.Value, variant);
    }

    private sealed partial class RecordedSource
    {
        internal void SetUnknownAdaptiveSync(bool unknown)
        {
            Require(bytes.Length == 256 && bytes[133] == 0x2B, "adaptive range fixture identity");
            bytes[134] = unknown ? (byte)1 : (byte)0;
            DisplayIdDynamicRangeFixture.Checksums(bytes);
        }
    }
}
