using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>Hardware-domain command owner. Pending includes the command currently executing.</summary>
public sealed class HardwareCommandQueue : IDisposable
{
    private readonly object sync = new();
    private TaskCompletionSource unavailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool disposed;
    private readonly LinkedList<ICommand> queue = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly HashSet<ICommand> pending = [];
    private int pendingScopes;
    private readonly TimeSpan requestTimeout;
    private readonly StateCell<bool>? pendingCell;
    private readonly ResourceManager.ReadWrite.Helpers.HardwareBridgeProcessOwner? bridge;
    public HardwareCommandQueue(StateAuthority? authority = null, ResourceManager.ReadWrite.Helpers.HardwareBridgeProcessOwner? bridge = null,
        TimeSpan? requestTimeout = null)
    {
        this.bridge = bridge;
        this.requestTimeout = requestTimeout ?? bridge?.RequestTimeout ?? TimeSpan.FromSeconds(90);
        if (this.requestTimeout <= TimeSpan.Zero || this.requestTimeout.TotalMilliseconds > uint.MaxValue - 1L)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        pendingCell = authority?.ClaimCell<bool>(StateItemUids.HardwareCommandPending);
        Publish(false);
        if (bridge is not null)
        {
            bridge.AvailabilityChanged += AvailabilityChanged;
            AvailabilityChanged(bridge.IsAvailable);
        }
    }
    public bool HasPending { get { lock (sync) return IsPending; } }
    private bool IsPending => pending.Count != 0 || pendingScopes != 0;

    /// <summary>Extends accepted command ownership through ControlPlane outcome publication.</summary>
    internal IDisposable AcquirePendingScope()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var previous = IsPending;
            pendingScopes++;
            if (!previous) Publish(true);
            return new PendingScope(this);
        }
    }
    private sealed class PendingScope(HardwareCommandQueue owner) : IDisposable
    {
        private HardwareCommandQueue? current = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref current, null);
            if (owner is null) return;
            lock (owner.sync)
            {
                owner.pendingScopes--;
                if (!owner.IsPending) owner.Publish(false);
            }
        }
    }
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(action);
        token.ThrowIfCancellationRequested();
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        if (bridge is { IsAvailable: false }) return await action(token).ConfigureAwait(false);
        var command = new Command<T>(action, token);
        bool runInline;
        Task unavailableSignal;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            runInline = bridge is { IsAvailable: false };
            unavailableSignal = unavailable.Task;
            if (!runInline)
            {
                if (pending.Count == 128) throw new InvalidOperationException("Hardware command queue is full.");
                command.Node = queue.AddLast(command);
                var previous = IsPending;
                pending.Add(command);
                if (!previous) Publish(true);
                if (wake.CurrentCount == 0) wake.Release();
            }
        }
        if (runInline) return await action(token).ConfigureAwait(false);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        waiting.CancelAfter(requestTimeout);
        using var cancellation = waiting.Token.Register(() =>
        {
            lock (sync)
            {
                if (RemoveWaiting(command))
                {
                    if (token.IsCancellationRequested) command.Cancel();
                    else command.Fail(new TimeoutException("Hardware command timed out waiting for execution."));
                    FinishLocked(command);
                }
            }
        });
        // Once taken, the command still owns its caller's operation until it
        // finishes. Returning early would release ControlPlane's serial gate
        // while that command can still publish receipts or change saved state.
        if (bridge is not null)
        {
            while (!command.Completion.Task.IsCompleted)
            {
                await Task.WhenAny(command.Completion.Task, unavailableSignal).ConfigureAwait(false);
                lock (sync)
                {
                    if (command.Node is null) break; // Already taken or canceled: await its own completion.
                    // Notifications only wake us. Recovery may have happened
                    // before this continuation acquired the queue lock.
                    runInline = !bridge.IsAvailable && RemoveWaiting(command);
                    if (runInline) FinishLocked(command);
                    else
                    {
                        if (bridge.IsAvailable && unavailable.Task.IsCompleted)
                            unavailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        unavailableSignal = unavailable.Task;
                    }
                }
                // Waiting work belongs to its caller, never to the failed process entry.
                // Commands already taken by a run retain that run's cancellation token.
                if (runInline) return await action(token).ConfigureAwait(false);
            }
        }
        return await command.Completion.Task.ConfigureAwait(false);
    }
    private void AvailabilityChanged(bool value)
    {
        lock (sync)
        {
            if (disposed) return;
            // Read the owner again so concurrent publication cannot apply an old event.
            if (bridge!.IsAvailable)
            {
                if (unavailable.Task.IsCompleted) unavailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else unavailable.TrySetResult();
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            FailPending(new ObjectDisposedException(nameof(HardwareCommandQueue)));
        }
        if (bridge is not null) bridge.AvailabilityChanged -= AvailabilityChanged;
    }
    public async Task RunAsync(CancellationToken stop)
    {
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            ICommand? command;
            lock (sync)
            {
                command = queue.First?.Value;
                if (command is not null) RemoveWaiting(command);
            }
            if (command is null) await wake.WaitAsync(stop).ConfigureAwait(false);
            else await ExecuteCommandAsync(command, stop).ConfigureAwait(false);
        }
    }

    /// <summary>The sole consumer executes only commands waiting at entry, then returns without waiting for arrivals.</summary>
    public async Task DrainPendingAsync(CancellationToken stop)
    {
        ICommand[] waiting;
        lock (sync) waiting = queue.ToArray();
        foreach (var command in waiting)
        {
            stop.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!RemoveWaiting(command)) continue;
            }
            await ExecuteCommandAsync(command, stop).ConfigureAwait(false);
        }
    }

    private async Task ExecuteCommandAsync(ICommand command, CancellationToken stop)
    {
        try { await command.ExecuteAsync(stop).ConfigureAwait(false); }
        finally { lock (sync) FinishLocked(command); }
    }
    public void FailPending(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (sync)
        {
            while (queue.First is { } first)
            {
                var command = first.Value;
                RemoveWaiting(command);
                command.Fail(error);
                FinishLocked(command);
            }
        }
    }

    // All linked-list and pending transitions belong to sync. Executing commands have no waiting node.
    private bool RemoveWaiting(ICommand command)
    {
        if (command.Node is not { } node) return false;
        queue.Remove(node);
        command.Node = null;
        return true;
    }
    private void FinishLocked(ICommand command)
    {
        if (pending.Remove(command) && !IsPending) Publish(false);
    }
    private void Publish(bool value) => _ = pendingCell?.PublishAsync(value,
        new(BusinessBlockRequestKind.Configuration, nameof(HardwareCommandQueue), "hardware-command-queue", 0));
    private interface ICommand
    {
        LinkedListNode<ICommand>? Node { get; set; }
        Task ExecuteAsync(CancellationToken stop);
        void Cancel();
        void Fail(Exception error);
    }
    private sealed class Command<T>(Func<CancellationToken, Task<T>> action, CancellationToken token) : ICommand
    {
        public LinkedListNode<ICommand>? Node { get; set; }
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Cancel() => Completion.TrySetCanceled(token);
        public async Task ExecuteAsync(CancellationToken stop)
        {
            using var active = CancellationTokenSource.CreateLinkedTokenSource(stop, token);
            try { active.Token.ThrowIfCancellationRequested(); Completion.TrySetResult(await action(active.Token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { Completion.TrySetCanceled(active.Token); }
            catch (Exception error) { Completion.TrySetException(error); }
        }
        public void Fail(Exception error) => Completion.TrySetException(error);
    }
}
