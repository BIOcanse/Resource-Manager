using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class NpuEngineSubscriptionConsumer
{
    internal static Task RunRecordedAsync() => NpuEngineConsumer.RunRecordedAsync(Observe);

    private static async Task Observe(ReadWriteCore core, WindowsHardwareMetricOwner owner, NpuEngineRecordedResources recording,
        ImmutableArray<FieldReference> fields, CancellationToken token)
    {
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(),
            Generation = 1, MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
            MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
        };
        var failures = new List<SubscriptionWorkerFailure>();
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(), settings,
            in configuration, failure => failures.Add(failure), TimeSpan.FromMilliseconds(100));
        await composition.StartAsync(token);
        var subscriptions = new FieldSubscriptionCore(core, composition);
        await using var stream = subscriptions.SubscribeAsync("recorded-npu-engine", fields, TimeSpan.FromMilliseconds(100), token).GetAsyncEnumerator();
        ImmutableArray<FieldReading> readings = [];
        for (var attempt = 0; attempt < 8; attempt++)
        {
            NpuEngineConsumer.Require(await stream.MoveNextAsync(), "native subscription delivers"); readings = stream.Current;
            NpuEngineConsumer.Require(readings.Select(reading => reading.Field).SequenceEqual(fields), "exact selected engine fields and order");
            foreach (var reading in readings)
                FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, core.ReadCached(reading.Field));
            if (readings.Where(row => row.Field.FieldName == NpuEngineRecordedResources.Ratio).All(row => row.Value?.Number == 125d)) break;
        }
        NpuEngineConsumer.Ratio(readings, 125);
        await composition.StopAsync();
        NpuEngineConsumer.Require(!await stream.MoveNextAsync() && failures.Count == 0, "stop drains existing worker/native subscription");
        var reads = recording.Reads;
        foreach (var field in fields) _ = core.ReadCached(field);
        NpuEngineConsumer.Require(reads == recording.Reads && recording.Opens == 1, "passive current/retained directory after stop");
        Console.WriteLine("[NPU-ENGINE-SUBSCRIPTION-PACKAGE-RECORDED] original-borrow/native-planner;125%; exact-engine-fields; stop-drains; no-live-hardware.");
    }
}
