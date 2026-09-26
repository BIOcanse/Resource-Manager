using System.Diagnostics;

namespace ResourceManager.NativeUi;

internal interface IBackendServiceAvailabilitySource
{
    event EventHandler<BackendServiceUnavailableEventArgs>? AvailabilityLost;
}

internal sealed class BackendServiceLossHandler : IDisposable
{
    private const int Active = 0;
    private const int TransitionQueued = 1;
    private const int Disposed = 2;

    private readonly IBackendServiceAvailabilitySource source;
    private readonly Func<bool> isShuttingDown;
    private readonly Action<Action> dispatch;
    private readonly Action<BackendServiceUnavailableEventArgs> transitionUnavailable;
    private int state;

    public BackendServiceLossHandler(
        IBackendServiceAvailabilitySource source,
        Func<bool> isShuttingDown,
        Action<Action> dispatch,
        Action<BackendServiceUnavailableEventArgs> transitionUnavailable)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.isShuttingDown =
            isShuttingDown ?? throw new ArgumentNullException(nameof(isShuttingDown));
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.transitionUnavailable = transitionUnavailable
            ?? throw new ArgumentNullException(nameof(transitionUnavailable));
        source.AvailabilityLost += OnAvailabilityLost;
    }

    public void ReportUnavailable(BackendServiceUnavailableEventArgs unavailable)
    {
        ArgumentNullException.ThrowIfNull(unavailable);
        Trace.WriteLine(unavailable.Message);
        if (isShuttingDown()
            || Interlocked.CompareExchange(ref state, TransitionQueued, Active) != Active)
        {
            return;
        }

        try
        {
            dispatch(() =>
            {
                if (Volatile.Read(ref state) == TransitionQueued && !isShuttingDown())
                {
                    transitionUnavailable(unavailable);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Trace.WriteLine(ex);
            _ = Interlocked.CompareExchange(ref state, Active, TransitionQueued);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref state, Disposed) == Disposed)
        {
            return;
        }

        source.AvailabilityLost -= OnAvailabilityLost;
    }

    private void OnAvailabilityLost(
        object? sender,
        BackendServiceUnavailableEventArgs unavailable) =>
        ReportUnavailable(unavailable);
}
