using System.Globalization;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class OperatingSystemBuildBootSubscriptionConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = new WindowsOperatingSystemInformationOwner(TimeSpan.FromSeconds(2));
        var backend = owner.CreateBackend();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var catalog = await backend.DiscoverAsync(timeout.Token);
        var build = catalog.Fields.Single(field => field.Definition.Name == "os_all_build_declared").Field;
        var boot = catalog.Fields.Single(field => field.Definition.Name == "os_all_boot_time_actual").Field;
        var uptime = catalog.Fields.Single(field => field.Definition.Name == "os_all_uptime_actual").Field;
        var originalUptime = backend.ReadCached(uptime);
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
            failure => Console.WriteLine($"OS declaration worker failure: {failure.Stage}; {failure.Error.Message}"), TimeSpan.FromMilliseconds(250));
        await composition.StartAsync(timeout.Token);
        await using var stream = composition.SubscribeAsync("os-build-boot", [build, boot], TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        if (!await stream.MoveNextAsync()) throw new InvalidDataException("OS native subscription ended without observation.");
        foreach (var reading in stream.Current)
        {
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, backend.ReadCached(reading.Field));
            if (reading.Status == FieldObservationStatus.Current)
            {
                if (reading.Field == build && (string.IsNullOrEmpty(reading.Value?.Text) || !reading.Value.Text.All(static c => c >= '0' && c <= '9')))
                    throw new InvalidDataException("Current build is not a complete declared BuildNumber.");
                if (reading.Field == boot && (!DateTimeOffset.TryParseExact(reading.Value?.Text, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var utc) || utc.Offset != TimeSpan.Zero))
                    throw new InvalidDataException("Current boot timestamp has no exact reported UTC value.");
            }
            else if (reading.Value is not null) throw new InvalidDataException("Unavailable OS fact has a fabricated value.");
            Console.WriteLine($"Actual OS declaration subscription: field={reading.Field.FieldName}; text={reading.Value?.Text}; status={reading.Status}; same-current=yes.");
        }
        if (backend.ReadCached(uptime) != originalUptime) throw new InvalidDataException("Declaration subscription changed unselected uptime/time.");
        await composition.StopAsync();
        if (await stream.MoveNextAsync()) throw new InvalidDataException("Retired OS run emitted a late value.");
        _ = await backend.CollectAsync([build], timeout.Token);
        Console.WriteLine("Actual OS declarations subscription stopped: external original owner remains explicitly collectible; no boot estimate/session claim.");
    }
}
