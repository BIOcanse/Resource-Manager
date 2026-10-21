using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.Metrics;

internal static class WindowsMetricWriteOwnerConsumer
{
    // Actual native discovery/query only. Never invokes a GPU setter.
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var owner = WindowsHardwareMetricOwner.CreateDefault();
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ReadWriteCatalog catalog;
        try { catalog = await core.DiscoverAsync(timeout.Token); }
        catch (InvalidOperationException exception) when (exception.Message == "The required exact GPU topology is unavailable.")
        { Console.WriteLine("Actual NVIDIA write discovery unavailable: " + exception.Message + "; no setter invoked."); return; }
        if (owner.ReadCachedSnapshot() is not null) throw new InvalidOperationException("Discovery fabricated numeric current.");
        var fields = catalog.Fields.Where(field => field.Definition.Name == "gpu_nvidia_requested_power_limit_watts").ToArray();
        foreach (var field in fields)
        {
            var capability = core.Query(field.Field);
            Console.WriteLine($"Actual NVIDIA requested power-limit declaration: device={field.Field.DeviceIdentity}; read={capability.Read.Supported}; write={capability.Write.Supported}; writeDependencies={string.Join(",", capability.Write.Dependencies)}; reason={capability.Write.Reason}; setter=not-invoked.");
        }
        if (fields.Length == 0) Console.WriteLine("Actual NVIDIA requested power-limit declaration unavailable: no matching exact device; setter=not-invoked.");
    }
}
