using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

public sealed class WindowsExternalGpuPlacementRuntime(
    IHostEnvironment environment, ExternalGpuResidentObservation observations,
    ILogger<WindowsExternalGpuPlacementRuntime> logger)
{
    public const string FileName = "ResourceManager.GpuPlacementExternal.exe";
    public const string ProviderId = "chromium-external-dxgi";
    public const string RendererFileName = "ResourceManager.GpuRendererExternal.exe";
    public const string QtProviderId = "qtquick-d3d12-external-dxgi";
    public const string QtVulkanProviderId = "qtquick-vulkan-external";
    internal static string ControlBuildId => typeof(WindowsExternalGpuPlacementRuntime).Module.ModuleVersionId.ToString("D");
    private readonly string executable = Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", FileName);
    private readonly string rendererExecutable = Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", RendererFileName);
    private readonly string evidenceRoot = Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData", "GpuPlacement", "External");
    private readonly object gate = new();
    private Task? activeCompletion;

    public bool HasUnreleasedControl
    {
        get
        {
            lock (gate)
            {
                if (activeCompletion is { IsCompleted: false }) return true;
                // A restarted backend must not forget a surviving recovery owner.
                if (!Directory.Exists(evidenceRoot)) return false;
                foreach (var directory in Directory.EnumerateDirectories(evidenceRoot))
                {
                    var path = Path.Combine(directory, "controller.json");
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var identity = JsonSerializer.Deserialize<GpuPlacementProcessInstance>(File.ReadAllText(path));
                        if (identity is null) return true;
                        using var process = Process.GetProcessById(identity.ProcessId);
                        if (ChromiumGpuProcessIdentity.Matches(process, identity)) return true;
                    }
                    catch (ArgumentException) { }
                    catch (Exception error) when (error is IOException or JsonException or Win32Exception or InvalidOperationException)
                    { logger.LogWarning(error, "Cannot settle external GPU controller {Path}.", path); return true; }
                }
                return false;
            }
        }
    }

    public ExternalGpuPlacementPlan? Prepare(IReadOnlyList<GpuPlacementProcessInstance> processes)
    {
        if (HasUnreleasedControl) return null;
        var eligible = processes.Select(ReadPlan).OfType<ExternalGpuPlacementPlan>().ToArray();
        return eligible.Length == 1 && !HasFailedAttempt(evidenceRoot, eligible[0].Root) ? eligible[0] : null;
    }

    internal IReadOnlyList<WindowsGpuRendererConfirmation.AdapterClient> ConfirmRenderer(GpuPlacementProcessInstance process)
        => WindowsGpuRendererConfirmation.Read(process, observations.ReadAdapterKeys());

    private ExternalGpuPlacementPlan? ReadPlan(GpuPlacementProcessInstance process)
    {
        var clients = ConfirmRenderer(process);
        var apis = clients.Where(client => client.GraphicsApi is not null)
            .Aggregate((GpuGraphicsApi)0, (all, client) => all | client.GraphicsApi!.Value);
        if (apis == 0) return null;
        if (File.Exists(executable) && WindowsGpuRendererConfirmation.ConfirmsOnlyD3D11(clients)
            && ChromiumGpuProcessIdentity.Read(process, apis) is { } chromium)
            return chromium;
        if (!File.Exists(rendererExecutable)) return null;
        var qt = QtQuickGpuProcessIdentity.Read(process);
        var required = qt?.Renderer == ExternalGpuRenderer.QtQuickVulkan ? GpuGraphicsApi.Vulkan : GpuGraphicsApi.D3D12;
        return qt is not null && apis.HasFlag(required) ? qt : null;
    }

    internal static bool HasFailedAttempt(string root, GpuPlacementProcessInstance browser)
    {
        if (!Directory.Exists(root)) return false;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var requestPath = Path.Combine(directory, "request.json");
            if (!File.Exists(requestPath)) continue;
            GpuPlacementProcessInstance? previous;
            try
            {
                using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
                var expected = request.RootElement.GetProperty("expected");
                // Retained Chromium attempts predate the renderer-neutral Root field.
                previous = (expected.TryGetProperty("Root", out var rootIdentity)
                    ? rootIdentity : expected.GetProperty("Browser")).Deserialize<GpuPlacementProcessInstance>();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or KeyNotFoundException) { continue; }
            if (previous is null || previous.ProcessId != browser.ProcessId
                || previous.ProcessStartKey != browser.ProcessStartKey
                || !StringComparer.OrdinalIgnoreCase.Equals(previous.ExecutablePath, browser.ExecutablePath)) continue;
            try
            {
                var resultPath = Path.Combine(directory, "result.json");
                if (!File.Exists(resultPath)) return true;
                var result = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(File.ReadAllText(resultPath));
                if (result?.Status != RunningGpuPlacementActionStatuses.RecreateRequested) return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or KeyNotFoundException) { return true; }
        }
        return false;
    }

    internal static string[] ChromiumControllerArguments(ExternalGpuPlacementPlan plan, string stop, string ready,
        ulong targetAdapterKey, ulong deadline)
        => [plan.Root.ProcessId.ToString(CultureInfo.InvariantCulture), plan.Root.ProcessStartKey.ToString(CultureInfo.InvariantCulture),
            plan.GpuProcess.ProcessId.ToString(CultureInfo.InvariantCulture), plan.GpuProcess.ProcessStartKey.ToString(CultureInfo.InvariantCulture),
            stop, ready, targetAdapterKey.ToString(CultureInfo.InvariantCulture), deadline.ToString(CultureInfo.InvariantCulture),
            plan.Root.ExecutablePath, plan.GpuProcess.ExecutablePath];

    internal static bool IsUnconfirmedObservation(RunningGpuPlacementActionResult? result)
        => result?.Status == RunningGpuPlacementActionStatuses.NotApplied && result.Records.Count == 1
            && result.Records[0].Metadata.GetValueOrDefault("residentConfirmation") == "unconfirmed"
            && result.Records[0].Metadata.GetValueOrDefault("controllerSucceeded") == bool.TrueString
            && result.Records[0].Metadata.GetValueOrDefault("controllerExited") == bool.TrueString
            && result.Records[0].Metadata.GetValueOrDefault("cleanupPassed") == bool.TrueString;

    internal static bool IsSettledFailure(RunningGpuPlacementActionResult? result)
        => result?.Status == RunningGpuPlacementActionStatuses.NotApplied && result.Records.Count == 1
            && result.Records[0].Metadata.GetValueOrDefault("controllerExited") == bool.TrueString
            && result.Records[0].Metadata.GetValueOrDefault("cleanupPassed") == bool.TrueString;

    public async Task<RunningGpuPlacementActionResult> ExecuteAsync(RunningGpuPlacementActionPlan plan,
        RunningGpuPlacementExecution execution, CancellationToken cancellationToken)
    {
        var expected = plan.External ?? throw new ArgumentException("Missing external process identities.", nameof(plan));
        var qt = expected.Renderer is ExternalGpuRenderer.QtQuickD3D12 or ExternalGpuRenderer.QtQuickVulkan;
        var controllerImage = qt ? rendererExecutable : executable;
        var provider = expected.Renderer == ExternalGpuRenderer.QtQuickVulkan ? QtVulkanProviderId : qt ? QtProviderId : ProviderId;
        var workDeadline = execution.RecreationDeadlineMilliseconds;
        var cleanupDeadline = execution.CleanupDeadlineMilliseconds;
        if (workDeadline <= Now || cleanupDeadline <= workDeadline || cancellationToken.IsCancellationRequested)
            return new([], "External GPU action has no remaining execution budget.", RunningGpuPlacementActionStatuses.Skipped);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(evidenceRoot, id);
        var stop = Path.Combine(directory, qt ? "controller.stop" : "stop");
        var cancel = qt ? Path.Combine(directory, "controller.cancel") : stop + ".cancel";
        var events = new ControllerEvents(expected.GpuProcess.ExecutablePath, expected.Renderer);
        Task completion;
        ExternalResidentSample? before;
        Process process;
        lock (gate)
        {
            if (HasUnreleasedControl || HasFailedAttempt(evidenceRoot, expected.Root)
                || ReadPlan(expected.GpuProcess) != expected)
                return new([], "External GPU process identity or control ownership changed.", RunningGpuPlacementActionStatuses.Skipped);
            before = observations.Read(expected.GpuProcess);
            if (before is null || before.Adapters.Count == 0)
                return new([], "No current resident-memory snapshot for this GPU process.", RunningGpuPlacementActionStatuses.Skipped);
            Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo(controllerImage)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory
            };
            var arguments = qt ? new[] {
                expected.GpuProcess.ProcessId.ToString(CultureInfo.InvariantCulture),
                expected.GpuProcess.ProcessStartKey.ToString(CultureInfo.InvariantCulture),
                expected.GpuProcess.ExecutablePath, directory,
                plan.Request.TargetAdapterKey.ToString(CultureInfo.InvariantCulture),
                workDeadline.ToString(CultureInfo.InvariantCulture), expected.Renderer == ExternalGpuRenderer.QtQuickVulkan ? "qtquick-vulkan" : "qtquick-d3d12" }
                : ChromiumControllerArguments(expected, stop, Path.Combine(directory,"ready"), plan.Request.TargetAdapterKey, workDeadline);
            foreach (var value in arguments) start.ArgumentList.Add(value);
            process = Process.Start(start) ?? throw new InvalidOperationException("External GPU controller did not start.");
            completion = CompleteAsync(process, events, directory, cancel);
            activeCompletion = completion;
            try
            {
                var controller = new GpuPlacementProcessInstance(process.Id,
                    checked((ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc()), process.ProcessName, controllerImage);
                WriteNew(Path.Combine(directory, "controller.json"), JsonSerializer.Serialize(controller));
                WriteNew(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new { plan.Request, expected, before, controlBuildId = ControlBuildId }));
                WriteNew(qt ? Path.Combine(directory, "controller.authorize") : stop + ".authorize", string.Empty);
            }
            catch
            {
                RequestStop(cancel);
                throw;
            }
        }

        ExternalResidentSample? after = null;
        string? error = null;
        var triggerSent = false;
        try
        {
            while (!completion.IsCompleted && Now < workDeadline && !events.RecoveryRequired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (qt && !triggerSent && File.Exists(Path.Combine(directory, "controller.ready")))
                {
                    WriteNew(Path.Combine(directory, "device-loss.request"), string.Empty);
                    triggerSent = true;
                }
                if (qt && File.Exists(Path.Combine(directory, "controller.presentation-observed"))) break;
                if (!qt && events.Replacement is { } replacement
                    && events.CanReleaseChromium(observations.Read(replacement), plan.Request.TargetAdapterKey)) break;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            RequestStop(error is null && !cancellationToken.IsCancellationRequested ? stop : cancel);
        }
        var remaining = cleanupDeadline > Now ? TimeSpan.FromMilliseconds(cleanupDeadline - Now) : TimeSpan.Zero;
        try { await completion.WaitAsync(remaining).ConfigureAwait(false); }
        catch (Exception exception) { error ??= exception.Message; }

        // A device's presence is distinct from presentation and from the amount of memory released.
        // Record facts, never infer render health or complete VRAM release from a controller exit.
        var finished = completion.IsCompletedSuccessfully;
        var summary = events.Summary;
        var clean = finished && summary?.CleanupPassed == true && summary.RootRestored && !events.RecoveryRequired;
        var finalIdentity = qt ? expected.GpuProcess : events.Replacement;
        var controllerSucceeded = clean && events.ExitCode == 0 && summary!.Passed && error is null && events.Error is null
            && finalIdentity is not null
            && (!qt || events.QtSubmissionObserved && events.Presentation is { Verified: true, Count: >= 30 } presentation
                && presentation.AdapterKey == plan.Request.TargetAdapterKey);
        var confirmationStartedAtUtcTicks = DateTime.UtcNow.Ticks;
        if (controllerSucceeded)
        {
            try
            {
                after = await observations.WaitForNextAsync(finalIdentity!, confirmationStartedAtUtcTicks,
                    workDeadline, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) { error = exception.Message; }
        }
        var transfer = controllerSucceeded && after?.ObservedAtUtcTicks > confirmationStartedAtUtcTicks
            ? ExternalGpuResidentObservation.ClassifyTransfer(before, after, plan.Request.TargetAdapterKey)
            : ExternalResidentTransfer.Unconfirmed;
        var succeeded = transfer != ExternalResidentTransfer.Unconfirmed;
        var status = !finished || !clean ? RunningGpuPlacementActionStatuses.Unresolved
            : succeeded ? RunningGpuPlacementActionStatuses.RecreateRequested : RunningGpuPlacementActionStatuses.NotApplied;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = provider, ["evidenceDirectory"] = directory,
            ["targetAdapterKey"] = plan.Request.TargetAdapterKey.ToString(CultureInfo.InvariantCulture),
            ["controllerExited"] = finished.ToString(), ["cleanupPassed"] = clean.ToString(),
            ["controllerSucceeded"] = controllerSucceeded.ToString(),
            ["confirmationStartedAtUtcTicks"] = confirmationStartedAtUtcTicks.ToString(CultureInfo.InvariantCulture),
            ["residentConfirmation"] = transfer switch
            {
                ExternalResidentTransfer.Full => "confirmed",
                ExternalResidentTransfer.Partial => "partial",
                _ => controllerSucceeded ? "unconfirmed" : "not-attempted"
            },
            ["controllerExitCode"] = JsonSerializer.Serialize(events.ExitCode),
            ["replacement"] = JsonSerializer.Serialize(events.Replacement),
            ["beforeResident"] = JsonSerializer.Serialize(before), ["afterResident"] = JsonSerializer.Serialize(after),
            ["presentation"] = qt ? JsonSerializer.Serialize(events.Presentation) : "route-qualified;not-observed-per-action",
            ["error"] = error ?? events.Error ?? string.Empty,
            ["summary"] = JsonSerializer.Serialize(summary)
        };
        var result = new RunningGpuPlacementActionResult([new(id, provider, metadata)],
            succeeded ? transfer == ExternalResidentTransfer.Full
                ? "Renderer recreated; target residency, full source release and controller cleanup recorded. Presentation was not verified."
                : "Renderer recreated; target residency, partial source release and controller cleanup recorded. Presentation was not verified."
                : "External GPU action was not confirmed; this attempt is recorded without a fallback or in-action retry.", status);
        WriteNew(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result));
        return result;
    }

    private async Task CompleteAsync(Process process, ControllerEvents events, string directory, string cancel)
    {
        try
        {
            void Failed(Exception error)
            {
                events.Error ??= error.Message;
                logger.LogError(error, "External GPU controller evidence failed at {Directory}; draining continues.", directory);
                RequestStop(cancel);
            }
            var output = PumpAsync(process.StandardOutput, Path.Combine(directory, "stdout.jsonl"), events.Accept, Failed);
            var errors = PumpAsync(process.StandardError, Path.Combine(directory, "stderr.log"), line =>
            {
                if (!string.IsNullOrWhiteSpace(line)) Failed(new InvalidOperationException(line));
            }, Failed);
            await Task.WhenAll(output, errors, process.WaitForExitAsync()).ConfigureAwait(false);
            events.ExitCode = process.ExitCode;
        }
        finally { process.Dispose(); }
    }

    private static Task PumpAsync(TextReader reader, string path, Action<string> accept, Action<Exception> failed)
        => DrainAsync(reader, () => new StreamWriter(new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read)) { AutoFlush = true }, accept, failed);

    internal static async Task DrainAsync(TextReader reader, Func<TextWriter> createWriter,
        Action<string> accept, Action<Exception> failed)
    {
        TextWriter? writer = null;
        try { writer = createWriter(); }
        catch (Exception error) { failed(error); }
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                accept(line);
                if (writer is null) continue;
                try { await writer.WriteLineAsync(line).ConfigureAwait(false); }
                catch (Exception error)
                {
                    failed(error);
                    try { writer.Dispose(); } catch (Exception disposeError) { failed(disposeError); }
                    writer = null;
                }
            }
        }
        catch (Exception error) { failed(error); reader.Dispose(); }
        finally { if (writer is not null) try { await writer.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failed(error); } }
    }

    private static ulong Now => checked((ulong)Environment.TickCount64);
    private static void WriteNew(string path, string text)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file);
        writer.Write(text);
    }
    private void RequestStop(string path)
    {
        try { if (!File.Exists(path)) WriteNew(path, string.Empty); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger.LogError(error, "Cannot signal external GPU controller cleanup at {Path}.", path); }
    }

    internal sealed class ControllerEvents(string executablePath, ExternalGpuRenderer renderer = ExternalGpuRenderer.ChromiumAngle)
    {
        public volatile GpuPlacementProcessInstance? Replacement;
        public volatile ControllerSummary? Summary;
        public volatile bool RecoveryRequired;
        public volatile string? Error;
        public int? ExitCode;
        public volatile bool QtSubmissionObserved;
        private volatile bool enumerationObserved;
        public volatile ControllerPresentation? Presentation;
        internal bool CanReleaseChromium(ExternalResidentSample? sample, ulong target)
            => Replacement is not null && enumerationObserved && !RecoveryRequired
                && sample?.Adapters.Any(row => row.AdapterKey == target && row.ResidentBytes > 0) == true;
        public void Accept(string line)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                switch (root.GetProperty("kind").GetString())
                {
                    case "process" when root.GetProperty("gpu").GetBoolean():
                        var identity = new GpuPlacementProcessInstance(root.GetProperty("pid").GetInt32(),
                            ulong.Parse(root.GetProperty("creationFileTime").GetString()!, CultureInfo.InvariantCulture),
                            Path.GetFileNameWithoutExtension(executablePath), executablePath);
                        if (Replacement is not null && Replacement != identity) { Error = "Multiple replacement GPU processes."; RecoveryRequired = true; }
                        else Replacement = identity;
                        break;
                    case "recoveryRequired": RecoveryRequired = true; Error = root.GetProperty("error").GetString(); break;
                    case "enum" when renderer == ExternalGpuRenderer.ChromiumAngle:
                        if (Replacement?.ProcessId == root.GetProperty("pid").GetInt32()
                            && root.GetProperty("slot").GetInt32() is 0 or 1 or 3)
                            enumerationObserved = true;
                        break;
                    case "qtD3D12Submission" when renderer == ExternalGpuRenderer.QtQuickD3D12:
                    case "qtVulkanSubmission" when renderer == ExternalGpuRenderer.QtQuickVulkan:
                        QtSubmissionObserved = root.GetProperty("tid").GetUInt32() > 0;
                        break;
                    case "vulkanObservation" when renderer == ExternalGpuRenderer.QtQuickVulkan:
                        if (Presentation is not null) { Error = "Duplicate presentation observation."; RecoveryRequired = true; }
                        else Presentation = new(root.GetProperty("luid").GetUInt64(), root.GetProperty("targetPresents").GetUInt32(),
                            root.GetProperty("targetPresentationVerified").GetBoolean()
                                && root.GetProperty("sourceLuid").GetUInt64() != 0
                                && root.GetProperty("sourceLuid").GetUInt64() != root.GetProperty("luid").GetUInt64()
                                && root.GetProperty("window").GetUInt64() != 0 && root.GetProperty("instance").GetUInt64() != 0);
                        break;
                    case "d3d12Observation" when renderer == ExternalGpuRenderer.QtQuickD3D12:
                        if (Presentation is not null) { Error = "Duplicate presentation observation."; RecoveryRequired = true; }
                        else Presentation = new(root.GetProperty("luid").GetUInt64(), root.GetProperty("targetPresents").GetUInt32(),
                            root.GetProperty("targetPresentationVerified").GetBoolean()
                                && root.GetProperty("luidObservedInsideSwapChainCreation").GetBoolean()
                                && !root.GetProperty("swapChainLifetimeEnded").GetBoolean());
                        break;
                    case "summary":
                        if (Summary is not null) { Error = "Duplicate controller summary."; RecoveryRequired = true; }
                        else Summary = new(root.GetProperty("passed").GetBoolean(), root.GetProperty("cleanupPassed").GetBoolean(),
                            root.GetProperty(renderer != ExternalGpuRenderer.ChromiumAngle ? "debuggerAbsent" : "rootRestored").GetBoolean());
                        break;
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            { Error = exception.Message; RecoveryRequired = true; }
        }
    }
    internal sealed record ControllerSummary(bool Passed, bool CleanupPassed, bool RootRestored);
    internal sealed record ControllerPresentation(ulong AdapterKey, uint Count, bool Verified);
}
