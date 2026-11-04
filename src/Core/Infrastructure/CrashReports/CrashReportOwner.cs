using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ResourceManager.App.Application.CrashReports;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.CrashReports;

/// <summary>The single owner of the crash-report list (docs/crash_dumps_2026-10-31.md 发现/接口/已读状态).
/// A scan lists the dump folder (bounded), registers every dump no record references and every Application log crash
/// of an RM image no record covers, prunes by the keep count, and publishes the whole list. The service runs one scan
/// at start (the only one that posts the crash-report notification); every read and new subscription rescans first,
/// so a window-process crash appears when the window next opens. All values come from the compiled
/// service_recovery.crash_reports point and the saved setting.</summary>
public sealed class CrashReportOwner(
    IRuntimePlanProvider plans,
    CrashReportEnvironment environment,
    ICrashEventSource events,
    INotificationOwner notifications,
    RuntimePersistenceCapabilityPolicy persistence,
    TimeProvider time,
    ILogger<CrashReportOwner> logger) : ICrashReportOwner
{
    private const int StoreVersion = 1;
    private static readonly JsonSerializerOptions StoreJsonOptions = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, RespectRequiredConstructorParameters = true };

    internal sealed record VersionSeen(string Version, DateTimeOffset FirstSeen);

    internal sealed record StoredCrashReports(int Version, DateTimeOffset? ScannedUntil,
        IReadOnlyList<VersionSeen> Versions, IReadOnlyList<CrashReport> Reports);

    private readonly object gate = new();
    private readonly HashSet<Channel<CrashReportList>> readers = [];
    private bool restored;
    private DateTimeOffset? scannedUntil;
    private List<VersionSeen> versions = [];
    private List<CrashReport> reports = [];

    private CrashReportOptions Options => plans.Current.HostManager.CrashReports
        ?? throw new InvalidOperationException("The crash report freedom point is unavailable.");

    private string ReportsFolder(CrashReportOptions options)
        => Path.GetFullPath(Path.Combine(environment.PackageRoot, "UserData", options.ReportsFolderName));

    private string StorePath(CrashReportOptions options) => Path.Combine(ReportsFolder(options), "crash-reports.json");

    private string PackageFolder(CrashReportOptions options) => Path.Combine(ReportsFolder(options), "packages");

    public CrashReportList Read()
    {
        lock (gate)
        {
            Scan(out _);
            return Snapshot();
        }
    }

    public async IAsyncEnumerable<CrashReportList> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Every frame is the whole list, so a slow reader only ever needs the newest one.
        var channel = Channel.CreateBounded<CrashReportList>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        lock (gate)
        {
            if (readers.Count >= Options.SubscriberCapacity) throw new InvalidOperationException("Crash report subscriber capacity exceeded.");
            Scan(out _);
            channel.Writer.TryWrite(Snapshot());
            readers.Add(channel);
        }
        try { await foreach (var list in channel.Reader.ReadAllAsync(cancellationToken)) yield return list; }
        finally { lock (gate) readers.Remove(channel); }
    }

    public void DiscoverAtStartup()
    {
        CrashReport? newest;
        int added;
        lock (gate)
        {
            added = Scan(out newest);
        }
        if (added == 0 || newest is null) return;
        try
        {
            notifications.Post(NotificationKinds.CrashReport.Name, NotificationFacts.Of(
                new CrashReportNotificationFacts(added, newest.Id, newest.Program, newest.Role)));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            logger.LogError(error, "Could not post the crash report notification.");
        }
    }

    public CrashReportPackageResult? Package(string id)
    {
        lock (gate)
        {
            RestoreOnce();
            var index = reports.FindIndex(report => report.Id == id);
            if (index < 0) return null;
            var options = Options;
            var report = reports[index];
            var folder = PackageFolder(options);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, report.Id + ".zip");
            var from = report.OccurredAt.AddSeconds(-options.ServiceLogBeforeSeconds);
            var to = report.OccurredAt.AddSeconds(options.ServiceLogAfterSeconds);
            var log = SafeRead(() => events.ReadServiceLog(environment.ServiceLogSource, from, to, options.ServiceLogMaxEntries), []);
            var window = TimeSpan.FromSeconds(options.EventMatchWindowSeconds);
            var crashEvents = SafeRead(() => events.ReadCrashEvents(report.OccurredAt - window, report.OccurredAt + window,
                [report.Program], options.EventQueryMaxEvents), []);
            var size = CrashReportPackageWriter.Write(path, report, environment.BuildVersion, crashEvents, log);
            var package = new CrashReportPackage(path, size, time.GetUtcNow(), size > options.IssueAttachmentLimitBytes);
            report = report with { Package = package };
            reports[index] = report;
            Commit();
            return new(report, package, package.ExceedsIssueLimit ? CrashReportPackageReasons.ExceedsIssueAttachmentLimit : null);
        }
    }

    public CrashReportIssueLink? IssueLink(string id)
    {
        lock (gate)
        {
            RestoreOnce();
            var index = reports.FindIndex(report => report.Id == id);
            if (index < 0) return null;
            var options = Options;
            var url = CrashIssueLink.Build(reports[index], options);
            var report = reports[index] with { Reported = true };
            reports[index] = report;
            Commit();
            return new(url, report);
        }
    }

    public CrashReportAcknowledgement? Acknowledge(string id)
    {
        lock (gate)
        {
            RestoreOnce();
            var index = reports.FindIndex(report => report.Id == id);
            if (index < 0) return null;
            var report = reports[index];
            if (!report.Acknowledged)
            {
                report = report with { Acknowledged = true };
                reports[index] = report;
                Commit();
            }
            return new(report);
        }
    }

    public bool Delete(string id)
    {
        lock (gate)
        {
            RestoreOnce();
            var index = reports.FindIndex(report => report.Id == id);
            if (index < 0) return false;
            DeleteFiles(reports[index], Options);
            reports.RemoveAt(index);
            Commit();
            return true;
        }
    }

    // Caller holds gate. Returns how many records this scan registered and the newest of them.
    private int Scan(out CrashReport? newest)
    {
        newest = null;
        var options = plans.Current.HostManager.CrashReports;
        if (options is null) return 0;
        RestoreOnce();
        var now = time.GetUtcNow();
        var changed = false;
        if (versions.Count == 0 || versions[^1].Version != environment.BuildVersion)
        {
            versions.Add(new(environment.BuildVersion, now));
            if (versions.Count > options.VersionHistoryCapacity) versions.RemoveRange(0, versions.Count - options.VersionHistoryCapacity);
            changed = true;
        }

        var settings = plans.Current.ServiceRecovery.CrashDumps;
        var dumpsOn = environment.ResolveMode(settings, options).Effective == AppCrashDumpModes.On;
        var images = options.Images.Select(static image => image.Image).ToArray();
        var window = TimeSpan.FromSeconds(options.EventMatchWindowSeconds);
        var added = new List<CrashReport>();

        // Dumps no record references, at most dump_scan_max_entries files of the top folder.
        foreach (var dump in ListDumps(environment.DumpFolder(options), options.DumpScanMaxEntries))
        {
            if (reports.Any(report => report.Dump is { } known && PathEquals(known.Path, dump.Path)
                    && report.OccurredAt == dump.WrittenAt)) continue;
            // A removed record must not be rediscovered from an old file that could not be deleted.
            if (scannedUntil is { } watermark && dump.WrittenAt < watermark) continue;
            var image = CrashDumpFileName.ImageOf(Path.GetFileName(dump.Path));
            if (image is null || options.RoleOf(image) is not { } role) continue;
            var near = SafeRead(() => events.ReadCrashEvents(dump.WrittenAt - window, dump.WrittenAt + window,
                [image], options.EventQueryMaxEvents), []);
            var fault = CrashFaultSummary.From(Nearest(near, dump.WrittenAt, options), options);
            added.Add(Create(dump.WrittenAt, image, role, CrashReportDumpStatuses.Saved,
                new CrashReportDump(dump.Path, dump.SizeBytes), fault));
        }

        // Application log crashes no record covers. The very first scan looks back first_scan_event_lookback_seconds.
        var since = scannedUntil ?? now.AddSeconds(-options.FirstScanEventLookbackSeconds);
        var found = SafeRead(() => events.ReadCrashEvents(since - window, now, images, options.EventQueryMaxEvents), []);
        foreach (var crash in CrashFaultSummary.Group(found, TimeSpan.FromSeconds(options.EventMergeWindowSeconds)))
        {
            var at = crash[0].TimeCreated;
            if (at < since) continue;
            if (reports.Concat(added).Any(report => string.Equals(report.Program, crash[0].Image, StringComparison.OrdinalIgnoreCase)
                    && (report.OccurredAt - at).Duration() <= window)) continue;
            if (options.RoleOf(crash[0].Image) is not { } role) continue;
            // With dumps on, a crash without a dump means the dump is missing; otherwise dumps were not enabled.
            added.Add(Create(at, crash[0].Image, role,
                dumpsOn ? CrashReportDumpStatuses.Missing : CrashReportDumpStatuses.NotEnabled, null,
                CrashFaultSummary.From(crash, options)));
        }
        scannedUntil = now;
        changed |= added.Count > 0;
        reports.AddRange(added);

        // A recorded dump that is gone is missing; the record keeps its old path and size.
        for (var index = 0; index < reports.Count; index++)
        {
            if (reports[index] is { DumpStatus: CrashReportDumpStatuses.Saved, Dump: { } dump } report
                && !DumpMatches(report, dump))
            {
                reports[index] = report with { DumpStatus = CrashReportDumpStatuses.Missing };
                changed = true;
            }
        }
        changed |= Prune(options, CrashDumpSettings.KeepCount(settings, options));
        if (changed) Commit();
        // The advanced scan mark keeps a deleted record's crash from being found again; an empty range needs no write.
        else if (found.Count > 0) Persist(options);
        newest = added.OrderByDescending(static report => report.OccurredAt).FirstOrDefault();
        return added.Count;
    }

    // Keeps the newest keepCount records per program, then the newest record_capacity overall.
    private bool Prune(CrashReportOptions options, int keepCount)
    {
        var ordered = reports.OrderByDescending(static report => report.OccurredAt).ToList();
        var kept = new List<CrashReport>(ordered.Count);
        var perProgram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var removed = false;
        foreach (var report in ordered)
        {
            var count = perProgram.GetValueOrDefault(report.Program);
            if (count >= keepCount || kept.Count >= options.RecordCapacity)
            {
                DeleteFiles(report, options);
                removed = true;
                continue;
            }
            perProgram[report.Program] = count + 1;
            kept.Add(report);
        }
        reports = kept;
        return removed;
    }

    private CrashReport Create(DateTimeOffset at, string image, string role, string status, CrashReportDump? dump,
        CrashReportFault? fault)
    {
        // Several images' instances may crash in the same second, including within this scan's added list.
        var id = $"{at.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{image}-{Guid.NewGuid():N}";
        var version = versions.LastOrDefault(seen => seen.FirstSeen <= at)?.Version;
        return new(id, at, image, role, version, status, dump, fault, null, false, false);
    }

    private static IEnumerable<CrashEvent> Nearest(IReadOnlyList<CrashEvent> near, DateTimeOffset at, CrashReportOptions options)
    {
        var groups = CrashFaultSummary.Group(near, TimeSpan.FromSeconds(options.EventMergeWindowSeconds));
        return groups.Count == 0 ? [] : groups.MinBy(group => (group[0].TimeCreated - at).Duration())!;
    }

    private static IEnumerable<(string Path, long SizeBytes, DateTimeOffset WrittenAt)> ListDumps(string folder, int max)
    {
        var result = new List<(string, long, DateTimeOffset)>();
        try
        {
            var scanned = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.TopDirectoryOnly))
            {
                if (scanned++ >= max) break;
                var info = new FileInfo(path);
                if (!string.Equals(info.Extension, ".dmp", StringComparison.OrdinalIgnoreCase)
                    || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
                result.Add((info.FullName, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }
        catch (Exception error) when (error is DirectoryNotFoundException or IOException or UnauthorizedAccessException) { }
        return result;
    }

    private void DeleteFiles(CrashReport report, CrashReportOptions options)
    {
        if (report.Dump is { } dump && IsUnder(dump.Path, environment.DumpFolder(options)) && DumpMatches(report, dump))
            TryDelete(dump.Path);
        if (report.Package is { } package && IsUnder(package.Path, PackageFolder(options))) TryDelete(package.Path);
    }

    private static bool DumpMatches(CrashReport report, CrashReportDump dump)
    {
        var file = new FileInfo(dump.Path);
        return file.Exists && file.LastWriteTimeUtc == report.OccurredAt.UtcDateTime;
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Could not delete crash report file {Path}.", path);
        }
    }

    private static bool IsUnder(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && full.IndexOf(Path.DirectorySeparatorChar, root.Length) < 0;
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private T SafeRead<T>(Func<T> read, T empty)
    {
        try { return read(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning(error, "Could not read the Application log for crash reports.");
            return empty;
        }
    }

    // Caller holds gate.
    private CrashReportList Snapshot()
    {
        // Before the first plan there is no folder or limit to name; the list is then necessarily empty too.
        if (plans.Current.HostManager.CrashReports is not { } options) return new([], "", 0);
        return new(reports.OrderByDescending(static report => report.OccurredAt).ToArray(),
            environment.DumpFolder(options), options.IssueAttachmentLimitBytes);
    }

    // Caller holds gate: persist, then publish the whole list.
    private void Commit()
    {
        Persist(plans.Current.HostManager.CrashReports);
        var list = Snapshot();
        foreach (var reader in readers) reader.Writer.TryWrite(list);
    }

    private void Persist(CrashReportOptions? options)
    {
        if (options is null || !persistence.MutablePersistenceEnabled) return;
        var path = StorePath(options);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new StoredCrashReports(StoreVersion, scannedUntil, versions, reports), StoreJsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, path);
            }
            finally { WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogError(error, "Crash report owner could not write {Path}.", path);
        }
    }

    // Caller holds gate.
    private void RestoreOnce()
    {
        if (restored) return;
        restored = true;
        if (!persistence.MutablePersistenceEnabled || plans.Current.HostManager.CrashReports is not { } options) return;
        var path = StorePath(options);
        try
        {
            using var stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(path);
            var stored = JsonSerializer.Deserialize<StoredCrashReports>(stream, StoreJsonOptions);
            if (stored?.Version != StoreVersion || stored.Reports is null || stored.Versions is null
                || stored.Reports.Any(static report => report is null || string.IsNullOrWhiteSpace(report.Id))
                || stored.Reports.Select(static report => report.Id).Distinct(StringComparer.Ordinal).Count() != stored.Reports.Count)
                throw new InvalidDataException("The stored crash reports are incomplete or inconsistent.");
            reports = stored.Reports.ToList();
            versions = stored.Versions.ToList();
            scannedUntil = stored.ScannedUntil;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(error, "Crash report owner could not restore {Path}.", path);
        }
    }
}
