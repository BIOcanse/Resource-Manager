using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using static Resource_Manager_APP.Tests.BusinessBlockTestSupport;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockConsumerDrainTests
{
    [Fact]
    public async Task BlockKeepsStoppingUntilItsOwnedConsumerHasDrainedAndReleasedAccounting()
    {
        const int blockId = BackendUids.RuntimeSpecializationCoordinator;
        using var releaseConsumer = new ManualResetEventSlim();
        var block = new ConsumerBlock(blockId, releaseConsumer);
        var registrations = new[] { new BusinessBlockRegistration(blockId, _ => block) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        block.Diagnostics = diagnostics;
        var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(new Dictionary<int, IBusinessBlock>
        {
            [blockId] = block
        });
        await using var owner = fixture;
        var scheduler = fixture.Scheduler;
        try
        {
            await scheduler.StartAsync(blockId, Reason());
            await block.Entered.Task.WaitAsync(TestTimeout);
            await State(scheduler, blockId, BusinessBlockState.Running);
            Assert.Single(diagnostics.Read(blockId).Metrics.ActiveThreads);
            await scheduler.StopAsync(blockId, Reason());
            await block.ConsumerNotified.Task.WaitAsync(TestTimeout);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(blockId));
            Assert.False(block.Returned.Task.IsCompleted);
            Assert.Single(diagnostics.Read(blockId).Metrics.ActiveThreads);
            releaseConsumer.Set();
            await block.Returned.Task.WaitAsync(TestTimeout);
            await State(scheduler, blockId, BusinessBlockState.Stopped);
            Assert.Empty(diagnostics.Read(blockId).Metrics.ActiveThreads);
            Assert.Equal(BusinessBlockOutcome.Stopped, diagnostics.Read(blockId).Result!.Outcome);
        }
        finally { releaseConsumer.Set(); await StopAll(scheduler); }
    }

    [Fact]
    public async Task ReportingConsumerFailureOnlyChangesBlockOwnedDiagnostics()
    {
        const int blockId = BackendUids.RuntimeSpecializationCoordinator;
        using var releaseConsumer = new ManualResetEventSlim();
        var block = new ConsumerBlock(blockId, releaseConsumer);
        var registrations = new[] { new BusinessBlockRegistration(blockId, _ => block) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        block.Diagnostics = diagnostics;
        var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(new Dictionary<int, IBusinessBlock>
        {
            [blockId] = block
        });
        await using var owner = fixture;
        var scheduler = fixture.Scheduler;
        try
        {
            await scheduler.StartAsync(blockId, Reason());
            await block.Entered.Task.WaitAsync(TestTimeout);
            await State(scheduler, blockId, BusinessBlockState.Running);
            diagnostics.ReportFailure(blockId, new IOException("private consumer failed"), "consume");
            Assert.Equal(BusinessBlockOutcome.Failed, diagnostics.Read(blockId).Result!.Outcome);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(blockId));
            Assert.False(block.ConsumerNotified.Task.IsCompleted);
            Assert.Single(diagnostics.Read(blockId).Metrics.ActiveThreads);
        }
        finally { releaseConsumer.Set(); await StopAll(scheduler); }
        Assert.Empty(diagnostics.Read(blockId).Metrics.ActiveThreads);
    }

    private sealed class ConsumerBlock(int blockId, ManualResetEventSlim releaseConsumer) : IBusinessBlock
    {
        public BusinessBlockDiagnosticsRegistry Diagnostics { get; set; } = null!;
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource ConsumerNotified { get; } = Signal();
        public TaskCompletionSource Returned { get; } = Signal();
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            Diagnostics.BeginRun(blockId);
            var consumer = DedicatedEtwConsumerThread.Start(blockId, "private-session", () =>
            {
                using var accounting = Diagnostics.RegisterConsumerThread(blockId, "private-session");
                Entered.TrySetResult();
                if (!context.StopNotification.WaitHandle.WaitOne(TestTimeout)) throw new TimeoutException("No stop notification arrived.");
                ConsumerNotified.TrySetResult();
                if (!releaseConsumer.Wait(TestTimeout)) throw new TimeoutException("The consumer was not released.");
            });
            await Entered.Task;
            context.ReportRunning();
            // Resource ownership belongs here: returning before this task ends would be a block bug.
            await consumer;
            Diagnostics.RecordResult(blockId, BusinessBlockResult.Stopped);
            Returned.TrySetResult();
        }
    }
}
