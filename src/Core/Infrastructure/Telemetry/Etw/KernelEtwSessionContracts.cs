using Microsoft.Diagnostics.Tracing.Parsers;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public interface IKernelEtwSessionBroker
{
    IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request);

    KernelEtwSessionSnapshot GetSnapshot();
}

public interface IKernelEtwSubscription : IDisposable;

public sealed record KernelEtwSubscriptionRequest(
    string Id,
    KernelTraceEventParser.Keywords Keywords,
    Action<KernelTraceEventParser, KernelEtwSessionContext> Bind);

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

internal sealed record KernelEtwSubscriptionRegistration(
    long LeaseId,
    string Id,
    KernelTraceEventParser.Keywords Keywords,
    Action<KernelTraceEventParser, KernelEtwSessionContext> Bind);
