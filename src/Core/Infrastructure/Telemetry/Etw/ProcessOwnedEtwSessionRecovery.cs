using Microsoft.Extensions.Logging;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// Product orphan scanning policy remains explicit in the Host.
internal sealed record ProcessOwnedEtwSessionRecoveryResult(
    int ProductSessionCount,
    int OrphanCandidateCount,
    int StoppedCount,
    int FailureCount);

internal sealed class ProcessOwnedEtwSessionOrphanReconciler(
    IProcessOwnedEtwSessionCatalog sessionCatalog,
    IProcessOwnedEtwSessionProcessInspector processInspector,
    ILogger logger)
{
    public static ProcessOwnedEtwSessionOrphanReconciler CreateDefault(ILogger logger)
    {
        return new ProcessOwnedEtwSessionOrphanReconciler(
            new TraceEventProcessOwnedEtwSessionCatalog(),
            new WindowsProcessOwnedEtwSessionProcessInspector(),
            logger);
    }

    public ProcessOwnedEtwSessionRecoveryResult Reconcile(
        string sessionPrefix,
        ProcessOwnedEtwSessionOwner currentOwner,
        string currentSessionName)
    {
        IReadOnlyList<string> activeSessionNames;
        try
        {
            activeSessionNames = sessionCatalog.GetActiveSessionNames();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate active ETW sessions before creating {SessionName}.", currentSessionName);
            return new ProcessOwnedEtwSessionRecoveryResult(0, 0, 0, 1);
        }

        var productSessionCount = 0;
        var orphanCandidateCount = 0;
        var stoppedCount = 0;
        var failureCount = 0;
        foreach (var sessionName in activeSessionNames.Order(StringComparer.Ordinal))
        {
            if (!ResourceManagerEtwSessionNames.TryParse(sessionPrefix, sessionName, out var owner))
            {
                continue;
            }

            productSessionCount++;
            if (string.Equals(sessionName, currentSessionName, StringComparison.Ordinal)
                || owner == currentOwner
                || !IsConfirmedOrphan(owner))
            {
                continue;
            }

            orphanCandidateCount++;
            try
            {
                using var attachment = sessionCatalog.TryAttach(sessionName);
                if (attachment is null || !IsConfirmedOrphan(owner))
                {
                    continue;
                }

                if (attachment.Stop())
                {
                    stoppedCount++;
                }
                else
                {
                    logger.LogDebug("ETW orphan session {SessionName} disappeared before it could be stopped.", sessionName);
                }
            }
            catch (Exception ex)
            {
                failureCount++;
                logger.LogWarning(ex, "Failed to stop confirmed Resource Manager ETW orphan session {SessionName}.", sessionName);
            }
        }

        if (stoppedCount > 0)
        {
            logger.LogInformation(
                "Stopped {StoppedCount} confirmed Resource Manager ETW orphan sessions for prefix {SessionPrefix}.",
                stoppedCount,
                sessionPrefix);
        }

        return new ProcessOwnedEtwSessionRecoveryResult(
            productSessionCount,
            orphanCandidateCount,
            stoppedCount,
            failureCount);
    }

    private bool IsConfirmedOrphan(ProcessOwnedEtwSessionOwner owner)
    {
        var state = processInspector.Inspect(owner);
        return state is ProcessOwnedEtwSessionOwnerState.Missing
            or ProcessOwnedEtwSessionOwnerState.Different;
    }
}
