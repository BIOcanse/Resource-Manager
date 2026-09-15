using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.Tracing.Session;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal readonly record struct ProcessOwnedEtwSessionOwner(
    int ProcessId,
    long? ProcessStartUtcTicks);

internal static class ResourceManagerEtwSessionNames
{
    public const string KernelPrefix = "ResourceManagerKernelTelemetry";
    public const string DxgkrnlVidMmPrefix = "ResourceManagerDxgKrnlVidMm";

    public static ProcessOwnedEtwSessionOwner CurrentOwner { get; } = ReadCurrentOwner();

    public static string KernelSessionName { get; } = Create(KernelPrefix, CurrentOwner);

    public static string DxgkrnlVidMmSessionName { get; } = Create(DxgkrnlVidMmPrefix, CurrentOwner);

    public static string Create(string prefix, ProcessOwnedEtwSessionOwner owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.EndsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("ETW session prefix must not end with a separator.", nameof(prefix));
        }

        if (owner.ProcessId <= 0 || owner.ProcessStartUtcTicks is not > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(owner), "An owned ETW session requires a positive PID and process start identity.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}-{owner.ProcessId}-{owner.ProcessStartUtcTicks.Value}");
    }

    public static bool TryParse(
        string prefix,
        string sessionName,
        out ProcessOwnedEtwSessionOwner owner)
    {
        owner = default;
        if (string.IsNullOrWhiteSpace(prefix)
            || string.IsNullOrWhiteSpace(sessionName)
            || prefix.EndsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        var expectedPrefix = $"{prefix}-";
        if (!sessionName.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var identity = sessionName.AsSpan(expectedPrefix.Length);
        var separator = identity.IndexOf('-');
        ReadOnlySpan<char> processIdText;
        ReadOnlySpan<char> processStartText;
        if (separator < 0)
        {
            processIdText = identity;
            processStartText = [];
        }
        else
        {
            processIdText = identity[..separator];
            processStartText = identity[(separator + 1)..];
            if (processStartText.IndexOf('-') >= 0)
            {
                return false;
            }
        }

        if (!TryParseCanonicalPositiveInt(processIdText, out var processId))
        {
            return false;
        }

        if (processStartText.IsEmpty)
        {
            owner = new ProcessOwnedEtwSessionOwner(processId, null);
            return true;
        }

        if (!TryParseCanonicalPositiveLong(processStartText, out var processStartUtcTicks))
        {
            return false;
        }

        owner = new ProcessOwnedEtwSessionOwner(processId, processStartUtcTicks);
        return true;
    }

    private static ProcessOwnedEtwSessionOwner ReadCurrentOwner()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessOwnedEtwSessionOwner(
            Environment.ProcessId,
            process.StartTime.ToUniversalTime().Ticks);
    }

    private static bool TryParseCanonicalPositiveInt(ReadOnlySpan<char> text, out int value)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value > 0
            && text.SequenceEqual(value.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryParseCanonicalPositiveLong(ReadOnlySpan<char> text, out long value)
    {
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value > 0
            && text.SequenceEqual(value.ToString(CultureInfo.InvariantCulture));
    }
}

internal enum ProcessOwnedEtwSessionOwnerState
{
    Missing,
    Matching,
    Different,
    Unknown
}

internal interface IProcessOwnedEtwSessionProcessInspector
{
    ProcessOwnedEtwSessionOwnerState Inspect(ProcessOwnedEtwSessionOwner owner);
}

internal sealed class WindowsProcessOwnedEtwSessionProcessInspector : IProcessOwnedEtwSessionProcessInspector
{
    public ProcessOwnedEtwSessionOwnerState Inspect(ProcessOwnedEtwSessionOwner owner)
    {
        if (owner.ProcessId <= 0)
        {
            return ProcessOwnedEtwSessionOwnerState.Unknown;
        }

        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            if (process.HasExited)
            {
                return ProcessOwnedEtwSessionOwnerState.Missing;
            }

            if (owner.ProcessStartUtcTicks is null)
            {
                return ProcessOwnedEtwSessionOwnerState.Matching;
            }

            var actualStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            return actualStartUtcTicks == owner.ProcessStartUtcTicks.Value
                ? ProcessOwnedEtwSessionOwnerState.Matching
                : ProcessOwnedEtwSessionOwnerState.Different;
        }
        catch (ArgumentException)
        {
            return ProcessOwnedEtwSessionOwnerState.Missing;
        }
        catch (InvalidOperationException)
        {
            return ProcessOwnedEtwSessionOwnerState.Missing;
        }
        catch (Win32Exception)
        {
            return ProcessOwnedEtwSessionOwnerState.Unknown;
        }
        catch (NotSupportedException)
        {
            return ProcessOwnedEtwSessionOwnerState.Unknown;
        }
    }
}

internal interface IProcessOwnedEtwSessionCatalog
{
    IReadOnlyList<string> GetActiveSessionNames();

    IProcessOwnedEtwSessionAttachment? TryAttach(string sessionName);
}

internal interface IProcessOwnedEtwSessionAttachment : IDisposable
{
    bool Stop();
}

internal sealed class TraceEventProcessOwnedEtwSessionCatalog : IProcessOwnedEtwSessionCatalog
{
    public IReadOnlyList<string> GetActiveSessionNames()
    {
        return TraceEventSession.GetActiveSessionNames().ToArray();
    }

    public IProcessOwnedEtwSessionAttachment? TryAttach(string sessionName)
    {
        var session = TraceEventSession.GetActiveSession(sessionName);
        return session is null
            ? null
            : new TraceEventProcessOwnedEtwSessionAttachment(session);
    }
}

internal sealed class TraceEventProcessOwnedEtwSessionAttachment : IProcessOwnedEtwSessionAttachment
{
    private readonly TraceEventSession session;

    public TraceEventProcessOwnedEtwSessionAttachment(TraceEventSession session)
    {
        this.session = session;
        this.session.StopOnDispose = false;
    }

    public bool Stop()
    {
        return session.Stop(noThrow: true);
    }

    public void Dispose()
    {
        session.StopOnDispose = false;
        session.Dispose();
    }
}

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
