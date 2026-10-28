using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Services;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class InformationOwnerSubscriptionConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var software = new WindowsInstalledSoftwareInventory();
        var softwareBackend = software.CreateBackend();
        var catalog = await softwareBackend.DiscoverAsync(timeout.Token);
        var size = catalog.Fields.FirstOrDefault(static field => field.Field.FieldName == "software_all_reported_size");
        if (size is null) Console.WriteLine("Actual information subscription: uninstall inventory empty; reported-size value not claimed.");
        else await ObserveAsync("software-size", softwareBackend, software.BorrowSubscriptionCapture(), [size.Field], timeout.Token);
        using var services = new WindowsServiceInformationOwner();
        var serviceBackend = services.CreateBackend();
        catalog = await serviceBackend.DiscoverAsync(timeout.Token);
        var state = catalog.Fields.FirstOrDefault(static field => field.Field.FieldName == "service_all_state_actual");
        if (state is null) Console.WriteLine($"Actual information subscription: SCM scope=Win32; available={services.ReadServices()?.Available}; error={services.ReadServices()?.ErrorCode}; no synthetic values.");
        else await ObserveAsync("service-info", serviceBackend, services.BorrowSubscriptionCapture(),
            catalog.GetFields(state.Field.DeviceIdentity).Select(static field => field.Field).ToImmutableArray(), timeout.Token);
    }
    private static async Task ObserveAsync(string id, IReadWriteBackend backend, IReadWriteCollectionBorrow borrow,
        ImmutableArray<FieldReference> fields, CancellationToken cancellationToken)
    {
        var settings = new SubscriptionRoleSettings(1, 1000, 1, 3000, 1000, 1000);
        var configuration = new NativeSamplingSubscriptionConfiguration
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(), Generation = 1,
            MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 32,
            MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
            DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
            ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
        };
        await using var composition = new NativeFieldSubscriptionComposition(borrow, settings, in configuration,
            failure => Console.WriteLine($"{id}: worker failure={failure.Stage}; {failure.Error.Message}"));
        await composition.StartAsync(cancellationToken);
        await using var stream = composition.SubscribeAsync(id, fields, TimeSpan.FromSeconds(1), cancellationToken).GetAsyncEnumerator();
        if (!await stream.MoveNextAsync()) throw new InvalidDataException("Native information subscription ended without an observation.");
        foreach (var reading in stream.Current)
        {
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, backend.ReadCached(reading.Field));
            if (reading.Status != FieldObservationStatus.Current && reading.Value is not null)
                throw new InvalidDataException("Unavailable information was fabricated as a value.");
            Console.WriteLine($"Actual information subscription: id={id}; identity={reading.Field.DeviceIdentity}; field={reading.Field.FieldName}; status={reading.Status}; integer={reading.Value?.Integer}; same-current=yes; source={reading.SourceId}.");
        }
        await composition.StopAsync();
        if (await stream.MoveNextAsync()) throw new InvalidDataException("Retired information subscription emitted a late value.");
        _ = await backend.CollectAsync(fields, cancellationToken); // Same external owner survives Stop.
        Console.WriteLine($"Actual information subscription completed: id={id}; original native subscription stopped; same external owner direct collection survived; no service/registry writes.");
    }
}
