using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class AtaSmartInformationSubscriptionConsumer
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var physical = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DeviceId"] = 7, ["FriendlyName"] = "Recorded disk A", ["SerialNumber"] = "serial-A",
            ["UniqueId"] = "unique-A", ["UniqueIdFormat"] = 1, ["Size"] = 100UL, ["BusType"] = 3
        };
        var identity = WindowsStorageOwner.ProjectIdentities([physical]).Single().ExactIdentityKey;
        var requests = new ConcurrentQueue<AtaSmartAttributeInformationRequest>();
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var access = owner.CreateAtaSmartAttributeInformation(
            new(new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal)
            { [identity] = AtaSmartAttributeInformationFields.Normalized | AtaSmartAttributeInformationFields.Raw },
                MaximumDeviceCount: 1),
            (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                requests.Enqueue(request);
                var fields = new List<DeviceInformationField>();
                if ((request.Devices[identity] & AtaSmartAttributeInformationFields.Normalized) != 0)
                {
                    using var document = JsonDocument.Parse("{\"slots\":[{\"attributeId\":9,\"currentNormalized\":88}]}");
                    fields.Add(new(DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeNormalized,
                        document.RootElement.Clone()));
                }
                if ((request.Devices[identity] & AtaSmartAttributeInformationFields.Raw) != 0)
                {
                    using var document = JsonDocument.Parse("{\"slots\":[{\"attributeId\":9,\"rawSixBytes\":\"AQIDBAUG\"}]}");
                    fields.Add(new(DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeRaw,
                        document.RootElement.Clone()));
                }
                return new(null, [new(identity, "physical_disk", fields)]);
            });

        var normalized = new FieldReference(identity, DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeNormalized.Name);
        var raw = new FieldReference(identity, DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeRaw.Name);
        var catalogNormalized = access.Backend.Catalog.Fields.Single(item => item.Field.DeviceIdentity == identity
            && item.Field.FieldName == normalized.FieldName).Field;
        var beforeSubscription = requests.Count;
        var failures = new ConcurrentQueue<string>();
        var settings = new SubscriptionRoleSettings(2, 1_000, 1, 3_000, 1_000, 1_000);
        var configuration = Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(access.BorrowSubscriptionCapture(),
            settings, in configuration, failure => failures.Enqueue($"{failure.Stage}: {failure.Error.Message}"));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = composition.SubscribeAsync("recorded-ata-smart", [catalogNormalized],
            TimeSpan.FromMilliseconds(250), timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token),
                "explicit role-2 subscription published the selected physical-disk field");
            var reading = AssertSingle(stream.Current);
            Require(reading.Status == FieldObservationStatus.Current
                && reading.Value?.Structure?.GetProperty("slots")[0].GetProperty("currentNormalized").GetByte() == 88
                && reading.ObservedAt.HasValue,
                "role-2 returned the recorded normalized ATA value with its observation time");
            var request = requests.Last();
            Require(requests.Count > beforeSubscription && request.MaximumDeviceCount == 1
                && request.Devices.Count == 1 && request.Devices.Single().Key == identity
                && request.Devices.Single().Value == AtaSmartAttributeInformationFields.Normalized,
                "role-2 passed only the selected exact disk and ATA field to the injected source");
            Require(access.Backend.ReadCached(normalized).Value?.Structure?.GetProperty("slots")[0]
                .GetProperty("currentNormalized").GetByte() == 88,
                "subscription output is the original owner's current value");
            await composition.StopAsync();
            Require(!await stream.MoveNextAsync(), "explicit Stop retires the selected role-2 stream");
        }

        var afterStop = requests.Count;
        await access.Backend.CollectAsync([raw], CancellationToken.None);
        Require(requests.Count > afterStop && requests.Last().Devices.Single().Key == identity
            && requests.Last().Devices.Single().Value == AtaSmartAttributeInformationFields.Raw,
            "stopping the borrow leaves the caller-owned source available for explicit direct collection");
        Require(failures.IsEmpty, "recorded ATA role-2 capture completed without worker faults");
        Console.WriteLine("[ATA-SMART-INFORMATION-SUBSCRIPTION-PACKAGE] recorded exact-disk role-2 capture used the original owner permit; no native disk read.");
    }

    private static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
        Generation = 1,
        MaximumSourceCount = 8,
        MaximumItemCount = 8,
        MaximumMembershipCount = 16,
        MaximumDueItemCount = 8,
        MaximumSourceViewCount = 8,
        MaximumExpiredSourceCount = 8,
        DefaultIntervalMilliseconds = 1_000,
        MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3_000,
        MaximumFutureSkewMilliseconds = 1_000
    };

    private static FieldReading AssertSingle(ImmutableArray<FieldReading> readings)
    {
        if (readings.Length != 1) throw new InvalidDataException("Expected one ATA SMART subscription reading.");
        return readings[0];
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException($"ATA SMART subscription consumer failed: {message}."); }
}
