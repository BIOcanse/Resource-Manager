using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationFailurePolicyTests
{
    [Fact]
    public async Task BlockDiagnosticsCannotWithdrawAConsumerOrChangeItsControlState()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var consumer = new Consumer();
        BusinessBlockRegistration[] declarations = [new("consumer", [], _ => consumer)];
        var diagnostics = new BusinessBlockDiagnosticsRegistry(declarations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        await using var scheduler = new BusinessBlockScheduler(services, declarations,
            Options.Create(new BusinessBlockOptions()), NullLogger<BusinessBlockScheduler>.Instance);
        var dependency = scheduler.AcquireDependency("consumer", new(BusinessBlockRequestKind.Dependency, "test", "allocation-output", 1));
        await EtwTestRunRecorder.ReadyAsync(dependency);
        await consumer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        diagnostics.ReportFailure("consumer", new IOException("reported data failure"), "allocation-consumer");
        diagnostics.RecordResult("consumer", BusinessBlockResult.Failed(new IOException("recorded result")));
        Assert.Equal(BusinessBlockState.Running, scheduler.ReadState("consumer"));
        Assert.Single(scheduler.Read().Demands);
        Assert.False(dependency.Released.IsCompleted);
        Assert.False(consumer.Stopped.Task.IsCompleted);
        await EtwTestRunRecorder.ReleaseAsync(dependency);
        await consumer.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class Consumer : IBusinessBlock
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning();
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
            Stopped.TrySetResult();
        }
    }
}
