using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Hosting;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    private static async Task<int> RunGpuFilterOnlyAsync(string[] args)
    {
        if (args.Length != 8 || args[0] != "--registered-root" || args[2] != "--runtime-root"
            || args[4] != "--artifact-manifest" || args[6] != "--source-manifest")
            throw new ArgumentException("--gpu-filter-only --registered-root <registered task> --runtime-root <new child> --artifact-manifest <copy manifest> --source-manifest <bound sources>");
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Use the existing elevated control session; this probe never elevates itself.");
        var registered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
        var root = Path.GetFullPath(args[3]);
        var image = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (!registered.StartsWith("F:\\", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(registered, ".generated-workspace.json"))
            || !root.StartsWith(registered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !image.StartsWith(registered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(image + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
            throw new InvalidDataException("Use separate copied image and fresh runtime children of the registered F: build.");
        var artifacts = CopiedArtifact.Verify(args[5], args[7]);
        WindowsNativeLaunchErrorPolicy.InitializeForCurrentProcess();
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "evidence");
        Directory.CreateDirectory(evidence);
        var provider = GpuAllocationCollector.Provider;
        var allowed = (int[])typeof(GpuAllocationCollector).GetField("EventIds", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var cases = new List<object>();
        var names = new List<string>();
        var passed = true;
        int[]? controlNeededRundownIds = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var variants = new[] { (Name: "filtered-no-capture", Filter: true, Capture: false, Reenable: false),
            (Name: "filtered-capture", Filter: true, Capture: true, Reenable: false),
            (Name: "unfiltered-baseline", Filter: false, Capture: false, Reenable: false),
            (Name: "filtered-capture-reenabled", Filter: true, Capture: true, Reenable: true) };
        foreach (var variant in variants)
        {
            // Fresh exact-name sessions, no app host, service, subscription pipe,
            // orphan reconciler, driver or hardware-control boundary is created.
            var name = $"ResourceManager.GpuFilterAcceptance.{Environment.ProcessId}.{Guid.NewGuid():N}";
            names.Add(name);
            var measurement = new ActualUseGpuMeasurement(provider, allowed);
            measurement.ReadRundownMetadata();
            using var observed = GpuAllocationObservation.Subscribe(name, measurement);
            TraceEventSession? native = null;
            TraceEventGpuAllocationSession? session = null;
            Exception? failure = null;
            KernelEtwSessionStopResult? release = null;
            var samples = new List<object>();
            var started = DateTimeOffset.UtcNow;
            var seconds = 0d;
            try
            {
                if (QueryOwnedKernel(name).Exists) throw new InvalidDataException("The fresh private session already exists.");
                native = new TraceEventSession(name);
                native.StopOnDispose = true;
                native.BufferSizeMB = 16;
                native.EnableProviderTimeoutMSec = 2000;
                // No host logger here; fixed-layout rejections still show in the drop counters.
                session = new TraceEventGpuAllocationSession(native, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                session.Bind((id, eventId, payload, timestamp) =>
                {
                    // This mode measures filter delivery only; it deliberately
                    // does not pretend a GPU-memory consumer used these events.
                    payload.DropCounter?.Drop();
                    GpuAllocationObservation.RecordDelivery(name, id, eventId, timestamp, false);
                });
                session.EnableProvider(provider, 0x41, variant.Filter ? allowed : null);
                var clock = Stopwatch.StartNew();
                var processingDone = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.StartProcessing(error => processingDone.TrySetResult(error));
                var captured = false;
                while (clock.Elapsed < TimeSpan.FromSeconds(10))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (processingDone.Task.IsCompleted)
                        throw new IOException("The private real-time consumer stopped before its ten-second observation ended.", await processingDone.Task);
                    // One second before CaptureState separates enable-only
                    // delivery from the capture interval in the second variant.
                    if (variant.Capture && !captured && clock.Elapsed >= TimeSpan.FromSeconds(1))
                    {
                        session.CaptureState(provider, 0x41, allowed);
                        if (variant.Reenable) session.EnableProvider(provider, 0x41, allowed);
                        captured = true;
                    }
                    samples.Add(new { at = DateTimeOffset.UtcNow, elapsedSeconds = clock.Elapsed.TotalSeconds,
                        session = QueryOwnedKernel(name), drops = EtwEventDropDiagnostics.Read().Sessions.Single(value => value.Name == name) });
                    await Task.Delay(100, deadline.Token);
                }
                seconds = clock.Elapsed.TotalSeconds;
                measurement.FinishObservation();
                if (variant.Capture && !captured)
                    throw new InvalidDataException("The CaptureState variant did not execute its capture command within the ten-second observation.");
                if (variant.Reenable)
                {
                    measurement.RequireRestoredFilter(ActualUseGpuMeasurement.RestorationComparisonGrace);
                    if (controlNeededRundownIds is not { Length: > 0 })
                        throw new InvalidDataException("The same-run filtered CaptureState control did not establish a positive needed-rundown-ID set; restoration evidence is inconclusive.");
                    var missing = controlNeededRundownIds.Except(measurement.ReadCaptureStateNeededRundownIds()).Order().ToArray();
                    if (missing.Length != 0)
                        throw new InvalidDataException($"Filtered re-enable variant missed same-run control rundown IDs [{string.Join(",", missing)}] during CaptureState. Missing-ID causality is unproven because the workload may have changed; no fallback or rebuild was applied.");
                }
                if (session.EventsLost != 0) throw new InvalidDataException("The filter comparison lost ETW events.");
                if (measurement.Overflow != 0) throw new InvalidDataException("The filter comparison exceeded its bounded recording capacity.");
                var current = EtwEventDropDiagnostics.Read().Sessions.Single(value => value.Name == name);
                if (current.Kinds.Sum(value => value.Received) == 0)
                    throw new InvalidDataException("No delivered DxgKrnl events; an empty run cannot establish filter capability.");
                if (variant.Name == "filtered-capture")
                    controlNeededRundownIds = measurement.ReadCaptureStateNeededRundownIds();
            }
            catch (Exception error) { failure = error; passed = false; }
            finally
            {
                // Freeze receipt-time cost at the end of the ten-second
                // observation; provider disable/stop latency is not exposure.
                // Any already queued callback is reported in separate tail bins.
                measurement.FinishObservation();
                if (session is not null)
                {
                    try
                    {
                        session.DisableProvider(provider);
                    }
                    catch (Exception error) { failure ??= error; passed = false; }
                    try
                    {
                        release = session.Stop(TimeSpan.FromSeconds(3));
                        if (!release.IsReleased) throw new IOException("Private GPU filter session release was not confirmed: " + string.Join(" | ", release.Failures));
                        session.JoinProcessing();
                    }
                    catch (Exception error) { failure ??= error; passed = false; }
                }
                else if (native is not null)
                {
                    // Preserve the raw owner if wrapping/binding failed. No
                    // session-name-wide or other-process recovery is permitted.
                    try { native.Dispose(); }
                    catch (Exception error) { failure ??= error; passed = false; }
                }
                if (QueryOwnedKernel(name).Exists)
                {
                    failure ??= new IOException("Private GPU filter session remains after cleanup: " + name);
                    passed = false;
                }
                await SaveAsync(evidence, variant.Name + "-samples.json", samples);
                await SaveAsync(evidence, variant.Name + "-kinds-per-second.json", measurement.Read());
                await SaveAsync(evidence, variant.Name + "-restoration.json",
                    measurement.ReadRestorationEvidence(ActualUseGpuMeasurement.RestorationComparisonGrace,
                        variant.Reenable ? controlNeededRundownIds : null));
                cases.Add(new { name = variant.Name, status = failure is null ? "passed" : "failed", session = name,
                    started, ended = DateTimeOffset.UtcNow, observedSeconds = seconds, filtered = variant.Filter,
                    captureState = variant.Capture, keywords = "0x41", allowedIds = variant.Filter ? allowed : null,
                    filteredEnableAfterCaptureState = variant.Reenable,
                    captureStateAt = variant.Capture ? "after-one-second-of-enable-only-observation" : "none",
                    release, error = failure?.ToString(), measurement = variant.Name + "-kinds-per-second.json" });
            }
        }
        var remaining = names.Where(name => QueryOwnedKernel(name).Exists).ToArray();
        cases.Add(new { name = "owned-session-cleanup", status = remaining.Length == 0 ? "passed" : "failed", remaining });
        passed &= remaining.Length == 0;
        await SaveAsync(evidence, "gpu-filter-only.json", new {
            contract = "resource-manager-gpu-filter-only-v1", status = passed ? "passed" : "failed", cases,
            processId = Environment.ProcessId, image, root, artifacts,
            proofLimit = "The first three variants retain the raw controls; the fourth requires positive newly emitted delivery and zero unread IDs after filtered re-enable plus the explicit one-second comparison grace. Compare CaptureState-emitted needed rundown IDs with the raw variant; missing facts are reported, never rebuilt. This mode has no GPU allocation consumer, so accepted counts are intentionally zero; the full copied host measures actual delivered/read cost." });
        return passed ? 0 : 1;
    }
}
