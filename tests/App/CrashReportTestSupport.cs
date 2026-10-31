using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.CrashReports;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.CrashReports;
using ResourceManager.App.Infrastructure.Notifications;

namespace Resource_Manager_APP.Tests;

// Fakes for the crash-dump feature. Nothing here touches HKLM or the real Application log.
internal static class CrashReportTestSupport
{
    public const string BetaVersion = "1.4.0-beta.20261031.1";
    public const string StableVersion = "1.4.0";

    private static readonly CompiledHostManagerPlan HostPlan = HostManagerTestPlanFactory.CreatePlan();

    public static CrashReportOptions Options { get; } = HostPlan.CrashReports
        ?? throw new InvalidOperationException("The compiled plan has no crash_reports point.");

    public static CrashReportEnvironment Environment(string? root = null, string version = BetaVersion)
        => new(root ?? Path.GetTempPath(), version, "ResourceManager");

    public static CompiledRuntimePlan Plan(AppCrashDumpSettings? settings = null, CrashReportOptions? options = null, long version = 1)
        => CompiledRuntimePlan.Default with
        {
            Version = version,
            ServiceRecovery = new AppSystemIntegrationSettings(false, []) { CrashDumps = settings ?? new AppCrashDumpSettings() },
            HostManager = HostPlan with { CrashReports = options ?? Options }
        };

    public static CrashDumpSettings DumpSettings(FakeRegistry? registry = null, CrashReportEnvironment? environment = null)
        => new(environment ?? Environment(), registry ?? new FakeRegistry(), NullLogger<CrashDumpSettings>.Instance);

    public static (CrashReportOwner Owner, NotificationOwner Notifications) Owner(string root, FixedPlan plans,
        FakeEvents events, ManualTime time, string version = BetaVersion)
    {
        var notifications = NotificationTestSupport.Create(root, persist: false, time: time);
        return (new CrashReportOwner(plans, Environment(root, version), events, notifications,
            new RuntimePersistenceCapabilityPolicy(true), time, NullLogger<CrashReportOwner>.Instance), notifications);
    }

    public static string DumpFolder(string root) => Path.Combine(root, "UserData", Options.FolderName);

    public static string WriteDump(string root, string image, int pid, DateTimeOffset writtenAt, int bytes = 4096)
    {
        Directory.CreateDirectory(DumpFolder(root));
        var path = Path.Combine(DumpFolder(root), $"{image}.{pid}.dmp");
        var content = new byte[bytes];
        new Random(pid).NextBytes(content);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime);
        return path;
    }

    public static CrashEvent ApplicationError(DateTimeOffset at, string image, string module = "ntdll.dll")
        => new(at, 1000, image, module, "0x00000000000a1b2c", "0xc0000005", null, null,
            $"Faulting application name: {image}\nFaulting module name: {module}");

    public static CrashEvent DotNetRuntime(DateTimeOffset at, string image, string stack = "at A.B()\nat C.D()")
        => new(at, 1026, image, null, null, null, "The process was terminated due to an unhandled exception.", stack,
            $"Application: {image}\nStack:\n{stack}");

    public sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH")
                ?? throw new InvalidOperationException("Set RM_REVIEW_SCRATCH to a running registered validation workspace."),
            "crash-fixtures", Guid.NewGuid().ToString("N"));
        public TempRoot() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            // No open resources remain. The registered parent batch owns retention and removes its fixture data.
        }
    }

    public sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public sealed class FixedPlan(CompiledRuntimePlan plan) : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = plan;
        public RuntimePlanPublicationLease AcquirePublicationLease() => RuntimePlanPublicationLease.CreateUntracked(Current, 0);
        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan next)
        {
            Current = next;
            return new(next, 0, []);
        }
    }

    public sealed class FakeRegistry : ICrashDumpRegistry
    {
        public Dictionary<string, CrashDumpRegistryEntry> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<CrashDumpRegistryKey> Writes { get; } = [];
        public List<string> Removals { get; } = [];
        public List<(string Path, bool UsersMayWrite)> Folders { get; } = [];
        public bool FailWrite;
        public int? OverrideDumpCount;

        public IReadOnlyList<CrashDumpRegistryEntry> Read(IReadOnlyList<string> images)
            => images.Select(image => Keys.TryGetValue(image, out var entry) ? entry : new(image, false, false)).ToArray();

        public void Write(CrashDumpRegistryKey key)
        {
            Writes.Add(key);
            if (FailWrite) throw new IOException("registry write denied");
            Keys[key.Image] = new(key.Image, true, true, key.DumpFolder, key.DumpType, key.CustomDumpFlags,
                OverrideDumpCount ?? key.DumpCount);
        }

        public void RemoveOwned(string image)
        {
            Removals.Add(image);
            if (Keys.TryGetValue(image, out var entry) && entry.OwnedByResourceManager) Keys.Remove(image);
        }

        public void PrepareFolder(string path, bool usersMayWrite) => Folders.Add((path, usersMayWrite));
    }

    public sealed class FakeEvents : ICrashEventSource
    {
        public List<CrashEvent> Crashes { get; } = [];
        public List<ServiceLogEntry> ServiceLog { get; } = [];
        public int CrashQueries;

        public IReadOnlyList<CrashEvent> ReadCrashEvents(DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string> images, int max)
        {
            CrashQueries++;
            return Crashes.Where(item => item.TimeCreated >= from && item.TimeCreated < to
                    && images.Contains(item.Image, StringComparer.OrdinalIgnoreCase))
                .OrderBy(static item => item.TimeCreated).Take(max).ToArray();
        }

        public IReadOnlyList<ServiceLogEntry> ReadServiceLog(string source, DateTimeOffset from, DateTimeOffset to, int max)
            => ServiceLog.Where(item => item.TimeCreated >= from && item.TimeCreated < to).Take(max).ToArray();
    }

    // For the ServiceRecoverySettings block tests: records startup discoveries, holds no list.
    public sealed class RecordingOwner : ICrashReportOwner
    {
        public int Discoveries;
        public CrashReportList Read() => new([], "", 0);
        public async IAsyncEnumerable<CrashReportList> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return Read();
        }
        public void DiscoverAtStartup() => Interlocked.Increment(ref Discoveries);
        public CrashReportPackageResult? Package(string id) => null;
        public CrashReportIssueLink? IssueLink(string id) => null;
        public CrashReportAcknowledgement? Acknowledge(string id) => null;
        public bool Delete(string id) => false;
    }
}
