using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using static Resource_Manager_APP.Tests.BusinessBlockTestSupport;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockSchedulerFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningEntryTriggersItsParentToRestartWhenStillDemandedWithoutFailingAnotherBlock(bool throws)
    {
        var releaseFirst = Signal();
        var firstEntered = Signal();
        var secondEntered = Signal();
        var runs = 0;
        var peer = new Probe();
        BusinessBlockDiagnosticsRegistry? diagnostics = null;
        var worker = new Block(async context =>
        {
            var run = Interlocked.Increment(ref runs);
            diagnostics!.BeginRun(154);
            context.ReportRunning();
            if (run == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
                if (throws)
                {
                    var error = new IOException("the block's own error");
                    diagnostics.RecordResult(154, BusinessBlockResult.Failed(error));
                    throw error;
                }
                diagnostics.RecordResult(154, BusinessBlockResult.Done);
                return;
            }
            secondEntered.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
            diagnostics.RecordResult(154, BusinessBlockResult.Stopped);
        });
        var registrations = new[] { Register(154, worker), Register(140, peer) };
        diagnostics = new(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        await using var scheduler = Create(registrations);
        try
        {
            await scheduler.StartAsync(154, Reason());
            await firstEntered.Task.WaitAsync(TestTimeout);
            var peerRequest = scheduler.StartAsync(140, Reason(owner: "unrelated-owner"));
            releaseFirst.TrySetResult();
            await peerRequest.WaitAsync(TestTimeout);
            await peer.Entered.Task.WaitAsync(TestTimeout);
            await secondEntered.Task.WaitAsync(TestTimeout);
            Assert.Equal(2, Volatile.Read(ref runs));
            Assert.Equal(1, peer.Runs);
            Assert.False(peer.Notified.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(154));
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(140));
            var workerHistory = scheduler.Read().History.Where(item => item.BlockId == 154).ToArray();
            Assert.Equal([BusinessBlockState.Starting, BusinessBlockState.Running, BusinessBlockState.Stopped,
                BusinessBlockState.Starting, BusinessBlockState.Running], workerHistory.Select(item => item.To));
        }
        finally { releaseFirst.TrySetResult(); await StopAll(scheduler); }
    }

    [Fact]
    public async Task DiagnosticFailureCannotCancelABlockOrWithdrawAnyDemand()
    {
        var first = new Probe(); var peer = new Probe();
        var registrations = new[] { Register(124, first), Register(140, peer) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        await using var scheduler = Create(registrations);
        try
        {
            await scheduler.StartAsync(124, Reason());
            await scheduler.StartAsync(140, Reason());
            await Task.WhenAll(first.Entered.Task, peer.Entered.Task).WaitAsync(TestTimeout);
            var before = scheduler.Read();
            diagnostics.ReportFailure(124, new IOException("reported by the block"), "read");
            Assert.Equal(BusinessBlockOutcome.Failed, diagnostics.Read(124).Result!.Outcome);
            Assert.Equal(before.History, scheduler.Read().History);
            Assert.Equal(before.Demands.OrderBy(item => item.Key), scheduler.Read().Demands.OrderBy(item => item.Key));
            Assert.False(first.Notified.Task.IsCompleted);
            Assert.False(peer.Notified.Task.IsCompleted);
        }
        finally { await StopAll(scheduler); }
    }
}
