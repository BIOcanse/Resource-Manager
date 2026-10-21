using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.SystemInformation;

internal static class PlatformDeclarationsConsumer
{
    // Root may wire this exact public-package consumer into its maintained harness.
    // Calling it explicitly enumerates real Windows providers; it is not a fixture.
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = new WindowsOperatingSystemInformationOwner(new WindowsPlatformDeclarationScope(), TimeSpan.FromSeconds(2));
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        if (!core.Catalog.Fields.IsEmpty) throw new InvalidDataException("Passive declaration owner captured facts.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var catalog = await core.DiscoverAsync(timeout.Token);
        string[] names = ["os_all_activation_state_reported", "environment_all_variable_configured", "startup_all_entry_configured", "software_all_package_family_declared"];
        foreach (var name in names)
        {
            var fields = catalog.Fields.Where(field => field.Field.FieldName == name).ToArray();
            var results = await core.CollectAsync(fields.Select(static field => field.Field).ToImmutableArray(), timeout.Token);
            foreach (var descriptor in fields)
            {
                var field = descriptor.Field; var original = core.ReadCached(field);
                if (!descriptor.Read.Supported || descriptor.Write.Supported) throw new InvalidDataException("Readonly declaration capability mismatch.");
                var result = results.Single(reading => reading.Field == field);
                // Enumeration can retire a package or registry entry during capture.
                if (core.Catalog.Fields.Any(candidate => candidate.Field == field) && core.ReadCached(field) != result)
                    throw new InvalidDataException("Package consumer diverged from original current.");
                if (result.Status == FieldObservationStatus.Current && name == "os_all_activation_state_reported"
                    && (result.Value?.Integer is null || result.Value.Boolean is not null))
                    throw new InvalidDataException("Licensing row status was collapsed into a boolean.");
                if (result.Status == FieldObservationStatus.Unavailable && result.Value is not null)
                    throw new InvalidDataException("Unavailable declaration fabricated a value.");
                Console.WriteLine($"Platform declaration: object={field.DeviceIdentity}; field={name}; status={result.Status}; source={result.SourceId}; first-observed={original.ObservedAt}");
            }
            Console.WriteLine($"Platform declaration scope: field={name}; explicitly enumerated rows={fields.Length}; empty/unavailable is not an aggregate negative assertion.");
        }
    }
}
