using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.SystemInformation;

internal static class StartupConfigurationConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = new WindowsOperatingSystemInformationOwner(new WindowsPlatformDeclarationScope(), TimeSpan.FromSeconds(2));
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        if (!core.Catalog.Fields.IsEmpty) throw new InvalidDataException("Passive startup owner read configuration.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.Startup, timeout.Token);
        var fields = core.Catalog.Fields.Where(field => field.Field.FieldName == "startup_all_entry_configured").ToArray();
        foreach (var field in fields)
        {
            if (field.Definition.ValueKind != "string" || field.Write.Supported) throw new InvalidDataException("Readonly configured startup payload type mismatch.");
            var reading = core.ReadCached(field.Field);
            if (reading.Status == FieldObservationStatus.Current && reading.Value?.Text is null) throw new InvalidDataException("Current configuration has no original text payload.");
            Console.WriteLine($"Configured startup: source-object={field.Field.DeviceIdentity}; status={reading.Status}; source={reading.SourceId}; original-current=yes; no-execution-result");
        }
        Console.WriteLine($"Startup complete-or-unavailable scope: Run/RunOnce + machine/current-token-SID Startup folders + local boot/logon task XML; rows={fields.Length}; no partial-success inference from empty catalog.");
    }
}
