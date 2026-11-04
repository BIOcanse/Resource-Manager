using ResourceManager.App.Application.CrashReports;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.CrashReports;

/// <summary>Owner of the WER LocalDumps state (docs/crash_dumps_2026-10-31.md 写入). Each published plan carries
/// the saved setting and the compiled crash_reports point; on writes the keys of RM's images, ask/off removes the
/// keys RM wrote. Like ServiceRecoverySettings it records the desired-vs-actual state and never retries a failed
/// attempt for an unrelated plan rebuild. Only the installed service touches the registry.</summary>
public sealed class CrashDumpSettings(
    CrashReportEnvironment environment,
    ICrashDumpRegistry registry,
    ILogger<CrashDumpSettings> logger)
{
    private readonly object gate = new();
    private string? attempted;
    private bool skipLogged;
    private CrashDumpDiagnostics? diagnostics;

    // Null until the first plan is applied.
    public CrashDumpDiagnostics? Diagnostics => Volatile.Read(ref diagnostics);

    // skipReason is the installed-service identity check of the caller (ServiceRecoverySettings): null means this
    // process is the installed service and may write HKLM. It is asked only when the desired state changed.
    public void Apply(CompiledRuntimePlan plan, Func<string?> skipReason)
    {
        var options = plan.HostManager.CrashReports;
        if (options is null) return;
        var settings = plan.ServiceRecovery.CrashDumps;
        var (effective, source, channelDefault, channel) = environment.ResolveMode(settings, options);
        var folder = environment.DumpFolder(options);
        var images = options.Images.Select(static image => image.Image).ToArray();
        var desired = effective == AppCrashDumpModes.On ? CreateKeys(settings, options, folder) : [];
        CrashDumpDiagnostics Diagnose(string state, IReadOnlyList<CrashDumpRegistryEntry>? actual = null,
            string? skip = null, string? write = null, string? read = null)
            => new(state, effective, source, channelDefault, channel, folder, images, options.KeepCountMaximum,
                actual, skip, write, read);

        lock (gate)
        {
            var key = string.Join('|', effective, settings.DumpType, KeepCount(settings, options), folder,
                options.MiniDumpCustomFlags, options.UsersMayWriteDumps, string.Join(',', images));
            if (attempted == key)
            {
                // The same desired state: keep the observed result, refresh only the mode facts.
                if (diagnostics is { } current)
                    Volatile.Write(ref diagnostics, current with { EffectiveMode = effective, ModeSource = source });
                return;
            }
            attempted = key;
            string? reason;
            try { reason = skipReason(); }
            catch (Exception error)
            {
                logger.LogError(error, "Could not verify the background service identity for crash dumps.");
                Volatile.Write(ref diagnostics, Diagnose("failed", write: error.Message));
                return;
            }
            if (reason is not null)
            {
                if (!skipLogged)
                {
                    logger.LogInformation("Skipped crash dump configuration: {Reason}", reason);
                    skipLogged = true;
                }
                Volatile.Write(ref diagnostics, Diagnose("skipped", skip: reason));
                return;
            }

            string? writeError = null;
            try
            {
                if (desired.Length > 0)
                {
                    registry.PrepareFolder(folder, options.UsersMayWriteDumps);
                    foreach (var entry in desired) registry.Write(entry);
                }
                else
                {
                    foreach (var image in images) registry.RemoveOwned(image);
                }
            }
            catch (Exception error)
            {
                writeError = error.Message;
                logger.LogError(error, "Could not write the crash dump configuration.");
            }

            IReadOnlyList<CrashDumpRegistryEntry>? actual = null;
            string? readError = null;
            try { actual = registry.Read(images); }
            catch (Exception error)
            {
                readError = error.Message;
                logger.LogError(error, "Could not read the crash dump configuration.");
            }
            var state = writeError is not null || readError is not null ? "failed"
                : Matches(desired, actual!) ? "applied" : "mismatch";
            if (state == "mismatch")
                logger.LogWarning("The observed crash dump configuration differs from the saved preference.");
            Volatile.Write(ref diagnostics, Diagnose(state, actual, write: writeError, read: readError));
        }
    }

    internal static int KeepCount(AppCrashDumpSettings settings, CrashReportOptions options)
        => Math.Clamp(settings.KeepCount, 1, options.KeepCountMaximum);

    // mini: WER custom dump (DumpType 0) with the declared flags; full: DumpType 2.
    internal static CrashDumpRegistryKey[] CreateKeys(AppCrashDumpSettings settings, CrashReportOptions options, string folder)
    {
        var full = settings.DumpType == AppCrashDumpTypes.Full;
        var count = KeepCount(settings, options);
        return options.Images.Select(image => new CrashDumpRegistryKey(image.Image, folder, full ? 2 : 0,
            full ? null : options.MiniDumpCustomFlags, count)).ToArray();
    }

    // on: every image has exactly the desired owned key; ask/off: no owned key remains.
    internal static bool Matches(IReadOnlyList<CrashDumpRegistryKey> desired, IReadOnlyList<CrashDumpRegistryEntry> actual)
    {
        if (desired.Count == 0) return actual.All(static entry => !(entry.Present && entry.OwnedByResourceManager));
        return desired.All(key => actual.Any(entry => entry.Present && entry.OwnedByResourceManager
            && string.Equals(entry.Image, key.Image, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.DumpFolder, key.DumpFolder, StringComparison.OrdinalIgnoreCase)
            && entry.DumpType == key.DumpType && entry.CustomDumpFlags == key.CustomDumpFlags
            && entry.DumpCount == key.DumpCount));
    }
}
