using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.Adapter.LocalResources;
using ResourceManager.Adapter.NativeLedger;
using ResourceManager.Adapter.NativeScheduling;

if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || args[1].Length != 64
    || !args[1].All(Uri.IsHexDigit))
{
    Console.Error.WriteLine("Usage: LegacyAdapterInitializationProbe <absolute AdapterNativeOutput> <expected SHA256>");
    return 2;
}
if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    Console.Error.WriteLine("The legacy Adapter probe requires a Windows x64 process.");
    return 2;
}

// This finite process is also required to run within the caller's bounded ExecutionJob.
NativeErrorPolicy.SetErrorMode(0x0001 | 0x0002);
using var watchdog = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
try
{
    var input = Path.GetFullPath(args[0]);
    var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ResourceManager.Adapter.Native.dll"));
    var expected = args[1].ToUpperInvariant();
    // Hold both exact files read-only for the entire lifetime. The SDK's original
    // resolver loads only the canonical candidate path in this probe's output.
    using var inputFile = File.OpenRead(input);
    using var candidateFile = File.OpenRead(candidate);
    RequireHash(inputFile, expected, "AdapterNativeOutput");
    RequireHash(candidateFile, expected, "probe output native candidate");
    var sessions = new List<SessionResult>();

    using (var scheduler = new NativeResourceSchedulerSession(1, 1, 1, 0, SchedulerConfiguration.Create(), 1))
    {
        if (scheduler.TargetCapacity != 1 || scheduler.ResidentBytes == 0)
            throw new InvalidOperationException("Scheduler initialization returned invalid owned capacity.");
        // No target, PID, plan, reservation or action is submitted to this session.
    }
    sessions.Add(new("resource-scheduler", ResourceSchedulerProtocol.Version, true, true));

    using (var ledger = new NativeAdapterResourceLedgerSession(new(1, 1,
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 64, 3, 4)))
    {
        if (ledger.Capacity != 1)
            throw new InvalidOperationException("Private-resource initialization returned invalid owned capacity.");
    }
    sessions.Add(new("private-resource-ledger", AdapterPrivateResourceLedgerProtocol.Version, true, true));

    // This covers the third ABI exported by the multi-ABI DLL. It does not
    // replace or exercise the independently migrated Ledger dev.2 product owner.
    using (var local = new NativeLocalResourceManagerSession(LocalResourceManagerConfiguration.CreateDefault(1, 1)))
    {
        if (local.StartsBackgroundWorker || local.ResidentBytes == 0)
            throw new InvalidOperationException("Local-resource initialization must own memory without a worker.");
    }
    sessions.Add(new("local-resource-manager", LocalResourceManagerProtocol.Version, true, true));

    RequireHash(inputFile, expected, "AdapterNativeOutput after disposal");
    RequireHash(candidateFile, expected, "probe output native candidate after disposal");
    var managed = new[] { typeof(NativeResourceSchedulerSession).Assembly.Location,
        typeof(ResourceManager.Adapter.AdapterResourceProtocol).Assembly.Location,
        typeof(SessionResult).Assembly.Location }.Select(path =>
        {
            using var file = File.OpenRead(path);
            return new { path, sha256 = Convert.ToHexString(SHA256.HashData(file)) };
        }).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "passed",
        adapterNativeOutput = input,
        loadedCandidate = candidate,
        nativeSha256 = expected,
        managedAssemblies = managed,
        sessions,
        scope = "owned-session-contract-initialize-dispose-only"
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}
catch (Exception failure)
{
    Console.Error.WriteLine(failure);
    return 1;
}

static void RequireHash(FileStream file, string expected, string description)
{
    file.Position = 0;
    var actual = Convert.ToHexString(SHA256.HashData(file));
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidDataException($"{description} SHA256 mismatch: expected={expected}, actual={actual}.");
}

sealed record SessionResult(string Module, uint ManagedProtocolVersion, bool Initialized, bool Disposed);

static class NativeErrorPolicy
{
    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);
}
