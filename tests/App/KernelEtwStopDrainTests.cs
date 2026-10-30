using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwStopDrainTests
{
    [Fact]
    public void StopsThenDrainsBeforeClosingTraceAndDisposingSession()
    {
        var cleanup = new KernelEtwSessionRuntimeCleanup();
        var order = new List<string>();
        var budget = TimeSpan.FromMilliseconds(125);
        var result = cleanup.Stop(budget,
            () => order.Add("stop-session"), () => order.Add("cancel"),
            timeout => { Assert.Equal(budget, timeout); order.Add("wait"); return true; },
            () => order.Add("stop-processing"), () => order.Add("close-trace"), () => order.Add("dispose"));

        Assert.True(result.IsReleased);
        Assert.Equal(new[] { "stop-session", "cancel", "wait", "stop-processing", "close-trace", "dispose" }, order);
        var repeated = cleanup.Stop(budget,
            Fail, Fail, _ => throw new InvalidOperationException("Completed wait repeated"), Fail, Fail, Fail);
        Assert.True(repeated.IsReleased);
    }

    [Fact]
    public void TimeoutForcesCloseAndReportsIncompleteEvenWhenCloseUnblocksConsumer()
    {
        var cleanup = new KernelEtwSessionRuntimeCleanup();
        var order = new List<string>();
        var completed = false;
        var result = cleanup.Stop(TimeSpan.Zero,
            () => order.Add("stop-session"), () => order.Add("cancel"),
            timeout => { Assert.Equal(TimeSpan.Zero, timeout); order.Add("wait"); return completed; },
            () => order.Add("stop-processing"),
            () => { order.Add("close-trace"); completed = true; }, () => order.Add("dispose"));

        Assert.True(result.SessionStopSucceeded);
        Assert.True(result.SessionDisposeSucceeded);
        Assert.False(result.ProcessingCompleted);
        Assert.False(result.IsReleased);
        Assert.Contains("natural drain", Assert.Single(result.Failures));
        Assert.Equal(new[] { "stop-session", "cancel", "wait", "stop-processing", "close-trace", "dispose" }, order);
        var retried = cleanup.Stop(TimeSpan.Zero, Fail, Fail, _ => completed, Fail, Fail, Fail);
        Assert.True(retried.IsReleased);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExplicitStopRetriesOnlyFailedCleanupSteps(int failedStep)
    {
        var cleanup = new KernelEtwSessionRuntimeCleanup();
        var attempts = new int[4];
        var fail = true;
        var actions = Enumerable.Range(0, 4).Select(index => (Action)(() =>
        {
            attempts[index]++;
            if (fail && index == failedStep) throw new IOException("Injected cleanup failure");
        })).ToArray();
        Assert.False(cleanup.Stop(TimeSpan.Zero, actions[0], static () => { }, _ => true,
            actions[1], actions[2], actions[3]).IsReleased);
        fail = false;
        Assert.True(cleanup.Stop(TimeSpan.Zero, actions[0], static () => { }, _ => true,
            actions[1], actions[2], actions[3]).IsReleased);
        Assert.Equal(Enumerable.Range(0, 4).Select(index => index == failedStep ? 2 : 1), attempts);
    }

    [Fact]
    public void FailedTraceCloseDefersSessionDisposeWhichWouldOtherwiseRetryClosingTheSource()
    {
        var cleanup = new KernelEtwSessionRuntimeCleanup();
        var order = new List<string>();
        var first = cleanup.Stop(TimeSpan.Zero, static () => { }, static () => { }, _ => true,
            static () => { }, () => { order.Add("close-failed"); throw new IOException("close failure"); }, Fail);
        Assert.False(first.IsReleased);
        Assert.False(first.SessionDisposeSucceeded);
        Assert.Equal(new[] { "close-failed" }, order);
        Assert.True(cleanup.Stop(TimeSpan.Zero, Fail, Fail, _ => throw new InvalidOperationException(), Fail,
            () => order.Add("close"), () => order.Add("dispose")).IsReleased);
        Assert.Equal(new[] { "close-failed", "close", "dispose" }, order);
    }

    [Fact]
    public void DeniedStopLeavesConsumerIntactUntilAnExplicitRetry()
    {
        var cleanup = new KernelEtwSessionRuntimeCleanup();
        var denied = cleanup.Stop(TimeSpan.Zero, () => throw new UnauthorizedAccessException("denied"),
            Fail, _ => throw new InvalidOperationException("Wait after denied stop"), Fail, Fail, Fail);
        Assert.True(denied.ControlDenied);
        Assert.False(denied.IsReleased);
        Assert.True(cleanup.Stop(TimeSpan.Zero, static () => { }, static () => { }, _ => true,
            static () => { }, static () => { }, static () => { }).IsReleased);
    }

    private static void Fail() => throw new InvalidOperationException("Completed cleanup step repeated");

    [Fact]
    public void RuntimeDisposalDoesNotSuppressItsIncompleteStopResult()
    {
        using var session = new Microsoft.Diagnostics.Tracing.Session.TraceEventSession("ResourceManager.GenericDrain.Test")
        { StopOnDispose = false };
        var runtime = new TraceEventSessionProcessingRuntime(session);
        // The generic runtime owns no kernel controller. Confirm this fixture's
        // no-op provider stop without opening a native ETW session.
        Cleanup(runtime).StopSession(static () => { });
        // No ETW source/session is opened. Supply an already failed consumer so
        // the actual runtime's initial Stop returns an incomplete drain result.
        var taskField = typeof(TraceEventSessionProcessingRuntime).GetField("processingTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        taskField.SetValue(runtime, Task.FromException(new IOException("fixture consumer failure")));
        try
        {
            var failure = Assert.Throws<AggregateException>(runtime.Dispose);
            Assert.Contains(failure.InnerExceptions, error => error.Message.StartsWith("ETW stop remains incomplete:"));
        }
        finally
        {
            taskField.SetValue(runtime, Task.CompletedTask);
            runtime.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenericConsumerCannotStartAfterStop(bool externalProcess)
    {
        using var session = new Microsoft.Diagnostics.Tracing.Session.TraceEventSession("ResourceManager.GenericDrain.Start.Test")
        { StopOnDispose = false };
        using var runtime = new TraceEventSessionProcessingRuntime(session);
        Cleanup(runtime).StopSession(static () => { });
        Assert.True(runtime.Stop(TimeSpan.Zero).IsReleased);

        if (externalProcess) Assert.Throws<ObjectDisposedException>(runtime.Process);
        else Assert.Throws<ObjectDisposedException>(() => runtime.StartProcessing(static _ => { }));
    }

    private static KernelEtwSessionRuntimeCleanup Cleanup(TraceEventSessionProcessingRuntime runtime)
        => (KernelEtwSessionRuntimeCleanup)typeof(TraceEventSessionProcessingRuntime).GetField("cleanup",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runtime)!;
}
