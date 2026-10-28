using ResourceManager.TestFixtures;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static partial class EdidModeSubscriptionConsumer
{
    private static async Task RunCompactRecordedAsync(SubscriptionRoleSettings settings, NativeSamplingSubscriptionConfiguration configuration,
        CancellationToken token, bool typeI, bool mixed = false)
    {
        var source = new RecordedSource(mixed ? DisplayIdMixedStructureFixture.Bytes() : DisplayIdCompactTimingFixture.Bytes(typeI));
        using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "compact subscription construction is passive");
        await backend.DiscoverAsync(token); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
        var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture()); var failures = new List<SubscriptionWorkerFailure>();
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => failures.Add(failure)))
        {
            await subscription.StartAsync(token);
            await using var stream = subscription.SubscribeAsync("recorded-displayid13-compact", [field], TimeSpan.FromMilliseconds(250),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), token).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "compact original role2 delivery");
            var delivered = stream.Current.Single(); VerifyCompact(delivered, typeI, mixed);
            await borrow.SecondQueryStarted.WaitAsync(token); await subscription.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.Count == 0, "compact original stop/drain");
            var reads = source.Reads; var current = backend.ReadCached(field);
            Require(reads == source.Reads && current.ObservedAt == delivered.ObservedAt
                && current.Value!.Structure!.Value.GetRawText() == delivered.Value!.Structure!.Value.GetRawText(), "compact same Current after stop");
        }
        source.SetUnknownCompact(typeI, true, mixed); var bad = await backend.CollectAsync([field, rawField], token);
        Require(bad.Single(r => r.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null }
            && bad.Single(r => r.Field == rawField).Value?.Bytes is { } raw && raw.AsSpan().SequenceEqual(source.RawBytes), "compact bad/raw after stop");
        source.SetUnknownCompact(typeI, false, mixed); VerifyCompact((await backend.CollectAsync([field], token)).Single(), typeI, mixed);
        if (mixed) Console.WriteLine("[DISPLAYID-MIXED-STRUCTURE-SUB] real-wire12/20; separate-counts; actual-native-role2; same-current; stop/drain; invalid-header/raw/recovery");
        else Console.WriteLine("[DISPLAYID13-COMPACT-SUB] actual-native-role2; normative-declaration; same-current; stop/drain; direct-bad/raw/recovery; no-live-display");
    }

    private static void VerifyCompact(FieldReading reading, bool typeI, bool mixed = false)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.ObservedAt is not null
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Value?.Structure is not null, "compact subscription declaration/source");
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
