using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite.Windows.Diagnostics;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class WindowsWheaHistorySubscriptionConsumer
{
    internal static async Task RunWindowsAsync(WindowsWheaHistoryRequest request)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = new WindowsWheaInformationOwner(request);
        var backend = owner.CreateBackend();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var catalog = await backend.DiscoverAsync(timeout.Token);
        if (catalog.Fields.IsEmpty) { Console.WriteLine("WHEA scoped history has no bindable events; no hardware health inference."); return; }
        var fields = catalog.Fields.Take(3).Select(static capability => capability.Field).ToImmutableArray();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(), Generation = 1,
            MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
            MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
        };
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(), settings, in configuration,
            failure => Console.WriteLine($"WHEA worker failure: {failure.Stage}; {failure.Error.Message}"), TimeSpan.FromMilliseconds(250));
        await composition.StartAsync(timeout.Token);
        await using var stream = composition.SubscribeAsync("whea-history", fields, TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        if (!await stream.MoveNextAsync()) throw new InvalidDataException("WHEA composition ended before its same-owner publication.");
        foreach (var reading in stream.Current)
        {
            var direct = backend.ReadCached(reading.Field);
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, direct);
        }
        await composition.StopAsync();
        if (await stream.MoveNextAsync()) throw new InvalidDataException("Retired WHEA run emitted late publication.");
        _ = await backend.CollectAsync(fields, timeout.Token);
        Console.WriteLine("WHEA actual source subscription uses original capture/current; stopped run retains externally owned reader.");
    }
}
