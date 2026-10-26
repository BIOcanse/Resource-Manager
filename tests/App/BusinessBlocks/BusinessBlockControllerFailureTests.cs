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
            diagnostics!.BeginRun("worker");
            context.ReportRunning();
            if (run == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
                if (throws)
                {
                    var error = new IOException("the block's own error");
                    diagnostics.RecordResult("worker", BusinessBlockResult.Failed(error));
                    throw error;
                }
                diagnostics.RecordResult("worker", BusinessBlockResult.Done);
                return;
            }
            secondEntered.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
            diagnostics.RecordResult("worker", BusinessBlockResult.Stopped);
        });
        var registrations = new[] { Register("worker", worker), Register("peer", peer) };
        diagnostics = new(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        await using var scheduler = Create(registrations);
        try
        {
            await scheduler.StartAsync("worker", Reason());
            await firstEntered.Task.WaitAsync(TestTimeout);
            var peerRequest = scheduler.StartAsync("peer", Reason(owner: "unrelated-owner"));
            releaseFirst.TrySetResult();
            await peerRequest.WaitAsync(TestTimeout);
            await peer.Entered.Task.WaitAsync(TestTimeout);
            await secondEntered.Task.WaitAsync(TestTimeout);
            Assert.Equal(2, Volatile.Read(ref runs));
            Assert.Equal(1, peer.Runs);
            Assert.False(peer.Notified.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState("worker"));
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState("peer"));
            var workerHistory = scheduler.Read().History.Where(item => item.BlockId == "worker").ToArray();
            Assert.Equal([BusinessBlockState.Starting, BusinessBlockState.Running, BusinessBlockState.Stopped,
                BusinessBlockState.Starting, BusinessBlockState.Running], workerHistory.Select(item => item.To));
        }
        finally { releaseFirst.TrySetResult(); await StopAll(scheduler); }
    }

    [Fact]
    public async Task DiagnosticFailureCannotCancelABlockOrWithdrawAnyDemand()
    {
        var first = new Probe(); var peer = new Probe();
        var registrations = new[] { Register("first", first), Register("peer", peer) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        await using var scheduler = Create(registrations);
        try
        {
            await scheduler.StartAsync("first", Reason());
            await scheduler.StartAsync("peer", Reason());
            await Task.WhenAll(first.Entered.Task, peer.Entered.Task).WaitAsync(TestTimeout);
            var before = scheduler.Read();
            diagnostics.ReportFailure("first", new IOException("reported by the block"), "read");
            Assert.Equal(BusinessBlockOutcome.Failed, diagnostics.Read("first").Result!.Outcome);
            Assert.Equal(before.History, scheduler.Read().History);
            Assert.Equal(before.Demands.OrderBy(item => item.Key), scheduler.Read().Demands.OrderBy(item => item.Key));
            Assert.False(first.Notified.Task.IsCompleted);
            Assert.False(peer.Notified.Task.IsCompleted);
        }
        finally { await StopAll(scheduler); }
    }
}
