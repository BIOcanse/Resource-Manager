using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Files.Layout;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class WindowsFileLayoutSubscriptionConsumer
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var reader = new RecordedVolumeReader(Map(13));
        using var query = new WindowsFileLayoutInformationQuery(
            WindowsFileLayoutQueryScope.ForVolumeMountPoint(@"C:\"), reader);
        var catalog = await query.Backend.DiscoverAsync(timeout.Token); // The caller explicitly starts the bounded full-map read.
        var field = catalog.Fields.Single().Field;
        using var borrow = query.BorrowSubscriptionCapture();
        var settings = new SubscriptionRoleSettings(1, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
            Generation = 1, MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
            MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
        };
        await using var composition = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => throw new InvalidOperationException("Unexpected file-layout subscription worker failure", failure.Error),
            TimeSpan.FromMilliseconds(250));
        await composition.StartAsync(timeout.Token);
        await using var stream = composition.SubscribeAsync("volume-file-layout", [field], TimeSpan.FromSeconds(1), timeout.Token)
            .GetAsyncEnumerator();
        // Cached access is immediate; the notification stream reports later
        // explicit commits and must not invent another volume-map read.
        var first = query.Backend.ReadCached(field);
        if (first.Status != FieldObservationStatus.Current || first.Value?.Structure is not { } firstMap
            || firstMap.GetProperty("totalClusters").GetString() != "13"
            || first != query.Backend.ReadCached(field) || reader.Reads != 1)
            throw new InvalidDataException("Subscription demand performed an implicit map read or diverged from the query owner's current.");

        var next = stream.MoveNextAsync().AsTask();
        if (reader.Reads != 1)
            throw new InvalidDataException("Subscription registration performed an implicit map read.");
        reader.Value = Map(14);
        await query.CaptureAsync(timeout.Token); // The caller explicitly re-reads; this publication wakes cached observers.
        if (!await next) throw new InvalidDataException("The explicit file-layout update was not published.");
        var updated = stream.Current.Single();
        if (updated.Status != FieldObservationStatus.Current || updated.Value?.Structure is not { } updatedMap
            || updatedMap.GetProperty("totalClusters").GetString() != "14"
            || updated != query.Backend.ReadCached(field) || reader.Reads != 2)
            throw new InvalidDataException("The subscription did not observe the same explicitly committed map current.");

        await composition.StopAsync();
        if (await stream.MoveNextAsync()) throw new InvalidDataException("Retired file-layout subscription emitted a later observation.");
        if (query.Backend.ReadCached(field) != updated)
            throw new InvalidDataException("Stopping demand retired the explicit query owner's current.");
        Console.WriteLine("SubscriptionCore package consumer: cached role-2 observations use the same query current; full map IOCTL is explicit only.");
    }

    private static WindowsFileLayoutReadResult Map(ulong clusters)
    {
        var now = DateTimeOffset.UtcNow;
        var bitCount = checked((int)((clusters + 7) / 8));
        var bits = new byte[bitCount];
        if (bits.Length > 0) bits[0] = 0b0000_1101;
        var page = new WindowsVolumeBitmapPage(0, clusters, clusters, Convert.ToBase64String(bits), now);
        var map = new WindowsVolumeAllocationMap(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\",
            19, clusters, 4096, now, now, [page]);
        return new(WindowsFileLayoutTargetKind.VolumeMountPoint,
            @"windows-file-layout|volume|\\?\Volume{11111111-2222-3333-4444-555555555555}|0000000000000013", null, map);
    }

    private sealed class RecordedVolumeReader(WindowsFileLayoutReadResult initial) : IWindowsFileLayoutInformationReader
    {
        private WindowsFileLayoutReadResult current = initial;
        private int reads;
        internal int Reads => Volatile.Read(ref reads);
        internal WindowsFileLayoutReadResult Value { set => Volatile.Write(ref current, value); }
        public WindowsFileLayoutReadResult Read(WindowsFileLayoutQueryScope scope,
            WindowsFileLayoutInformationFields selected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scope.TargetKind != WindowsFileLayoutTargetKind.VolumeMountPoint
                || selected != WindowsFileLayoutInformationFields.VolumeFragmentationMap)
                throw new InvalidOperationException("The recorded producer was invoked outside the requested volume field.");
            Interlocked.Increment(ref reads);
            return Volatile.Read(ref current);
        }
    }
}
