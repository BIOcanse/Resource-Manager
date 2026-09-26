using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

internal sealed class HostManagerOperationAttemptArbiter : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<AttemptKey, AttemptControl> attempts = [];
    private bool disposed;

    internal AttemptControl Register(
        NativeOperationHandle128 operationId,
        NativeOperationHandle128 attemptToken)
    {
        if (operationId.IsZero || attemptToken.IsZero)
        {
            throw new ArgumentException("The operation attempt identity is zero.");
        }
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var key = new AttemptKey(operationId, attemptToken);
            if (attempts.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    "The operation attempt is already registered.");
            }
            var control = new AttemptControl();
            attempts.Add(key, control);
            return control;
        }
    }

    internal Task? RequestCancel(
        NativeOperationHandle128 operationId,
        NativeOperationHandle128 attemptToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!attempts.TryGetValue(
                    new AttemptKey(operationId, attemptToken),
                    out var control))
            {
                return null;
            }
            control.RequestCancel();
            return control.Completion;
        }
    }

    internal void Complete(
        NativeOperationHandle128 operationId,
        NativeOperationHandle128 attemptToken)
    {
        AttemptControl? control;
        lock (gate)
        {
            if (!attempts.Remove(
                    new AttemptKey(operationId, attemptToken),
                    out control))
            {
                return;
            }
        }
        control.Complete();
        control.Dispose();
    }

    internal bool IsEmpty
    {
        get
        {
            lock (gate)
            {
                return attempts.Count == 0;
            }
        }
    }

    internal void RequestStopAll()
    {
        AttemptControl[] controls;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            controls = attempts.Values.ToArray();
        }
        foreach (var control in controls)
        {
            control.RequestStop();
        }
    }

    public void Dispose()
    {
        AttemptControl[] controls;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            controls = attempts.Values.ToArray();
            attempts.Clear();
        }
        foreach (var control in controls)
        {
            control.RequestStop();
            control.Complete();
            control.Dispose();
        }
    }

    internal sealed class AttemptControl : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal AttemptControl() => cancellation = new CancellationTokenSource();

        internal CancellationToken Token => cancellation.Token;

        internal Task Completion => completion.Task;

        internal void RequestCancel() => cancellation.Cancel();

        internal void RequestStop() => cancellation.Cancel();

        internal void Complete() => completion.TrySetResult();

        public void Dispose() => cancellation.Dispose();
    }

    private readonly record struct AttemptKey(
        NativeOperationHandle128 OperationId,
        NativeOperationHandle128 AttemptToken);
}
