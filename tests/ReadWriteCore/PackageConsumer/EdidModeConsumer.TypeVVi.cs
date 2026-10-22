using ResourceManager.TestFixtures;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;

internal static partial class EdidModeConsumer
{
    private static async Task RunTypeVViRecordedAsync()
    {
        var source = new RecordedSource(DisplayIdTypeVViFixture.Bytes());
        using var owner = source.CreateOwner();
        using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "Type V/VI owner construction is passive");

        await core.DiscoverAsync();
        var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
        Require(core.Catalog.Fields.Any(item => item.Field == field), "Type V/VI keeps the original owner field");
        var reads = source.Reads;
        VerifyTypeVVi(core.ReadCached(field));
        Require(source.Reads == reads, "Type V/VI owner Current is passive");
        Require(core.ReadCached(new(RecordedSource.Identity, "display_all_resolution_actual")).Value is null,
            "static EDID declarations do not imply an active display mode");

        var direct = (await core.CollectAsync([field])).Single();
        VerifyTypeVVi(direct);
        var current = core.ReadCached(field);
        Require(source.Reads == reads + 1
            && direct.Value!.Structure!.Value.GetRawText() == current.Value!.Structure!.Value.GetRawText()
            && direct.ObservedAt == current.ObservedAt,
            "Type V/VI parser Direct updates the original owner Current");

        source.Fail = true;
        Require((await core.CollectAsync([field])).Single()
            is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.Catalog.Fields.Any(item => item.Field == field)
            && core.ReadCached(field).Value is null,
            "Type V/VI source failure clears Current and keeps field identity");
        source.Fail = false;
        VerifyTypeVVi((await core.CollectAsync([field])).Single());

        source.SetUnsupportedTypeVVi(true);
        var expectedRaw = source.RawBytes;
        var unsupported = await core.CollectAsync([field, rawField]);
        var unavailableMode = unsupported.Single(item => item.Field == field);
        var retainedRaw = unsupported.Single(item => item.Field == rawField);
        Require(unavailableMode is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.ReadCached(field).Value is null
            && retainedRaw.Status == FieldObservationStatus.Current
            && retainedRaw.Value?.Bytes is { } raw && raw.AsSpan().SequenceEqual(expectedRaw),
            "unsupported Type V formula withholds the complete mode capability while exact raw remains current");
        source.SetUnsupportedTypeVVi(false);
        VerifyTypeVVi((await core.CollectAsync([field])).Single());

        source.SetContradictoryTypeViClock(true);
        var ambiguousClock = (await core.CollectAsync([field])).Single();
        VerifyTypeVViClockConflict(ambiguousClock);
        source.SetContradictoryTypeViClock(false);
        VerifyTypeVVi((await core.CollectAsync([field])).Single());

        Console.WriteLine("[DISPLAYID13-TYPEVVI-RW] parse; owner/current/direct; failure/recovery; unsupported/raw; recorded-only");
    }

    private static void VerifyTypeVVi(FieldReading reading)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid"
            && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null
            && reading.Value?.Structure is not null,
            "Type V/VI real parser owner declares the original current field");
        DisplayIdTypeVViConsumerAssertions.Verify(reading.Value!.Structure!.Value);
    }

    private static void VerifyTypeVViClockConflict(FieldReading reading)
    {
        Require(reading.Status == FieldObservationStatus.Current
            && reading.SourceId == "windows.registry-monitor-edid"
            && reading.Value?.Structure is not null,
            "Type VI contradictory clock does not reject the owner mode capability");
        DisplayIdTypeVViConsumerAssertions.VerifyContradictoryTypeViClock(reading.Value!.Structure!.Value);
    }

    private sealed partial class RecordedSource
    {
        internal void SetUnsupportedTypeVVi(bool unsupported)
            => DisplayIdTypeVViFixture.SetUnsupportedTypeV(bytes, unsupported);

        internal void SetContradictoryTypeViClock(bool contradictory)
            => DisplayIdTypeVViFixture.SetContradictoryTypeViClock(bytes, contradictory);
    }
}
