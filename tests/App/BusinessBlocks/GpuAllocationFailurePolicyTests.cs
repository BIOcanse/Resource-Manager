using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationFailurePolicyTests
{
    [Theory]
    [InlineData(BusinessBlockFailurePolicy.RecordOnly, false)]
    [InlineData(BusinessBlockFailurePolicy.StopDependents, true)]
    public async Task SessionFailureUsesTheConfiguredPolicyForItsConsumer(
        BusinessBlockFailurePolicy policy, bool stopsConsumer)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var factory = new GpuAllocationTestSessionFactory();
        var consumer = new Consumer();
        GpuAllocationCollector? collector = null;
        await using var controller = new BusinessBlockController(services,
            [new("gpu-allocation-etw", [], _ => collector!), new("consumer", ["gpu-allocation-etw"], _ => consumer)],
            Options.Create(new BusinessBlockOptions
            {
                Blocks = new() { ["gpu-allocation-etw"] = new() { OnFailure = policy } }
            }), NullLogger<BusinessBlockController>.Instance);
        collector = new GpuAllocationCollector(NullLogger<GpuAllocationCollector>.Instance,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), controller, factory, TimeProvider.System);
        var dependency = controller.AcquireDependency("consumer", new(BusinessBlockRequestKind.Dependency, "test", "allocation-output", 1));
        try
        {
            await dependency.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            factory.Current.End(new IOException("session failure"));
            Assert.Equal(BusinessBlockOutcome.Failed,
                (await controller.GetCompletion("gpu-allocation-etw").WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
            if (stopsConsumer) await consumer.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(stopsConsumer, consumer.Stopped.Task.IsCompleted);
            Assert.Equal(policy, controller.Read().Blocks.Single(block => block.Id == "gpu-allocation-etw").Policy.OnFailure);
        }
        finally
        {
            if (stopsConsumer)
                await Assert.ThrowsAsync<InvalidOperationException>(() => dependency.DisposeAsync().AsTask());
            else await dependency.DisposeAsync();
        }
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, factory.Current.Stops);
    }

    private sealed class Consumer : IBusinessBlock
    {
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stopNotification); }
            catch (OperationCanceledException) when (stopNotification.IsCancellationRequested) { }
            Stopped.TrySetResult();
            return BusinessBlockResult.Stopped;
        }
    }
}
