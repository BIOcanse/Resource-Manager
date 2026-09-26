using ResourceManager.App.Application.ExternalInvocation;
using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Infrastructure.ExternalInvocation.Audit;

public sealed class LoggingExternalInvocationAuditSink(
    ILogger<LoggingExternalInvocationAuditSink> logger) : IExternalInvocationAuditSink
{
    public ValueTask WriteAsync(
        ExternalInvocationAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        if (auditEvent.Status == ExternalInvocationStatus.Succeeded
            && auditEvent.AccessClass is ExternalInvocationAccessClass.Open)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                Log(LogLevel.Debug, auditEvent);
            }
        }
        else if (auditEvent.Status == ExternalInvocationStatus.Succeeded)
        {
            Log(LogLevel.Information, auditEvent);
        }
        else
        {
            Log(LogLevel.Warning, auditEvent);
        }

        return ValueTask.CompletedTask;
    }

    private void Log(LogLevel level, ExternalInvocationAuditEvent auditEvent)
    {
        logger.Log(
            level,
            "External invocation {OperationId} completed with {Status} for {SubjectId}/{ApplicationId} in {ElapsedMicroseconds} us (correlation {CorrelationId}, error {ErrorCode}).",
            auditEvent.OperationId,
            auditEvent.Status,
            auditEvent.SubjectId,
            auditEvent.ApplicationId,
            auditEvent.ElapsedMicroseconds,
            auditEvent.CorrelationId,
            auditEvent.ErrorCode);
    }
}
