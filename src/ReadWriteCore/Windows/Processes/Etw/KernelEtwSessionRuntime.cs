using Microsoft.Diagnostics.Tracing.Parsers;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public interface IKernelEtwSessionRuntimeFactory
{
    /// <summary>Validates a hold before registration; must not change the running provider.</summary>
    void ValidateRequest(KernelEtwSubscriptionRequest request) { }

    bool TryGetAvailability(out string state, out string message);

    IKernelEtwSessionRuntime Create(
        string sessionName,
        KernelTraceEventParser.Keywords keywords,
        KernelEtwSessionContext context,
        IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions);
}

public interface IKernelEtwSessionRuntime : IDisposable
{
    bool IsProcessing { get; }

    int EventsLost { get; }

    void StartProcessing(Action<Exception?> completed);

    void UpdateSubscriptions(
        IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
        KernelEtwSessionContext context);

    KernelEtwSessionStopResult Stop(TimeSpan waitTimeout);
}

internal sealed class KernelEtwSessionCreationCleanupException(
    Exception creationFailure,
    Exception cleanupFailure,
    IKernelEtwSessionRuntime? runtime = null)
    : Exception(
        "Kernel ETW session creation failed and the partially created session was not proven released.",
        new AggregateException(creationFailure, cleanupFailure))
{
    internal IKernelEtwSessionRuntime? Runtime { get; } = runtime;
}

public sealed record KernelEtwSessionStopResult(
    bool SessionStopSucceeded,
    bool SessionDisposeSucceeded,
    bool ProcessingCompleted,
    IReadOnlyList<string> Failures)
{
    public bool ControlDenied { get; init; }

    public bool IsReleased => SessionStopSucceeded
        && SessionDisposeSucceeded
        && ProcessingCompleted
        && Failures.Count == 0;

    public static KernelEtwSessionStopResult Released { get; } = new(
        true,
        true,
        true,
        []);
}
