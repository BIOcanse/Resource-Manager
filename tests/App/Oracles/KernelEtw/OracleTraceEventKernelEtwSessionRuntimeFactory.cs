using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;
internal sealed class OracleTraceEventKernelEtwSessionRuntimeFactory(
    Action<string>? beforeCreate = null,
    IEtwConsumerThreadObserver? consumerThreadObserver = null,
    int? blockId = null)
{
    private readonly int? consumerBlockId = DedicatedEtwConsumerThread.ValidateBlockId(blockId, consumerThreadObserver);

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

    public OracleTraceEventSessionProcessingRuntime Create(
        string sessionName,
        KernelTraceEventParser.Keywords keywords,
        KernelEtwSessionContext context,
        IReadOnlyList<OracleKernelEtwSubscriptionRegistration> subscriptions)
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
            var dispatcher = new OracleKernelEtwSubscriptionDispatcher();
            dispatcher.AttachParser(session.Source.Kernel);
            dispatcher.InitializeSubscriptions(subscriptions, context);
            return new OracleTraceEventSessionProcessingRuntime(
                session,
                keywordControl,
                dispatcher,
                consumerThreadObserver,
                consumerBlockId);
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
