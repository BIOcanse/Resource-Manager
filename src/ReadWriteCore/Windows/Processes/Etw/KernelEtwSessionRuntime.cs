using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public interface IKernelEtwSessionRuntimeFactory
{
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
    Exception cleanupFailure)
    : Exception(
        "Kernel ETW session creation failed and the partially created session was not proven released.",
        new AggregateException(creationFailure, cleanupFailure));

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

public sealed class TraceEventKernelEtwSessionRuntimeFactory(Action<string>? beforeCreate = null) : IKernelEtwSessionRuntimeFactory
{

    public bool TryGetAvailability(out string state, out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            state = "Unavailable";
            message = "当前系统不是 Windows，共享 kernel ETW 会话不可用。";
            return false;
        }

        if (TraceEventSession.IsElevated() != true)
        {
            state = "Unavailable";
            message = "共享 kernel ETW 会话需要管理员权限或性能日志权限。";
            return false;
        }

        state = "Available";
        message = "共享 kernel ETW 会话可用。";
        return true;
    }

    public IKernelEtwSessionRuntime Create(
        string sessionName,
        KernelTraceEventParser.Keywords keywords,
        KernelEtwSessionContext context,
        IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
    {
        TraceEventSession? session = null;
        try
        {
            beforeCreate?.Invoke(sessionName); // Host may explicitly supply its orphan recovery policy.
            session = new TraceEventSession(sessionName, TraceEventSessionOptions.Create)
            {
                StopOnDispose = true
            };

            // TraceEvent requires the kernel provider to be the first provider enabled for
            // the session. Enable the exact union once. Parser callbacks stay fixed;
            // consumers bind managed event sources before processing starts.
            session.EnableKernelProvider(keywords);
            var keywordControl = new KernelEtwSessionKeywordControl(
                sessionName, new WindowsKernelEtwNativeSessionControl());
            var dispatcher = new KernelEtwSubscriptionDispatcher();
            dispatcher.AttachParser(session.Source.Kernel);
            dispatcher.InitializeSubscriptions(subscriptions, context);
            return new TraceEventSessionProcessingRuntime(session, keywordControl, dispatcher);
        }
        catch (Exception startFailure)
        {
            try
            {
                StopAndDispose(session);
            }
            catch (Exception cleanupFailure)
            {
                throw new KernelEtwSessionCreationCleanupException(
                    startFailure,
                    cleanupFailure);
            }

            throw;
        }
    }

    private static void StopAndDispose(TraceEventSession? session)
    {
        if (session is null)
        {
            return;
        }

        var failures = new List<Exception>();
        try
        {
            session.Source.StopProcessing();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            if (!session.Stop(noThrow: false))
            {
                failures.Add(new InvalidOperationException(
                    "The partially created kernel ETW session did not confirm stop."));
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "The partially created kernel ETW session could not be released.",
                failures);
        }
    }
}

internal sealed class TraceEventSessionProcessingRuntime(
    TraceEventSession session,
    KernelEtwSessionKeywordControl? keywordControl = null,
    KernelEtwSubscriptionDispatcher? dispatcher = null) : IKernelEtwSessionRuntime
{
    private Task? processingTask;
    private int processing;
    private readonly object stopGate = new();
    private KernelEtwSessionStopResult? stopResult;

    public bool IsProcessing => Volatile.Read(ref processing) != 0;

    public int EventsLost => session.EventsLost;

    public void UpdateSubscriptions(
        IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
        KernelEtwSessionContext context)
    {
        lock (stopGate)
        {
            ObjectDisposedException.ThrowIf(stopResult is not null, this);
            if (keywordControl is null || dispatcher is null)
            {
                throw new InvalidOperationException("This processing runtime has no kernel keyword controller.");
            }
            dispatcher.UpdateSubscriptions(subscriptions, context, keywordControl.UpdateKeywords);
        }
    }

    public void StartProcessing(Action<Exception?> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (Interlocked.CompareExchange(ref processing, 1, 0) != 0)
        {
            throw new InvalidOperationException("ETW 事件处理已经启动。");
        }

        processingTask = Task.Run(() =>
        {
            Exception? failure = null;
            try
            {
                session.Source.Process();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Volatile.Write(ref processing, 0);
                completed(failure);
            }
        }, CancellationToken.None);
    }

    public KernelEtwSessionStopResult Stop(TimeSpan waitTimeout)
    {
        if (waitTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        }

        lock (stopGate)
        {
            if (stopResult is not null)
            {
                return stopResult;
            }

            var failures = new List<string>();
            var sessionStopSucceeded = false;
            try
            {
                // TraceEvent.Stop marks itself stopped even when ControlTrace is denied.
                // Use the owned native controller so a later reconcile really retries.
                if (keywordControl is not null)
                {
                    keywordControl.StopSession();
                    session.StopOnDispose = false;
                    sessionStopSucceeded = true;
                }
                else sessionStopSucceeded = session.Stop(noThrow: false);
                if (!sessionStopSucceeded)
                {
                    failures.Add("SessionStop: the ETW controller did not confirm stop.");
                }
            }
            catch (Exception ex) when (KernelEtwSessionKeywordControl.IsAccessDenied(ex))
            {
                // A denied control operation did not stop the owned session. Leave
                // callbacks, Process and the session handle alive; do not cache this result.
                return new(false, false, processingTask?.IsCompleted ?? true,
                    [$"SessionStop: {ex.GetType().Name}: {ex.Message}"]) { ControlDenied = true };
            }
            catch (Exception ex)
            {
                failures.Add($"SessionStop: {ex.GetType().Name}: {ex.Message}");
            }

            dispatcher?.CancelAll();
            try
            {
                session.Source.StopProcessing();
            }
            catch (Exception ex)
            {
                failures.Add($"StopProcessing: {ex.GetType().Name}: {ex.Message}");
            }

            var sessionDisposeSucceeded = false;
            try
            {
                session.Dispose();
                sessionDisposeSucceeded = true;
            }
            catch (Exception ex)
            {
                failures.Add($"SessionDispose: {ex.GetType().Name}: {ex.Message}");
            }

            var processingCompleted = processingTask is null;
            try
            {
                processingCompleted = processingTask?.Wait(waitTimeout) ?? true;
                if (!processingCompleted)
                {
                    failures.Add($"ProcessingTask: did not exit within {waitTimeout}.");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"ProcessingTask: {ex.GetType().Name}: {ex.Message}");
            }

            stopResult = new KernelEtwSessionStopResult(
                sessionStopSucceeded,
                sessionDisposeSucceeded,
                processingCompleted,
                failures.ToArray());
            return stopResult;
        }
    }

    public void Dispose()
    {
        _ = Stop(TimeSpan.FromSeconds(2));
    }
}
