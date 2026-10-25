using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockControllerFailureTests
{
    private static readonly BusinessBlockReason Reason = new(BusinessBlockRequestKind.User, "test", "explicit", 1);

    [Fact]
    public async Task FailedRestartDoesNotExposeThePreviousRunsCompletion()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var creations = 0;
        await using var controller = Create(services, new("worker", [], _ =>
        {
            if (++creations == 2) throw new IOException("creation failed");
            return new CallbackBlock(_ => Task.FromResult(BusinessBlockResult.Done));
        }));
        await controller.StartAsync("worker", Reason);
        await controller.GetCompletion("worker");

        await Assert.ThrowsAsync<IOException>(() => controller.StartAsync("worker", Reason));

        Assert.Throws<InvalidOperationException>(() => { _ = controller.GetCompletion("worker"); });
        Assert.Equal(BusinessBlockState.Failed, Assert.Single(controller.Read().Blocks).State);
    }

    [Fact]
    public async Task StopNotificationFailureIsRecordedAsFailedAndPropagated()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var controller = Create(services, new("worker", [], _ => new CallbackBlock(async stop =>
        {
            // Not disposed when the run ends: the block's continuation can observe cancellation and
            // leave before the canceling thread reaches this callback, which would unregister it.
            stop.Register(() => throw new IOException("callback failure"));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            return BusinessBlockResult.Stopped;
        })));
        await controller.StartAsync("worker", Reason);

        var error = await Assert.ThrowsAsync<AggregateException>(() => controller.StopAsync("worker", Reason));

        Assert.Contains(error.Flatten().InnerExceptions, item => item is IOException { Message: "callback failure" });
        var facts = Assert.Single(controller.Read().Blocks);
        Assert.Equal(BusinessBlockState.Failed, facts.State);
        Assert.Equal(BusinessBlockTransitionOutcome.Failed, facts.Outcome);
        Assert.False(facts.ExecutionOutstanding);
    }

    private static BusinessBlockController Create(IServiceProvider services, BusinessBlockRegistration registration)
        => new(services, [registration], Options.Create(new BusinessBlockOptions { MetricsInterval = null }),
            NullLogger<BusinessBlockController>.Instance);

    private sealed class CallbackBlock(Func<CancellationToken, Task<BusinessBlockResult>> run) : IBusinessBlock
    {
        public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification) => run(stopNotification);
    }
}
