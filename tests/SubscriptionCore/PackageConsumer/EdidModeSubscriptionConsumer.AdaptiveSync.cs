using ResourceManager.TestFixtures;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static partial class EdidModeSubscriptionConsumer
{
    private static async Task RunAdaptiveSyncRecordedAsync(SubscriptionRoleSettings settings,
        NativeSamplingSubscriptionConfiguration configuration, CancellationToken token, int variant)
    {
        var source = new RecordedSource(DisplayIdAdaptiveSyncFixture.Bytes(variant));
        using var owner = source.CreateOwner(); var backend = owner.CreateBackend();
        Require(source.Reads == 0 && backend.Catalog.Fields.IsEmpty, "range subscription construction passive");
        await backend.DiscoverAsync(token); var field = new FieldReference(RecordedSource.Identity, RecordedSource.Name);
        var rawField = new FieldReference(RecordedSource.Identity, "display_all_edid_raw_declared");
        var borrow = new FirstQueryBorrow(owner.BorrowSubscriptionCapture()); var failures = new List<SubscriptionWorkerFailure>();
        await using (var subscription = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => failures.Add(failure)))
        {
            await subscription.StartAsync(token);
            await using var stream = subscription.SubscribeAsync("recorded-displayid20-dynamic-range", [field], TimeSpan.FromMilliseconds(250),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), token).GetAsyncEnumerator();
            Require(await stream.MoveNextAsync(), "range original role2 delivery");
            var delivered = stream.Current.Single(); VerifyAdaptiveSync(delivered, variant);
            await borrow.SecondQueryStarted.WaitAsync(token); await subscription.StopAsync();
            Require(!await stream.MoveNextAsync() && failures.Count == 0, "range original stop/drain");
            var reads = source.Reads; var current = backend.ReadCached(field);
            Require(reads == source.Reads && current.ObservedAt == delivered.ObservedAt
                && current.Value!.Structure!.Value.GetRawText() == delivered.Value!.Structure!.Value.GetRawText(), "range same Current after stop");
        }
        source.SetUnknownAdaptiveSync(true); var bad = await backend.CollectAsync([field, rawField], token);
        Require(bad.Single(r => r.Field == field) is { Status: FieldObservationStatus.Unavailable, Value: null }
            && bad.Single(r => r.Field == rawField).Value?.Bytes is { } raw && raw.AsSpan().SequenceEqual(source.RawBytes), "range bad/raw after stop");
        source.SetUnknownAdaptiveSync(false); VerifyAdaptiveSync((await backend.CollectAsync([field], token)).Single(), variant);
        Console.WriteLine("[DISPLAYID-ADAPTIVE-SYNC-SUB] variant=" + variant + "; actual-owner/current; base-rates/flags/exact-duration/raw; bad/recovery; recorded-only");
    }

    private static void VerifyAdaptiveSync(FieldReading reading, int variant)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.ObservedAt is not null
            && reading.SourceId == "windows.registry-monitor-edid" && reading.Value?.Structure is not null, "range subscription declared source/current");
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
