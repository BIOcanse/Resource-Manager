using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class DriverInformationSubscriptionConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var backend = owner.CreateBackend();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var catalog = await backend.DiscoverAsync(timeout.Token);
        var field = catalog.Fields.FirstOrDefault(field => field.Field.FieldName == "driver_all_provider_declared")?.Field;
        if (field is null)
        {
            Console.WriteLine($"Actual driver subscription directory: state={owner.ReadState().State}; reason={owner.ReadState().FailureCode}; no available/value claim.");
            return;
        }
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
            failure => Console.WriteLine($"Driver worker failure: {failure.Stage}; {failure.Error.Message}"), TimeSpan.FromMilliseconds(250));
        await composition.StartAsync(timeout.Token);
        await using var stream = composition.SubscribeAsync("device-driver", [field.Value], TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        if (!await stream.MoveNextAsync()) throw new InvalidDataException("Driver subscription ended without observation.");
        var reading = stream.Current.Single();
        if (backend.Catalog.Fields.Any(item => item.Field == field.Value))
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, backend.ReadCached(field.Value));
        if (reading.Status == FieldObservationStatus.Current && string.IsNullOrWhiteSpace(reading.Value?.Text))
            throw new InvalidDataException("Current driver provider is missing.");
        if (reading.Status != FieldObservationStatus.Current && reading.Value is not null)
            throw new InvalidDataException("Unavailable driver provider is fabricated.");
        Console.WriteLine($"Actual device-bound driver subscription: provider={reading.Value?.Text}; status={reading.Status}; same-current=yes; exactidentity={reading.Field.DeviceIdentity}.");
        await composition.StopAsync();
        if (await stream.MoveNextAsync()) throw new InvalidDataException("Retired driver run emitted a late observation.");
        // The external subscription owns only its borrow; stopped composition cannot dispose the original owner.
        await owner.CaptureInformationAsync(timeout.Token);
        Console.WriteLine("Actual driver subscription completed: original native run stopped; direct owner remains usable.");
    }
}
