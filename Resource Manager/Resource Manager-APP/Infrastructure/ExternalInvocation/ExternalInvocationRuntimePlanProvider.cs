using ResourceManager.App.Application.ExternalInvocation;
using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Infrastructure.ExternalInvocation;

public sealed class ExternalInvocationRuntimePlanProvider :
    IExternalInvocationRuntimePlanProvider,
    IExternalInvocationRuntimePlanPublisher
{
    private ExternalInvocationRuntimePlan current = ExternalInvocationRuntimePlan.Default;

    public ExternalInvocationRuntimePlan Current => Volatile.Read(ref current);

    public void Publish(ExternalInvocationRuntimePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Interlocked.Exchange(ref current, plan);
    }
}
