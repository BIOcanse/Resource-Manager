using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class OperatingSystemUptimeSubscriptionConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = new WindowsOperatingSystemInformationOwner();
        var backend = owner.CreateBackend();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var field = (await backend.DiscoverAsync(timeout.Token)).Fields.Single(field => field.Definition.Name == "os_all_uptime_actual").Field;
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
            failure => Console.WriteLine($"OS uptime worker failure: {failure.Stage}; {failure.Error.Message}"), TimeSpan.FromMilliseconds(250));
        await composition.StartAsync(timeout.Token);
        await using var stream = composition.SubscribeAsync("machine-uptime", [field], TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        if (!await stream.MoveNextAsync()) throw new InvalidDataException("Uptime native subscription ended without observation.");
        var reading = stream.Current.Single();
        FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, backend.ReadCached(field));
        if (reading.Status == FieldObservationStatus.Current)
        {
            if (reading.Value?.Integer is not { Negative: false, HighBits: 0 } || reading.Value!.Number is not null)
                throw new InvalidDataException("Actual uptime is not unsigned64 seconds.");
        }
        else if (reading.Value is not null) throw new InvalidDataException("Unavailable uptime has a synthetic value.");
        Console.WriteLine($"Actual OS uptime subscription: integer={reading.Value?.Integer}; unit=s; status={reading.Status}; same-current=yes; scope=machine.");
        await composition.StopAsync();
        if (await stream.MoveNextAsync()) throw new InvalidDataException("Retired uptime run emitted a late value.");
        var direct = (await backend.CollectAsync([field], timeout.Token)).Single();
        if (direct != backend.ReadCached(field)) throw new InvalidDataException("Stopped subscription changed the direct owner's current.");
        Console.WriteLine("Actual OS uptime subscription completed: native run stopped; direct owner same-current; scope=machine.");
    }
}

