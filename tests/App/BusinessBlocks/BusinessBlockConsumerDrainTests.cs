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
        using var releaseConsumer = new ManualResetEventSlim();
        var block = new ConsumerBlock(releaseConsumer);
        var registrations = new[] { Register(120, block) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        block.Diagnostics = diagnostics;
        await using var scheduler = Create(registrations);
        try
        {
            await scheduler.StartAsync(120, Reason());
            await block.Entered.Task.WaitAsync(TestTimeout);
            await State(scheduler, 120, BusinessBlockState.Running);
            Assert.Single(diagnostics.Read(120).Metrics.ActiveThreads);
            await scheduler.StopAsync(120, Reason());
            await block.ConsumerNotified.Task.WaitAsync(TestTimeout);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(120));
            Assert.False(block.Returned.Task.IsCompleted);
            Assert.Single(diagnostics.Read(120).Metrics.ActiveThreads);
            releaseConsumer.Set();
            await block.Returned.Task.WaitAsync(TestTimeout);
            await State(scheduler, 120, BusinessBlockState.Stopped);
            Assert.Empty(diagnostics.Read(120).Metrics.ActiveThreads);
            Assert.Equal(BusinessBlockOutcome.Stopped, diagnostics.Read(120).Result!.Outcome);
        }
        finally { releaseConsumer.Set(); await StopAll(scheduler); }
    }

    [Fact]
    public async Task ReportingConsumerFailureOnlyChangesBlockOwnedDiagnostics()
    {
        using var releaseConsumer = new ManualResetEventSlim();
        var block = new ConsumerBlock(releaseConsumer);
        var registrations = new[] { Register(120, block) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        block.Diagnostics = diagnostics;
        await using var scheduler = Create(registrations);
        try
        {
            await scheduler.StartAsync(120, Reason());
            await block.Entered.Task.WaitAsync(TestTimeout);
            await State(scheduler, 120, BusinessBlockState.Running);
            diagnostics.ReportFailure(120, new IOException("private consumer failed"), "consume");
            Assert.Equal(BusinessBlockOutcome.Failed, diagnostics.Read(120).Result!.Outcome);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(120));
            Assert.False(block.ConsumerNotified.Task.IsCompleted);
            Assert.Single(diagnostics.Read(120).Metrics.ActiveThreads);
        }
        finally { releaseConsumer.Set(); await StopAll(scheduler); }
        Assert.Empty(diagnostics.Read(120).Metrics.ActiveThreads);
    }

    private sealed class ConsumerBlock(ManualResetEventSlim releaseConsumer) : IBusinessBlock
    {
        public BusinessBlockDiagnosticsRegistry Diagnostics { get; set; } = null!;
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource ConsumerNotified { get; } = Signal();
        public TaskCompletionSource Returned { get; } = Signal();
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            Diagnostics.BeginRun(120);
            var consumer = DedicatedEtwConsumerThread.Start(120, "private-session", () =>
            {
                using var accounting = Diagnostics.RegisterConsumerThread(120, "private-session");
                Entered.TrySetResult();
                if (!context.StopNotification.WaitHandle.WaitOne(TestTimeout)) throw new TimeoutException("No stop notification arrived.");
                ConsumerNotified.TrySetResult();
                if (!releaseConsumer.Wait(TestTimeout)) throw new TimeoutException("The consumer was not released.");
            });
            await Entered.Task;
            context.ReportRunning();
            // Resource ownership belongs here: returning before this task ends would be a block bug.
            await consumer;
            Diagnostics.RecordResult(120, BusinessBlockResult.Stopped);
            Returned.TrySetResult();
        }
    }
}
