using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Application.RuntimeSpecialization;

public interface IRuntimePlanProvider
{
    CompiledRuntimePlan Current { get; }

    RuntimePlanPublicationLease AcquirePublicationLease();

    RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan);
}

public sealed record RuntimePlanDeliveryFailure(
    string Subscriber,
    string ExceptionType,
    string Message);

public sealed record RuntimePlanPublicationResult(
    CompiledRuntimePlan Plan,
    ulong PublicationSequence,
    IReadOnlyList<RuntimePlanDeliveryFailure> DeliveryFailures)
{
    public bool HasDeliveryFailures => DeliveryFailures.Count != 0;
}

public sealed class RuntimePlanPublicationLease : IDisposable
{
    private Action? release;

    private RuntimePlanPublicationLease(
        CompiledRuntimePlan plan,
        ulong publicationSequence,
        Action? release)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        PublicationSequence = publicationSequence;
        this.release = release;
    }

    public CompiledRuntimePlan Plan { get; }

    public ulong PublicationSequence { get; }

    public static RuntimePlanPublicationLease CreateUntracked(
        CompiledRuntimePlan plan,
        ulong publicationSequence)
        => new(plan, publicationSequence, null);

    internal static RuntimePlanPublicationLease CreateTracked(
        CompiledRuntimePlan plan,
        ulong publicationSequence,
        Action release)
        => new(
            plan,
            publicationSequence,
            release ?? throw new ArgumentNullException(nameof(release)));

    public void Dispose()
        => Interlocked.Exchange(ref release, null)?.Invoke();
}
