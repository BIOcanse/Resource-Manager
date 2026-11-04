using System.Text.Json;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

public sealed partial class FanControlCoreClient
{
    private readonly SemaphoreSlim resourceGate = new(1, 1);
    private readonly SemaphoreSlim discoveryGate = new(1, 1);
    private TaskCompletionSource<IFanControlCoreSession?> runReady = NewRunSignal();
    private Task runStartup = Task.CompletedTask;
    private Task runShutdown = Task.CompletedTask;
    private Task? disposal;
    private TaskCompletionSource resourceIdle = CompletedSignal();
    private int holders;
    private int pendingCommands;
    private bool runHoldsSession;

    internal bool PendingCommands => Volatile.Read(ref pendingCommands) != 0;
    internal event Action? PendingCommandsChanged;

    /// <summary>Retains command admission until the control operation publishes its outcome.</summary>
    internal IDisposable AcquirePendingScope()
    {
        lock (sessionSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ChangePending(1);
            return new PendingScope(this);
        }
    }

    private sealed class PendingScope(FanControlCoreClient owner) : IDisposable
    {
        private FanControlCoreClient? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ChangePending(-1);
    }

    private static TaskCompletionSource<IFanControlCoreSession?> NewRunSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private void ChangePending(int change)
    {
        lock (sessionSync)
        {
            var previous = pendingCommands;
            pendingCommands += change;
            if ((previous == 0) == (pendingCommands == 0)) return;
            foreach (var callback in PendingCommandsChanged?.GetInvocationList() ?? [])
            {
                try { ((Action)callback)(); }
                catch (Exception error) { logger?.LogWarning(error, "Fan pending-command observer failed."); }
            }
        }
    }

    public async Task<JsonElement?> SendAsync(string operation,
        IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken, bool skipIfBusy = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (sessionSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsAvailable) return UnavailableCommand();
            if (skipIfBusy && (sessionStopped || pendingCommands != 0 || gate.CurrentCount == 0)) return null;
            ChangePending(1);
        }
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(RequestTimeout);
        var command = ExecuteRunRequestAsync(operation, arguments, waiting.Token, pendingCommand: true);
        try { return await command.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller callbacks run last-in-first-out. The outer waiter can finish and
            // dispose the link before its callback reaches this queued operation.
            waiting.Cancel();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && waiting.IsCancellationRequested)
        {
            unavailableReason = "fan-core-timeout";
            return null;
        }
        catch (TimeoutException error)
        {
            waiting.Cancel();
            logger?.LogWarning(error, "Fan Control Core request timed out ({Operation}); accepted native work remains owned until completion.", operation);
            unavailableReason = "fan-core-timeout";
            return null;
        }
    }

    /// <summary>Periodic readings use the current run; they cannot admit or open a session.</summary>
    public async Task<JsonElement?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(RequestTimeout);
        var reading = ExecuteRunRequestAsync("read", null, waiting.Token, pendingCommand: false);
        try { return await reading.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            waiting.Cancel();
            throw;
        }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested) { return null; }
        catch (TimeoutException)
        {
            waiting.Cancel();
            return null;
        }
    }

    private async Task<JsonElement?> ExecuteRunRequestAsync(string operation,
        IReadOnlyDictionary<string, object?>? arguments, CancellationToken waiting, bool pendingCommand)
    {
        try
        {
            while (true)
            {
                TaskCompletionSource<IFanControlCoreSession?> ready;
                lock (sessionSync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (pendingCommand && !IsAvailable) return UnavailableCommand();
                    if (!pendingCommand && (sessionStopped || !runReady.Task.IsCompletedSuccessfully)) return null;
                    ready = runReady;
                }
                var current = pendingCommand
                    ? await WaitForRunAsync(ready.Task, waiting).ConfigureAwait(false)
                    : await ready.Task.WaitAsync(waiting).ConfigureAwait(false);
                if (current is null) continue;
                await gate.WaitAsync(waiting).ConfigureAwait(false);
                var accepted = false;
                try
                {
                    lock (sessionSync)
                    {
                        ObjectDisposedException.ThrowIf(disposed, this);
                        waiting.ThrowIfCancellationRequested();
                        if (sessionStopped || !ReferenceEquals(ready, runReady))
                        {
                            if (!pendingCommand) return null;
                            continue;
                        }
                        if (activeCalls++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        commandOperation = operation;
                        accepted = true;
                    }
                    // Once accepted, cancellation cannot stop native I/O or release its gate/owner early.
                    return await Task.Run(() => ExecuteRequest(current, operation, arguments)).ConfigureAwait(false);
                }
                finally
                {
                    if (accepted)
                    {
                        lock (sessionSync) commandOperation = null;
                        EndCall();
                    }
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) { throw; }
        catch (Exception error)
        {
            logger?.LogWarning(error, "Fan Control Core request failed ({Operation}).", operation);
            if (!IsAvailable) return UnavailableCommand();
            unavailableReason = "fan-core-failed";

            return null;
        }
        finally { if (pendingCommand) ChangePending(-1); }
    }

    private JsonElement? ExecuteRequest(IFanControlCoreSession current, string operation,
        IReadOnlyDictionary<string, object?>? arguments)
    {
        var request = new Dictionary<string, object?>(arguments ?? new Dictionary<string, object?>())
        {
            ["id"] = Interlocked.Increment(ref nextRequestId), ["op"] = operation
        };
        var response = Handle(current, JsonSerializer.SerializeToElement(request), operation);
        if (response is { } result)
        {
            if (operation == "describe") ObserveDescription(result);
        }
        return response;
    }

    private JsonElement? Handle(IFanControlCoreSession current, JsonElement request, string operation)
    {
        try { return current.Handle(request); }
        catch (Exception error)
        {
            logger?.LogWarning(error, "Fan Control Core request failed ({Operation}).", operation);
            unavailableReason = "fan-core-failed";

            return null;
        }
    }

    private async Task<IFanControlCoreSession> AcquireSessionAsync(CancellationToken cancellationToken)
    {
        await resourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (holders == 0)
            {
                resourceIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    lock (sessionSync) commandOperation = "start/recovery for describe";
                    var opened = await Task.Run(() =>
                    {
                        var stateDirectory = Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
                            "UserData", "Control", "FanControlCore");
                        var created = sessionFactory is { } factory
                            ? factory(stateDirectory, ResolveLibreHardwareMonitorPath())
                            : new EmbeddedFanControlCoreSession(stateDirectory, ResolveLibreHardwareMonitorPath());
                        created.SetDiagnosticSink(ReportDiagnostic);
                        lock (sessionSync) session = created;
                        ObserveSession(created);
                        return created;
                    }).ConfigureAwait(false);
                }
                catch
                {
                    resourceIdle.TrySetResult();
                    throw;
                }
                finally { lock (sessionSync) commandOperation = null; }
            }
            holders++;
            return session!;
        }
        finally { resourceGate.Release(); }
    }

    private async Task ReleaseSessionAsync()
    {
        await resourceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (--holders != 0) return;
            Task drain;
            lock (sessionSync) drain = drained.Task;
            await drain.ConfigureAwait(false);
            IFanControlCoreSession? closing;
            lock (sessionSync) { closing = session; session = null; closingSession = closing; }
            try
            {
                await Task.Run(() =>
                {
                    ForgetSession(closing);
                    closing?.Dispose();
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                logger?.LogError(error, "Fan Control Core shutdown restoration failed; durable recovery remains for next startup.");
            }
            finally
            {

                lock (sessionSync) closingSession = null;
                resourceIdle.TrySetResult();
            }
        }
        finally { resourceGate.Release(); }
    }

    private void EndCall()
    {
        lock (sessionSync)
            if (--activeCalls == 0) drained.TrySetResult();
    }

    internal Task StartSession()
    {
        lock (sessionSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!sessionStopped) return runStartup;
            sessionStopped = false;
            runStartup = StartRunAsync(runReady, runShutdown);
            return runStartup;
        }
    }

    private async Task StartRunAsync(TaskCompletionSource<IFanControlCoreSession?> ready, Task precedingShutdown)
    {
        try
        {
            await precedingShutdown.ConfigureAwait(false);
            var held = await AcquireSessionAsync(CancellationToken.None).ConfigureAwait(false);
            lock (sessionSync) runHoldsSession = true;
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!sessionStopped && !disposed && ReferenceEquals(ready, runReady))
                {
                    var description = await Task.Run(() => ExecuteRequest(held, "describe", null)).ConfigureAwait(false);
                    if (description is not { } result || result.TryGetProperty("available", out var availableValue)
                        && availableValue.ValueKind == JsonValueKind.False)
                        throw new InvalidOperationException(openFailureReason ?? (description is { } failed ? Text(failed, "reason") : null)
                            ?? "Fan core Open failed.");
                }
            }
            finally { gate.Release(); }
            lock (sessionSync)
                if (!sessionStopped && !disposed && ReferenceEquals(ready, runReady)) ready.TrySetResult(held);
        }
        catch (Exception error)
        {
            unavailableReason = "fan-core-failed";
            logger?.LogWarning(error, "Fan Control Core startup discovery/recovery failed.");
            ready.TrySetException(error);
            throw;
        }
    }

    internal ValueTask StopSessionAsync()
    {
        lock (sessionSync)
        {
            if (sessionStopped) return new(runShutdown);
            sessionStopped = true;
            var preceding = runReady;
            runReady = NewRunSignal();
            preceding.TrySetResult(null);
            runShutdown = StopRunAsync(runStartup);
            return new(runShutdown);
        }
    }

    private async Task StopRunAsync(Task startup)
    {
        try { await startup.ConfigureAwait(false); }
        catch { /* The start path owns its error; cleanup must still release a late acquired holder. */ }
        bool release;
        lock (sessionSync) { release = runHoldsSession; runHoldsSession = false; }
        if (release) await ReleaseSessionAsync().ConfigureAwait(false);
    }

    private static string? ResolveLibreHardwareMonitorPath()
    {
        var path = Path.GetDirectoryName(typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.Location);
        return path is not null && File.Exists(Path.Combine(path, "LibreHardwareMonitorLib.dll")) ? path : null;
    }

    public void Dispose() => _ = DisposeAsync().AsTask();

    public ValueTask DisposeAsync()
    {
        lock (sessionSync)
        {
            if (disposal is not null) return new(disposal);
            disposed = true;
            runReady.TrySetException(new ObjectDisposedException(nameof(FanControlCoreClient)));
            disposal = DisposeOwnedAsync();
            return new(disposal);
        }
    }

    private async Task DisposeOwnedAsync()
    {
        await StopSessionAsync().ConfigureAwait(false);
        await resourceGate.WaitAsync().ConfigureAwait(false);
        Task idle;
        try { idle = resourceIdle.Task; }
        finally { resourceGate.Release(); }
        await idle.ConfigureAwait(false);
    }
}
