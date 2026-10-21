using System.Text.Json;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

internal static class AtaSmartInformationPublicationConsumer
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
        var requests = new List<AtaSmartAttributeInformationRequest>();
        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var access = owner.CreateAtaSmartAttributeInformation(
            new(new Dictionary<string, AtaSmartAttributeInformationFields>(StringComparer.Ordinal)
            { [identity] = AtaSmartAttributeInformationFields.Raw }, MaximumDeviceCount: 1),
            (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                requests.Add(request);
                using var document = JsonDocument.Parse("{\"slots\":[{\"attributeId\":9,\"rawSixBytes\":\"AQIDBAUG\"}]}");
                var field = new DeviceInformationField(DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeRaw,
                    document.RootElement.Clone());
                return new(null, [new(identity, "physical_disk", [field])]);
            });

        var raw = new FieldReference(identity, DeviceInformationFieldRegistry.StorageAllAtaSmartAttributeRaw.Name);
        Require(access.Backend.Catalog.Fields.Length == 1 && requests.Count == 0,
            "public catalog is passive and scoped to the exact selected disk/field");
        Require(access.Backend.ReadCached(raw).Status == FieldObservationStatus.Unavailable && requests.Count == 0,
            "public cached read does not invoke the producer");

        await access.Backend.DiscoverAsync(CancellationToken.None);
        Require(requests.Count == 1 && requests[0].Devices.Count == 1
            && requests[0].Devices.Single().Key == identity
            && requests[0].Devices.Single().Value == AtaSmartAttributeInformationFields.Raw
            && requests[0].MaximumDeviceCount == 1,
            "explicit discovery passes the exact identity, typed field, and supplied finite bound");
        var reading = (await access.Backend.CollectAsync([raw], CancellationToken.None)).Single();
        Require(reading.Status == FieldObservationStatus.Current
            && reading.Value?.Structure?.GetProperty("slots")[0].GetProperty("rawSixBytes").GetString() == "AQIDBAUG"
            && requests[^1].Devices.Count == 1 && requests[^1].Devices.Single().Key == identity
            && requests[^1].Devices.Single().Value == AtaSmartAttributeInformationFields.Raw,
            "public selected collection returns the recorded structured raw table for the exact disk only");
        Console.WriteLine("[ATA-SMART-PUBLICATION-PACKAGE] exact selected ATA values use the original topology current and caller-provided source; no native read.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException($"ATA SMART publication consumer failed: {message}."); }
}
