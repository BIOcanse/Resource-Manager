using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class SecurityProductSubscriptionConsumer
{
    internal static async Task RunRecordedAsync()
    {
        var source = new SecurityProductConsumer.RecordedSource();
        using var owner = source.CreateOwner();
        var backend = owner.CreateBackend();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _ = await backend.DiscoverAsync(timeout.Token);
        var reads = source.Reads;
        var failures = new ConcurrentQueue<SubscriptionWorkerFailure>();
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()), Generation = 1,
            MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
            MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
        };
        await using (var subscription = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(),
            new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000), in configuration,
            failures.Enqueue))
        {
            await subscription.StartAsync(timeout.Token);
            SecurityProductConsumer.Require(source.Reads == reads, "starting without demand does not capture");
            await using var stream = subscription.SubscribeAsync("security-recorded", [SecurityProductConsumer.Antivirus],
                TimeSpan.FromMilliseconds(1000), timeout.Token).GetAsyncEnumerator(timeout.Token);
            SecurityProductConsumer.Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token), "original native role delivers selected product field");
            var delivered = stream.Current.Single();
            SecurityProductConsumer.Verify(delivered);
            await subscription.StopAsync();
            SecurityProductConsumer.Require(!await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token), "Stop ends delivery after draining");
            SecurityProductConsumer.Require(failures.IsEmpty && source.Reads > reads, "actual capture occurred without worker failure");
            var cached = backend.ReadCached(SecurityProductConsumer.Antivirus);
            SecurityProductConsumer.Require(cached.Value!.Structure!.Value.GetRawText() == delivered.Value!.Structure!.Value.GetRawText(),
                "subscription and direct reader use the same owner Current");
            reads = source.Reads;
            SecurityProductConsumer.Verify(backend.ReadCached(SecurityProductConsumer.Antivirus));
            SecurityProductConsumer.Require(source.Reads == reads, "stopped cached reads do not restart sampling");
        }
        source.FailAntivirus = true;
        SecurityProductConsumer.Require((await backend.CollectAsync([SecurityProductConsumer.Antivirus], timeout.Token)).Single()
            is { Status: FieldObservationStatus.Unavailable, Value: null }, "stopped subscription leaves direct category failure observable");
        source.FailAntivirus = false;
        SecurityProductConsumer.Verify((await backend.CollectAsync([SecurityProductConsumer.Antivirus], timeout.Token)).Single());
        Console.WriteLine("[SECURITY-PRODUCT-SUB-PACKAGE] nine extensions; original native role/owner/Current; passive start/read; stop/drain; direct failure/recovery; recorded only");
    }
}
