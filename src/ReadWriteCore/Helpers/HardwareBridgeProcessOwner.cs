using System.Diagnostics;
using System.Text.Json;

namespace ResourceManager.ReadWrite.Helpers;

/// <summary>One entry owns one child, command channel and readings section until the child exits.</summary>
public sealed partial class HardwareBridgeProcessOwner : IDisposable
{
    private readonly Func<string?> resolveExecutablePath;
    private readonly HardwareBridgeProcessOptions options;
    private readonly Action<Exception?, string>? warning;
    private readonly Func<ProcessStartInfo, Process?>? startFactory;
    private readonly object sync = new();
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private Session? current;
    private CancellationTokenSource? runningStop;
    private TaskCompletionSource? ended;
    private bool disposed;
    private bool suspended;
    private volatile bool ownerStopRequested;
    private int subscriptions;
    private long requestId;
    public event Action<bool>? ReadingsInUseChanged;
    public bool ReadingsInUse { get { lock (sync) return subscriptions != 0; } }
    public bool IsInstalled => resolveExecutablePath() is not null;
    public TimeSpan RequestTimeout => options.RequestTimeout;
    public string? UnavailableReason { get; private set; }
    internal Func<Process, TimeSpan, Task> WaitForChildExitAsync { get; set; }
        = static (process, timeout) => process.WaitForExitAsync().WaitAsync(timeout);

    public HardwareBridgeProcessOwner(Func<string?> resolveExecutablePath, HardwareBridgeProcessOptions options,
        Action<Exception?, string>? warning = null, Func<ProcessStartInfo, Process?>? startFactory = null)
    {
        ArgumentNullException.ThrowIfNull(resolveExecutablePath);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RequestTimeout <= TimeSpan.Zero || options.StopTimeout <= TimeSpan.Zero
            || options.ReadingInterval <= TimeSpan.Zero || options.DiagnosticDrainTimeout < TimeSpan.Zero
            || options.AvailabilityCheckInterval < TimeSpan.Zero
            || options.RequestTimeout.TotalMilliseconds > uint.MaxValue - 1L
            || options.StopTimeout.TotalMilliseconds > uint.MaxValue - 1L
            || options.DiagnosticDrainTimeout.TotalMilliseconds > uint.MaxValue - 1L
            || options.AvailabilityCheckInterval.TotalMilliseconds > uint.MaxValue - 1L)
            throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.ErrorOutput);
        var errorOutput = options.ErrorOutput;
        if (errorOutput.ReadBufferSize <= 0 || errorOutput.FirstCharacterLimit < 0 || errorOutput.TailCharacterLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentNullException.ThrowIfNull(errorOutput.RuntimeName);
        ArgumentNullException.ThrowIfNull(errorOutput.RuntimeVersionMarker);
        ArgumentNullException.ThrowIfNull(errorOutput.MissingRuntimeReason);
        ArgumentNullException.ThrowIfNull(errorOutput.StartupFailedReason);
        this.resolveExecutablePath = resolveExecutablePath; this.options = options;
        this.warning = warning; this.startFactory = startFactory;
        InitializeAvailability();
    }

    public async Task RunAsync(CancellationToken stop, Action reportRunning,
        Func<CancellationToken, Task>? executeCommands = null)
    {
        CancellationTokenSource lifetime;
        TaskCompletionSource completed;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runningStop is not null || suspended) throw new InvalidOperationException("Helper run is already owned or installation is active.");
            ThrowIfUnavailable();
            stop.ThrowIfCancellationRequested();
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
            ownerStopRequested = false;
            runningStop = lifetime;
            ended = completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Session? run = null;
        HardwareBridgeChildProcess? child = null;
        Task? commands = null;
        Exception? failure = null;
        var executableNotFound = false;
        try
        {
            try
            {
                var executable = resolveExecutablePath() ?? throw new FileNotFoundException("HardwareBridge is not installed.");
                lifetime.Token.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                // Keep the child owned even if diagnostics or Session construction fails.
                child = HardwareBridgeChildProcess.Start(start, startFactory);
            }
            catch (Exception error) when (error is FileNotFoundException
                or System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 })
            {
                executableNotFound = true;
                throw;
            }
            run = new(child, lifetime, new(child.Error, options.ErrorOutput));
            UnavailableReason = null;
            run.Description = await ExchangeAsync(run, "describe", null, lifetime.Token).ConfigureAwait(false);
            if (run.Description is null) throw new IOException("HardwareBridge description failed.");
            if (run.Description.Value.TryGetProperty("available", out var supported) && supported.ValueKind == JsonValueKind.False)
                throw new BridgeHardwareUnavailableException(run.Description.Value.TryGetProperty("reason", out var reason) ? reason.GetString() : null);
            var name = "Local\\ResourceManager.HardwareBridge." + Guid.NewGuid().ToString("N");
            var configured = await ExchangeAsync(run, "configure-readings", new Dictionary<string, object?>
            {
                ["sectionName"] = name, ["intervalMilliseconds"] = options.ReadingInterval.TotalMilliseconds
            }, lifetime.Token).ConfigureAwait(false);
            if (configured is not { } reply || !reply.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new IOException("HardwareBridge readings configuration failed.");
            run.Section = HardwareBridgeReadingsSection.OpenReader(name);
            lock (sync)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                current = run;
            }
            reportRunning();
            commands = executeCommands?.Invoke(lifetime.Token);
            var exited = child.Process.WaitForExitAsync();
            var stopping = Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
            var winner = commands is null ? await Task.WhenAny(exited, stopping).ConfigureAwait(false)
                : await Task.WhenAny(exited, stopping, commands).ConfigureAwait(false);
            if (winner == commands) await commands!.ConfigureAwait(false);
            if (!lifetime.IsCancellationRequested)
                throw new IOException("HardwareBridge exited unexpectedly.");
            if (!stop.IsCancellationRequested && !ownerStopRequested)
                throw new IOException("HardwareBridge command channel failed.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested
            && (stop.IsCancellationRequested || ownerStopRequested)) { }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            lock (sync) { current = null; }
            var retainedExit = failure as HardwareBridgeChildProcess.ExitNotConfirmedException;
            var exitConfirmed = child is null && retainedExit is null;
            Exception? cleanupFailure = null;
            try
            {
                // A cancellation callback must not prevent process cleanup.
                try { await lifetime.CancelAsync().ConfigureAwait(false); }
                finally
                {
                    try { run?.Section?.Dispose(); }
                    finally
                    {
                        if (child is not null)
                        {
                            await EndRunAsync(child).ConfigureAwait(false);
                            exitConfirmed = true;
                            if (run?.Diagnostics is { } diagnostics)
                            {
                                try { await diagnostics.Completion.WaitAsync(options.DiagnosticDrainTimeout).ConfigureAwait(false); }
                                catch (TimeoutException) { }
                            }
                        }
                    }
                }
            }
            catch (Exception error)
            {
                cleanupFailure = error;
                retainedExit = error as HardwareBridgeChildProcess.ExitNotConfirmedException ?? retainedExit;
                throw;
            }
            finally
            {
                // Join accepted commands even when process cleanup failed. Exit
                // polling may release this run only after its callback has ended.
                try
                {
                    if (commands is not null)
                        try { await commands.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                }
                catch (Exception error) { cleanupFailure ??= error; throw; }
                finally
                {
                    try
                    {
                        if (!exitConfirmed)
                        {
                            lock (sync)
                            {
                                var unconfirmed = retainedExit ?? new HardwareBridgeChildProcess.ExitNotConfirmedException(child!, cleanupFailure ?? failure!);
                                if (disposed)
                                {
                                    unconfirmed.Dispose();
                                    lifetime.Dispose();
                                    runningStop = null;
                                }
                                else
                                {
                                    pendingExit = unconfirmed;
                                    SetUnavailableLocked(HardwareBridgeUnavailableKind.ExitNotConfirmed, pendingExit.Message);
                                }
                            }
                            PublishAvailabilityChanges();
                        }
                        else if (executableNotFound)
                            SetUnavailable(HardwareBridgeUnavailableKind.NotInstalled, failure!.Message);
                        else if (failure is not null || cleanupFailure is not null)
                            PublishRunFailure(failure ?? cleanupFailure!, run?.Diagnostics);
                    }
                    finally
                    {
                        if (exitConfirmed)
                        {
                            lock (sync)
                            {
                                runningStop = null;
                                if (!suspended && unavailableKind == HardwareBridgeUnavailableKind.InstallationInProgress)
                                    InstallationChangedLocked();
                            }
                            child?.Dispose();
                            lifetime.Dispose();
                            completed.TrySetResult();
                            PublishAvailabilityChanges();
                        }
                        else
                        {
                            // Retain exclusion and the actual handle until the owner confirms exit.
                            completed.TrySetException(cleanupFailure ?? failure!);
                        }
                    }
                }
            }
        }
    }

    public async Task<JsonElement?> SendWhileRunningAsync(string operation,
        IReadOnlyDictionary<string, object?>? arguments, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        token.ThrowIfCancellationRequested();
        Session? run;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); run = current;
            if (run is null || run.Token.IsCancellationRequested || run.Child.Process.HasExited) return null;
        }
        if (operation == "read") throw new ArgumentException("Read current values from the readings section.", nameof(operation));
        if (operation == "describe") return run.Description;
        return await ExchangeAsync(run, operation, arguments, token).ConfigureAwait(false);
    }

    private async Task<JsonElement?> ExchangeAsync(Session run, string operation,
        IReadOnlyDictionary<string, object?>? arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        timeout.CancelAfter(options.RequestTimeout);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        await commandGate.WaitAsync(admission.Token).ConfigureAwait(false);
        var sent = false;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            var request = arguments is null ? new Dictionary<string, object?>() : new(arguments);
            request["id"] = Interlocked.Increment(ref requestId); request["op"] = operation;
            sent = true;
            await run.Child.Input.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token).ConfigureAwait(false);
            await run.Child.Input.FlushAsync(timeout.Token).ConfigureAwait(false);
            var line = await run.Child.Output.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null) throw new IOException("HardwareBridge closed its response channel.");
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            // An interrupted accepted line loses the reply boundary. Fail the run;
            // never retry the command or create a replacement from the request path.
            if (sent) run.Stop.Cancel();
            warning?.Invoke(error, operation);
            if (!sent && token.IsCancellationRequested) throw;
            return null;
        }
        finally { commandGate.Release(); }
    }

    // Transport cleanup takes at most three StopTimeout periods (gate, EOF, kill)
    // plus DiagnosticDrainTimeout. The command callback is joined separately and
    // must honor cancellation; it is not covered by this process-cleanup bound.
    private async Task EndRunAsync(HardwareBridgeChildProcess child)
    {
        var entered = await commandGate.WaitAsync(options.StopTimeout).ConfigureAwait(false);
        try
        {
            if (entered)
                try { child.CloseInput(); }
                catch (Exception error) when (error is IOException or InvalidOperationException) { }
            try { await WaitForChildExitAsync(child.Process, options.StopTimeout).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                child.Terminate();
                await WaitForChildExitAsync(child.Process, options.StopTimeout).ConfigureAwait(false);
            }
        }
        catch (Exception failure)
        {
            throw new HardwareBridgeChildProcess.ExitNotConfirmedException(child, failure);
        }
        finally
        {
            if (entered) commandGate.Release();
        }
    }

    public HardwareBridgeReadingsSnapshot? ReadCurrentReadings()
    {
        lock (sync)
        {
            if (current is not { } run || run.Stop.IsCancellationRequested || run.Child.Process.HasExited) return null;
            return run.Section?.Read();
        }
    }
    public IDisposable SubscribeReadings()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (++subscriptions == 1) ReadingsInUseChanged?.Invoke(true);
        }
        return new ReadingSubscription(this);
    }
    private sealed class ReadingSubscription(HardwareBridgeProcessOwner owner) : IDisposable
    {
        private HardwareBridgeProcessOwner? current = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref current, null);
            if (value is null) return;
            lock (value.sync) if (--value.subscriptions == 0) value.ReadingsInUseChanged?.Invoke(false);
        }
    }

    public async ValueTask<IAsyncDisposable> SuspendForInstallationAsync(CancellationToken token)
    {
        Task? completion;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (suspended) throw new InvalidOperationException("Installation already active.");
            if (pendingExit is not null) throw pendingExit;
            suspended = true;
            if (available) SetUnavailableLocked(HardwareBridgeUnavailableKind.InstallationInProgress, "hardware-bridge-installation-in-progress");
            else CancelAvailabilityCheckLocked();
            ownerStopRequested = true; runningStop?.Cancel(); completion = ended?.Task;
        }
        PublishAvailabilityChanges();
        try { if (completion is not null) await completion.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            lock (sync)
            {
                suspended = false;
                if (pendingExit is not null)
                    SetUnavailableLocked(HardwareBridgeUnavailableKind.ExitNotConfirmed, UnavailableReason);
                else if (runningStop is null) InstallationChangedLocked();
            }
            PublishAvailabilityChanges();
            throw;
        }
        return new InstallationScope(this);
    }
    private sealed class InstallationScope(HardwareBridgeProcessOwner owner) : IAsyncDisposable
    {
        private HardwareBridgeProcessOwner? current = owner;
        public ValueTask DisposeAsync()
        {
            var value = Interlocked.Exchange(ref current, null);
            if (value is not null)
            {
                lock (value.sync)
                {
                    value.suspended = false;
                    value.InstallationChangedLocked();
                }
                value.PublishAvailabilityChanges();
            }
            return ValueTask.CompletedTask;
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; ownerStopRequested = true; runningStop?.Cancel();
            if (ended?.Task.IsCompleted == true)
            {
                runningStop?.Dispose();
                runningStop = null;
            }
        }
        DisposeAvailabilityChecks();
    }
    private sealed class Session(HardwareBridgeChildProcess child, CancellationTokenSource stop, HelperErrorOutput diagnostics)
    {
        public HardwareBridgeChildProcess Child { get; } = child;
        public CancellationTokenSource Stop { get; } = stop;
        public CancellationToken Token { get; } = stop.Token;
        public HelperErrorOutput Diagnostics { get; } = diagnostics;
        public JsonElement? Description { get; set; }
        public HardwareBridgeReadingsSection? Section { get; set; }
    }
}
