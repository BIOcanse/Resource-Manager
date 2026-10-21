using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.SystemInformation;

internal static class OperatingSystemUptimeConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = new WindowsOperatingSystemInformationOwner();
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        if (!core.Catalog.Fields.IsEmpty) throw new InvalidDataException("Passive constructor published uptime.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var catalog = await core.DiscoverAsync(timeout.Token);
        var field = catalog.Fields.Single(field => field.Definition.Name == "os_all_uptime_actual");
        if (field.Field.DeviceIdentity != WindowsOperatingSystemInformationOwner.MachineIdentity
            || field.Definition.Name != "os_all_uptime_actual" || field.Definition.ValueKind != "unsigned64"
            || field.Definition.Unit != "s" || !core.Query(field.Field).Read.Supported
            || !field.Read.Dependencies.IsEmpty || field.Write.Supported)
            throw new InvalidDataException("Machine uptime canonical contract mismatch.");
        var observed = (await core.CollectAsync([field.Field], timeout.Token)).Single();
        if (observed.Status != FieldObservationStatus.Current)
        {
            if (observed.Value is not null) throw new InvalidDataException("Unavailable uptime is fabricated as a value.");
            Console.WriteLine($"Actual OS uptime unavailable: status={observed.Status}; no zero/current claim.");
            return;
        }
        if (!ulong.TryParse(observed.Value?.Integer, out var seconds) || observed.Value!.Number is not null
            || !observed.ObservedAt.HasValue || core.ReadCached(field.Field) != observed)
            throw new InvalidDataException("Actual uptime does not share lossless same current.");
        Console.WriteLine($"Actual OS uptime: integer={seconds}; unit=s; status={observed.Status}; source={observed.SourceId}; same-current=yes; scope=machine; exact-boot/session not claimed.");
    }
}

