using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.SystemInformation;

internal static class OperatingSystemSessionConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = new WindowsOperatingSystemInformationOwner();
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        if (!core.Catalog.Fields.IsEmpty) throw new InvalidDataException("Passive session owner captured native information.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.SessionId, timeout.Token);
        var fields = core.Catalog.Fields.Where(field => field.Field.FieldName == "os_all_session_id_actual").ToArray();
        foreach (var descriptor in fields)
        {
            var field = descriptor.Field;
            if (descriptor.Definition.ValueKind != "unsigned64" || descriptor.Definition.Unit != "session-id" || descriptor.Write.Supported)
                throw new InvalidDataException("Session declaration lost its exact readonly type.");
            var reading = (await core.CollectAsync([field], timeout.Token)).Single();
            if (reading.Status == FieldObservationStatus.Current && (reading.Value?.Integer is not { } integer || !uint.TryParse(integer, out _)))
                throw new InvalidDataException("WTS session identifier was not retained as an exact unsigned integer.");
            if (core.Catalog.Fields.Any(candidate => candidate.Field == field) && core.ReadCached(field) != reading)
                throw new InvalidDataException("Session consumer did not use original current.");
            Console.WriteLine($"WTS login: identity={field.DeviceIdentity}; id={reading.Value?.Integer}; status={reading.Status}; source={reading.SourceId}; same-current=yes");
        }
        Console.WriteLine($"WTS login enumeration: rows={fields.Length}; local all-queryable-user-logins; empty/unavailable is not active-console-only.");
    }
}
