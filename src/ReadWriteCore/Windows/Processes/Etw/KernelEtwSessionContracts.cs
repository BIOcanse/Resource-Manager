using Microsoft.Diagnostics.Tracing.Parsers;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public interface IKernelEtwSessionBroker
{
    IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request);

    KernelEtwSessionSnapshot GetSnapshot();
}

public interface IKernelEtwSubscription : IDisposable
{
    /// <summary>Session status with availability resolved for this lease's registered keywords.</summary>
    KernelEtwSessionSnapshot GetSnapshot();
}

public sealed record KernelEtwSubscriptionRequest(
    string Id,
    KernelTraceEventParser.Keywords Keywords,
    Action<KernelEtwEventSource, KernelEtwSessionContext> Bind);

public sealed record KernelEtwSessionContext(
    long Generation,
    DateTimeOffset StartedAt);

public sealed record KernelEtwSessionSnapshot(
    string State,
    string Message,
    long Generation,
    DateTimeOffset StartedAt,
    KernelTraceEventParser.Keywords EnabledKeywords,
    int SubscriptionCount,
    int EventsLost)
{
    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One current holder of the shared kernel session: its lease, its named id and the keywords it asked for.</summary>
public sealed record KernelEtwHolderView(long LeaseId, string Id, string Keywords);

public sealed record KernelEtwSubscriptionRegistration(
    long LeaseId,
    string Id,
    KernelTraceEventParser.Keywords Keywords,
    Action<KernelEtwEventSource, KernelEtwSessionContext> Bind);
