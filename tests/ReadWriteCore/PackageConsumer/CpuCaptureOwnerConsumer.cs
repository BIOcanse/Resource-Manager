using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.Cpu.Snapshots;

internal static class CpuCaptureOwnerConsumer
{
    // Explicit admitted Windows route: the default owner uses the actual SDK OS reader, not a recorded backend.
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var owner = new CpuTopologyCaptureOwner();
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(static _ => new(true, null, [])), new DependencyActions(http, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var catalog = await core.DiscoverAsync(timeout.Token);
        Require(owner.ReadState().Snapshot is null, "raw capture does not manufacture product topology");
        if (catalog.Fields.IsEmpty)
        {
            Console.WriteLine("Actual CPU OS capture returned no canonical fields (empty or unavailable); no available-field claim.");
            return;
        }
        var fields = catalog.Fields.Select(item => item.Field).ToImmutableArray();
        Require(fields.All(field => core.Query(field).Read.Supported && !core.Query(field).Write.Supported), "canonical read/support contract");
        Require(owner.ReadInformation().Devices.SelectMany(device => device.Fields).
            Where(field => field.Definition.Name.Contains("busy", StringComparison.Ordinal)).All(field => field.Value is null),
            "first real usage capture has no fabricated interval");
        var current = owner.ReadState();
        foreach (var field in fields) _ = core.ReadCached(field);
        Require(ReferenceEquals(current, owner.ReadState()), "cached reads retain the original current without capture");
        using var signal = owner.SubscribePublications();
        var notification = signal.WaitAsync(timeout.Token).AsTask();
        var readings = await core.CollectAsync(fields, timeout.Token);
        await notification;
        Require(readings.Length == fields.Length, "actual canonical batch uses the shared capture/current owner");
        Require(owner.ReadState().Snapshot is null, "second raw capture still has no product fallback");
        Require(typeof(CpuTopologyCaptureOwner).Assembly == typeof(ReadWriteCore).Assembly, "independent owner is in the package");
        Console.WriteLine("Actual independent CPU canonical consumer completed; field availability follows real OS results and valid intervals.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}

