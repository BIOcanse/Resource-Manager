using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.Services;

internal static class ServiceInformationConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var owner = new WindowsServiceInformationOwner();
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var catalog = await core.DiscoverAsync(timeout.Token);
        var facts = owner.ReadServices();
        if (facts?.Available != true)
        {
            Console.WriteLine($"Actual SCM information unavailable: error={facts?.ErrorCode}; no service-value claim.");
            return;
        }
        Require(owner.Scope == WindowsServiceScope.Win32, "Default original Win32 scope.");
        Require(catalog.Fields.Length == facts.Services.Count * 3, "Stable stopped/PID0 service directory has all three fields.");
        foreach (var capability in catalog.Fields)
        {
            var queried = core.Query(capability.Field);
            Require(queried.Read.Supported && queried.Read.Dependencies.IsEmpty && !queried.Write.Supported,
                "SCM built-in read contract and no service write binding.");
            if (capability.Field.FieldName == "service_all_start_type_configured")
                Require(core.ReadCached(capability.Field).Value is null, "Discovery does not query configuration.");
        }
        var chosen = facts.Services.FirstOrDefault();
        if (chosen is null)
        {
            Console.WriteLine("Actual SCM inventory successfully empty; scope=Win32; no synthetic service values.");
            return;
        }
        var identity = "windows-service|" + chosen.ServiceName.ToLowerInvariant();
        var fields = catalog.GetFields(identity).Select(static capability => capability.Field).ToImmutableArray();
        var readings = await core.CollectAsync(fields, timeout.Token);
        foreach (var reading in readings)
        {
            Require(core.ReadCached(reading.Field) == reading, "Direct API shares the same published SCM current.");
            Require(reading.Status == FieldObservationStatus.Current ? reading.Value?.Integer is not null : reading.Value is null,
                "Successful enum zero and failed/unavailable query stay distinct.");
            Console.WriteLine($"Actual SCM field: identity={identity}; field={reading.Field.FieldName}; status={reading.Status}; integer={reading.Value?.Integer}; scope=Win32; same-current=yes.");
        }
        var present = owner.ReadServices()!.Services.FirstOrDefault(service => string.Equals(service.ServiceName, chosen.ServiceName, StringComparison.OrdinalIgnoreCase));
        if (present is not null)
        {
            var pid = readings.Single(static reading => reading.Field.FieldName == "service_all_process_pid_actual");
            Require(pid.Value?.Integer == present.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "PID is current SCM report, including zero, never PID+birth instance identity.");
        }
        Console.WriteLine("SCM information consumer completed: actual Win32 enum and selected read-only config; driver scope explicit-only; no service control or writes.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
