using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class RuntimePlanProvider(HostManagerDeploymentState hostManagerDeploymentState) : IRuntimePlanProvider
{
    private readonly object publicationGate = new();
    private int activePublicationLeases;

    internal event Action<CompiledRuntimePlan>? Published;

    public CompiledRuntimePlan Current => hostManagerDeploymentState.CurrentRuntimePlan;

    public RuntimePlanPublicationLease AcquirePublicationLease()
    {
        lock (publicationGate)
        {
            var publication =
                hostManagerDeploymentState.CaptureRuntimePlanPublication();
            activePublicationLeases = checked(activePublicationLeases + 1);
            return RuntimePlanPublicationLease.CreateTracked(
                publication.Plan,
                publication.PublicationSequence,
                ReleasePublicationLease);
        }
    }

    public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        HostManagerRuntimePlanPublication publication;
        lock (publicationGate)
        {
            while (activePublicationLeases != 0)
            {
                Monitor.Wait(publicationGate);
            }

            hostManagerDeploymentState.PublishRuntimePlan(plan);
            publication =
                hostManagerDeploymentState.CaptureRuntimePlanPublication();
        }

        var failures = new List<RuntimePlanDeliveryFailure>();
        if (Published is { } published)
        {
            foreach (var subscriber in published.GetInvocationList())
            {
                try
                {
                    ((Action<CompiledRuntimePlan>)subscriber)(plan);
                }
                catch (Exception exception)
                {
                    failures.Add(new RuntimePlanDeliveryFailure(
                        $"{subscriber.Method.DeclaringType?.FullName ?? "<unknown>"}.{subscriber.Method.Name}",
                        exception.GetType().FullName ?? exception.GetType().Name,
                        exception.Message));
                }
            }
        }

        return new RuntimePlanPublicationResult(
            publication.Plan,
            publication.PublicationSequence,
            failures);
    }

    private void ReleasePublicationLease()
    {
        lock (publicationGate)
        {
            if (activePublicationLeases <= 0)
            {
                throw new InvalidOperationException(
                    "The runtime plan publication lease count is invalid.");
            }

            activePublicationLeases--;
            if (activePublicationLeases == 0)
            {
                Monitor.PulseAll(publicationGate);
            }
        }
    }
}
