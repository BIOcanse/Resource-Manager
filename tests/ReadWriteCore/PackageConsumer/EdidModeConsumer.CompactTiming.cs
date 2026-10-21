using ResourceManager.TestFixtures;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;

internal static partial class EdidModeConsumer
{
    private static async Task RunCompactRecordedAsync()
    {
        foreach (var variant in new[] { 0, 1, 2 })
        {
            var typeI = variant == 1; var mixed = variant == 2;
            var source = new RecordedSource(mixed ? DisplayIdMixedStructureFixture.Bytes() : DisplayIdCompactTimingFixture.Bytes(typeI));
            using var owner = source.CreateOwner(); using var client = new HttpClient();
            var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
            Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "compact construction is passive");
            await core.DiscoverAsync(); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
            var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
            var reads = source.Reads; VerifyCompact(core.ReadCached(field), typeI, mixed); Require(reads == source.Reads, "compact cache is passive");
            var direct = (await core.CollectAsync([field])).Single(); VerifyCompact(direct, typeI, mixed);
            Require(core.ReadCached(field).ObservedAt == direct.ObservedAt, "compact Direct and Current agree");
            source.SetUnknownCompact(typeI, true, mixed);
            var bad = await core.CollectAsync([field, rawField]);
            Require(bad.Single(r => r.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null }
                && bad.Single(r => r.Field == rawField).Value?.Bytes is { } raw && raw.AsSpan().SequenceEqual(source.RawBytes),
                "compact bad declaration clears value and retains exact raw");
            source.SetUnknownCompact(typeI, false, mixed); VerifyCompact((await core.CollectAsync([field])).Single(), typeI, mixed);
            source.Fail = true; Require((await core.CollectAsync([field])).Single().Value is null, "compact read failure clears current");
            source.Fail = false; VerifyCompact((await core.CollectAsync([field])).Single(), typeI, mixed);
            if (mixed) Console.WriteLine("[DISPLAYID-MIXED-STRUCTURE-RW] real-wire12/20; separate-counts; same-owner/current; invalid-header/raw/recovery");
        }
        Console.WriteLine("[DISPLAYID13-COMPACT-RW] normative-II-byte3/10; TypeI-odd-full-frame; actual-owner/current; bad/raw/recovery; no-live-display");
    }

    private static void VerifyCompact(FieldReading reading, bool typeI, bool mixed = false)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.ObservedAt is not null
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Value?.Structure is not null, "compact declared current/source");
        if (mixed) DisplayIdMixedStructureFixture.Verify(reading.Value!.Structure!.Value);
        else DisplayIdCompactTimingFixture.Verify(reading.Value!.Structure!.Value, typeI);
    }

    private sealed partial class RecordedSource
    {
        internal RecordedSource(byte[] compactBytes) { bytes = compactBytes.ToArray(); }
        internal void SetUnknownCompact(bool typeI, bool unknown, bool mixed = false)
        { if (mixed) DisplayIdMixedStructureFixture.SetUnknown(bytes, unknown); else DisplayIdCompactTimingFixture.SetUnknown(bytes, typeI, unknown); }
    }
}
