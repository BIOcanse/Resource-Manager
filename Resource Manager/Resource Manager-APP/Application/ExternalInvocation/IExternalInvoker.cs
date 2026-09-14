using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Application.ExternalInvocation;

public interface IExternalInvoker
{
    ValueTask<ExternalInvocationResult> InvokeAsync(
        ExternalInvocationRequest request,
        CancellationToken cancellationToken);
}

public interface IExternalInvocationRuntimePlanProvider
{
    ExternalInvocationRuntimePlan Current { get; }
}

public interface IExternalInvocationRuntimePlanPublisher
{
    void Publish(ExternalInvocationRuntimePlan plan);
}

public interface IExternalInvocationAuditSink
{
    ValueTask WriteAsync(
        ExternalInvocationAuditEvent auditEvent,
        CancellationToken cancellationToken);
}
