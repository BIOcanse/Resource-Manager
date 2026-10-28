using ResourceManager.TestFixtures;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static partial class EdidModeSubscriptionConsumer
{
    private static async Task RunTypeVViRecordedAsync(SubscriptionRoleSettings settings,
        NativeSamplingSubscriptionConfiguration configuration, CancellationToken token)
    {
        var source = new RecordedSource(DisplayIdTypeVViFixture.Bytes());
        using var owner = source.CreateOwner();
        var backend = owner.CreateBackend();
        Require(settings.RoleId == 2 && source.Reads == 0 && backend.Catalog.Fields.IsEmpty,
            "Type V/VI subscription uses native role 2 and starts passively");

        await backend.DiscoverAsync(token);
        var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
        Require(backend.Catalog.Fields.Any(item => item.Field == field), "Type V/VI keeps the original owner field");
        var reads = source.Reads;
        VerifyTypeVVi(backend.ReadCached(field));
        Require(source.Reads == reads, "Type V/VI cached owner Current is passive");

        var failures = new List<SubscriptionWorkerFailure>();
        var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture());
        var readsAfterStop = 0;
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => failures.Add(failure)))
        {
            await subscription.StartAsync(token);
            await using var stream = subscription.SubscribeAsync("recorded-displayid13-type-v-vi", [field],
                TimeSpan.FromMilliseconds(250), new(FieldSubscriptionSamplingMode.FreshOnTrigger), token).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "native role 2 delivers the real Type V/VI declaration");
            var delivered = stream.Current.Single();
            VerifyTypeVVi(delivered);
            await borrow.SecondQueryStarted.WaitAsync(token);
            await subscription.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.Count == 0,
                "Type V/VI role 2 stop drains cleanly");

            var current = backend.ReadCached(field);
            Require(source.Reads == reads + 1
                && current.ObservedAt == delivered.ObservedAt
                && current.Value!.Structure!.Value.GetRawText() == delivered.Value!.Structure!.Value.GetRawText(),
                "native role 2 and subscription share the original owner Current");
            readsAfterStop = source.Reads;
            await Task.Delay(300, token);
            var stopped = backend.ReadCached(field);
            Require(source.Reads == readsAfterStop && stopped.ObservedAt == current.ObservedAt
                && stopped.Value!.Structure!.Value.GetRawText() == current.Value!.Structure!.Value.GetRawText(),
                "Type V/VI stop/drain leaves Current stable");
        }

        var direct = (await backend.CollectAsync([field], token)).Single();
        VerifyTypeVVi(direct);
        var directCurrent = backend.ReadCached(field);
        Require(source.Reads == readsAfterStop + 1
            && direct.ObservedAt == directCurrent.ObservedAt
            && direct.Value!.Structure!.Value.GetRawText() == directCurrent.Value!.Structure!.Value.GetRawText(),
            "stopped subscription leaves the original owner Direct route usable");

        source.Fail = true;
        Require((await backend.CollectAsync([field], token)).Single()
            is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && backend.ReadCached(field).Value is null,
            "Type V/VI direct source failure clears Current after stop");
        source.Fail = false;
        VerifyTypeVVi((await backend.CollectAsync([field], token)).Single());

        source.SetUnsupportedTypeVVi(true);
        var expectedRaw = source.RawBytes;
        var unsupported = await backend.CollectAsync([field, rawField], token);
        var unavailableMode = unsupported.Single(item => item.Field == field);
        var retainedRaw = unsupported.Single(item => item.Field == rawField);
        Require(unavailableMode is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && backend.ReadCached(field).Value is null
            && retainedRaw.Status == FieldObservationStatus.Current
            && retainedRaw.Value?.Bytes is { } raw && raw.AsSpan().SequenceEqual(expectedRaw),
            "unsupported Type V formula withholds the complete mode capability while exact raw remains current");
        source.SetUnsupportedTypeVVi(false);
        VerifyTypeVVi((await backend.CollectAsync([field], token)).Single());

        source.SetContradictoryTypeViClock(true);
        var ambiguousClock = (await backend.CollectAsync([field], token)).Single();
        VerifyTypeVViClockConflict(ambiguousClock);
        source.SetContradictoryTypeViClock(false);
        VerifyTypeVVi((await backend.CollectAsync([field], token)).Single());

        Console.WriteLine("[DISPLAYID13-TYPEVVI-SUB] parse; role2/current; stop/drain; direct; failure/recovery; unsupported/raw; recorded-only");
    }

    private static void VerifyTypeVVi(FieldReading reading)
    {
        Require(reading.Field == new FieldReference(RecordedSource.Identity, RecordedSource.Name)
            && reading.SourceId == "windows.registry-monitor-edid"
            && reading.Status == FieldObservationStatus.Current
            && reading.ObservedAt is not null
            && reading.Value?.Structure is not null,
            "Type V/VI subscription declares the original owner field/current");
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
