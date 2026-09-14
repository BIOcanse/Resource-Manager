using System.Diagnostics;
using ResourceManager.App.Application.ExternalInvocation;
using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Infrastructure.ExternalInvocation;

public sealed class ExternalInvoker(
    IExternalInvocationRegistry registry,
    IExternalInvocationRuntimePlanProvider runtimePlanProvider,
    IExternalInvocationPolicy policy,
    IExternalInvocationAuditSink auditSink,
    ILogger<ExternalInvoker> logger) : IExternalInvoker
{
    public async ValueTask<ExternalInvocationResult> InvokeAsync(
        ExternalInvocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Caller);

        var started = Stopwatch.GetTimestamp();
        ExternalInvocationRegistration? registration = null;
        ExternalInvocationResult result;

        if (!registry.TryGetOperation(request.OperationId, out registration))
        {
            result = ExternalInvocationResult.Reject(
                ExternalInvocationStatus.NotFound,
                "operation-not-found");
        }
        else
        {
            result = await InvokeRegisteredAsync(
                request,
                registration,
                cancellationToken);
        }

        await WriteAuditSafelyAsync(
            request,
            registration?.Module.ModuleId,
            registration?.Operation.Descriptor.AccessClass,
            result,
            Stopwatch.GetElapsedTime(started));
        return result;
    }

    private async ValueTask<ExternalInvocationResult> InvokeRegisteredAsync(
        ExternalInvocationRequest request,
        ExternalInvocationRegistration registration,
        CancellationToken cancellationToken)
    {
        var plan = runtimePlanProvider.Current;
        if (!plan.IsModuleEnabled(registration.Module)
            || !plan.IsOperationEnabled(registration.Operation.Descriptor))
        {
            return ExternalInvocationResult.Reject(
                ExternalInvocationStatus.Disabled,
                "operation-disabled");
        }

        if (!registration.Availability.Available)
        {
            return ExternalInvocationResult.Reject(
                ExternalInvocationStatus.Unavailable,
                registration.Availability.Reason ?? "module-unavailable");
        }

        var policyDecision = policy.Evaluate(
            request.Caller,
            registration.Operation.Descriptor);
        if (!policyDecision.Allowed)
        {
            return ExternalInvocationResult.Reject(
                policyDecision.RejectionStatus,
                policyDecision.ErrorCode ?? "invocation-rejected");
        }

        if (!IsRequestCompatible(registration.Operation, request.Payload))
        {
            return ExternalInvocationResult.Reject(
                ExternalInvocationStatus.InvalidRequest,
                "request-type-mismatch");
        }

        try
        {
            var value = await registration.Operation.InvokeAsync(
                request.Payload,
                cancellationToken);
            return ExternalInvocationResult.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExternalInvocationResult.Reject(
                ExternalInvocationStatus.Canceled,
                "invocation-canceled");
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "External invocation operation {OperationId} failed.",
                registration.Operation.Descriptor.OperationId);
            return ExternalInvocationResult.Reject(
                ExternalInvocationStatus.Failed,
                "operation-failed");
        }
    }

    private static bool IsRequestCompatible(
        IExternalInvocationOperation operation,
        object? request)
    {
        if (request is null)
        {
            return !operation.RequestRequired;
        }

        return operation.RequestType.IsInstanceOfType(request);
    }

    private async ValueTask WriteAuditSafelyAsync(
        ExternalInvocationRequest request,
        string? moduleId,
        ExternalInvocationAccessClass? accessClass,
        ExternalInvocationResult result,
        TimeSpan elapsed)
    {
        var auditEvent = new ExternalInvocationAuditEvent(
            request.CorrelationId,
            request.OperationId,
            moduleId,
            request.Caller.SubjectId,
            request.Caller.WindowsSid,
            request.Caller.ApplicationId,
            request.Caller.Transport,
            request.Caller.ProcessId,
            accessClass,
            result.Status,
            result.ErrorCode,
            (long)(elapsed.TotalMilliseconds * 1000),
            DateTimeOffset.UtcNow);

        try
        {
            await auditSink.WriteAsync(auditEvent, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "External invocation audit write failed.");
        }
    }
}
