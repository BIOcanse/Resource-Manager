using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class NvidiaDrsInformationSubscriptionConsumer
{
    internal static async Task RunRecordedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var limits = new NvidiaDrsReadLimits(1, 2, 2, 1, 50_000);
        var scope = new NvidiaDrsInformationRequest([new("Game", new uint[] { 7, 8 })], limits);
        var queries = new ConcurrentQueue<uint[]>();
        var access = owner.CreateNvidiaDrsInformation(scope, (profiles, supplied, token) =>
        {
            token.ThrowIfCancellationRequested();
            Require(supplied == limits && profiles.Count == 1 && profiles[0].ProfileName == "Game", "original typed scope");
            queries.Enqueue(profiles[0].SettingIds.ToArray());
            return new([new("Game", 7, 0, 0, (uint)profiles[0].SettingIds.Count,
                [.. profiles[0].SettingIds.Select(id => new NvidiaDrsSetting(id, "setting", 4, 0, 0, 1, new NvidiaDrsQwordValue(ulong.MaxValue)))])]);
        });
        Require(queries.IsEmpty && access.Backend.Catalog.Fields.Length == 2, "passive scoped catalog");
        var failures = new ConcurrentQueue<string>();
        var configuration = Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(access.BorrowSubscriptionCapture(),
            new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000), in configuration,
            failure => failures.Enqueue(failure.Stage + ": " + failure.Error.Message), TimeSpan.FromMilliseconds(10));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = composition.SubscribeAsync("drs-recorded", [scope.Fields[0]],
            TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token), "real original role-2 capture");
            var reading = stream.Current.Single();
            Require(reading.Status == FieldObservationStatus.Current
                && reading.Value!.Structure!.Value.GetProperty("currentValue").GetProperty("value").GetString() == "18446744073709551615",
                "lossless QWORD subscription value");
            Require(queries.Last().SequenceEqual(new uint[] { 7 }), "selected exact setting only");
            Require(access.Backend.ReadCached(scope.Fields[0]).Value!.Structure!.Value.GetRawText()
                == owner.ReadState().InformationFields.FindField(scope.Fields[0])!.Value!.Value.GetRawText(), "same original Current");
            await composition.StopAsync(); Require(!await stream.MoveNextAsync(), "Stop retires delivery");
        }
        var before = queries.Count;
        _ = await access.Backend.CollectAsync([scope.Fields[1]], default);
        Require(queries.Count > before && queries.Last().SequenceEqual(new uint[] { 8 }), "direct source still available after Stop");
        Require(failures.IsEmpty, "no original worker failures");
        Console.WriteLine("[NVIDIA-DRS-SUBSCRIPTION-PACKAGE] selected library setting uses original owner/role2/Current; recorded source, no driver.");
    }
    private static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version, StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
        Generation = 1, MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
        MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
        DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1, ActiveTtlMilliseconds = 3000,
        MaximumFutureSkewMilliseconds = 1000
    };
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException("NVIDIA DRS subscription consumer: " + message); }
}
