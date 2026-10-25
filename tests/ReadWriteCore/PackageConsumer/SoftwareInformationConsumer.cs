using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;

internal static class SoftwareInformationConsumer
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var inventory = new WindowsInstalledSoftwareInventory();
        if (typeof(WindowsInstalledSoftwareInventory).Assembly != typeof(ReadWriteCore).Assembly)
            throw new InvalidDataException("Software collection owner is not SDK-owned.");
        var backend = inventory.CreateBackend();
        var catalog = await backend.DiscoverAsync(default);
        var entries = await inventory.GetInstalledSoftwareAsync(default);
        if (catalog.Fields.Length != entries.Count * 4) throw new InvalidDataException("Software declaration projection omitted fields.");
        foreach (var capability in catalog.Fields)
        {
            var value = backend.ReadCached(capability.Field);
            if (capability.Write.Supported || capability.Read.Dependencies.Length != 0
                || value.SourceId != "windows.uninstall-registry") throw new InvalidDataException("Software source/dependency mismatch.");
            if (capability.Field.FieldName == "software_all_reported_size")
            {
                var entry = entries.Single(entry => capability.Field.DeviceIdentity == "windows-uninstall|" + entry.RegistryPath);
                MetricInteger? expected = entry.ReportedSizeBytes is { } size ? MetricInteger.FromUnsigned128(size) : null;
                if (value.Value?.Integer != expected || value.Status != (expected is null
                    ? FieldObservationStatus.Unavailable : FieldObservationStatus.Current))
                    throw new InvalidDataException("Registry EstimatedSize bytes differ from the same inventory publication.");
            }
        }
        Console.WriteLine($"Software information package consumer passed: current-account HKCU/HKLM 32/64 inventory registrations {entries.Count}, fields {catalog.Fields.Length}; original filtering, registry declaration values and same SDK cache; no registry writes.");
    }
}


