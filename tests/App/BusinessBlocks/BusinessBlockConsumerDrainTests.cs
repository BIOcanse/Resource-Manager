using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockConsumerDrainTests
{
    [Fact]
    public async Task RunFailureIsVisibleBeforeItsPrivateConsumerHasDrained()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var release = new ManualResetEventSlim();
        var legacy = new LegacyBlock(release, failBeforeStop: true);
        await using var controller = new BusinessBlockController(services,
            [new("consumer", [], _ => legacy)], Options.Create(new BusinessBlockOptions
            { MetricsInterval = null, Policy = new() { StopDeadline = TimeSpan.FromSeconds(5) } }),
            NullLogger<BusinessBlockController>.Instance);
        legacy.Controller = controller;
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, "test", "enabled", 1);
        await controller.StartAsync("consumer", reason);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Assert.Single(controller.Read().Blocks).State != BusinessBlockState.Failed)
                await Task.Delay(10, timeout.Token);
            var facts = Assert.Single(controller.Read().Blocks);
            Assert.True(facts.ExecutionOutstanding);
            Assert.Single(facts.Metrics.ActiveThreads);
        }
        finally { release.Set(); }
        await controller.StopAsync("consumer", reason);
        Assert.Equal(BusinessBlockOutcome.Failed, (await controller.GetCompletion("consumer")).Outcome);
        Assert.False(Assert.Single(controller.Read().Blocks).ExecutionOutstanding);
    }

    [Fact]
    public async Task ExistingLegacyConsumerDoesNotPreventItsHostedLifetimeFromStarting()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new BusinessBlockController(services,
            [new("consumer", [], _ => new CooperativeBlock(release))], Options.Create(new BusinessBlockOptions
            { MetricsInterval = null, Policy = new() { StopDeadline = TimeSpan.FromSeconds(5) } }),
            NullLogger<BusinessBlockController>.Instance);
        var consumer = DedicatedEtwConsumerThread.Start("legacy", "before-host", () =>
        {
            using var registration = controller.RegisterConsumerThread("consumer", "before-host");
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Consumer was not released.");
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, "host", "enabled", 1);
            await controller.StartAsync("consumer", reason);
            Assert.Single(Assert.Single(controller.Read().Blocks).Metrics.ActiveThreads);
            await controller.StopAsync("consumer", reason);
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(Assert.Single(controller.Read().Blocks).Metrics.ActiveThreads);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task StopDeadlineCoversRegisteredConsumerAfterLegacyRunHasReturned()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var releaseConsumer = new ManualResetEventSlim();
        var legacy = new LegacyBlock(releaseConsumer);
        await using var controller = new BusinessBlockController(services,
            [new("consumer", [], _ => legacy)], Options.Create(new BusinessBlockOptions
            {
                MetricsInterval = null,
                Policy = new() { StopDeadline = TimeSpan.FromMilliseconds(40) }
            }), NullLogger<BusinessBlockController>.Instance);
        legacy.Controller = controller;
        var start = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, "test", "enabled", 1);
        var stop = new BusinessBlockReason(BusinessBlockRequestKind.User, "stop-owner", "explicit-stop", 2);
        await controller.StartAsync("consumer", start);
        await legacy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await controller.StopAsync("consumer", stop).WaitAsync(TimeSpan.FromSeconds(5));
            var facts = Assert.Single(controller.Read().Blocks);
            Assert.True(legacy.Returned);
            Assert.Equal(BusinessBlockState.Failed, facts.State);
            Assert.Equal(BusinessBlockTransitionOutcome.StopTimeout, facts.Outcome);
            Assert.True(facts.ExecutionOutstanding);
            Assert.Single(facts.Metrics.ActiveThreads);
        }
        finally { releaseConsumer.Set(); }
        await controller.GetCompletion("consumer").WaitAsync(TimeSpan.FromSeconds(5));
        await legacy.Consumer!.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = Assert.Single(controller.Read().Blocks);
        Assert.False(completed.ExecutionOutstanding);
        Assert.Empty(completed.Metrics.ActiveThreads);
        Assert.Equal(BusinessBlockState.Stopped, completed.State);
        Assert.Equal(stop, completed.LastReason);
    }

    private sealed class LegacyBlock(ManualResetEventSlim releaseConsumer, bool failBeforeStop = false) : IBusinessBlock
    {
        public BusinessBlockController Controller = null!;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Consumer;
        public bool Returned;
        public async Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Consumer = DedicatedEtwConsumerThread.Start("business-drain", "session", () =>
            {
                using var registration = Controller.RegisterConsumerThread("consumer", "session");
                Entered.TrySetResult();
                stopNotification.WaitHandle.WaitOne();
                stopped.TrySetResult();
                if (!releaseConsumer.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test consumer was not released.");
            });
            await Entered.Task;
            if (failBeforeStop) throw new IOException("run failure before consumer drain");
            await stopped.Task;
            Returned = true;
            return BusinessBlockResult.Stopped;
        }
    }

    private sealed class CooperativeBlock(ManualResetEventSlim release) : IBusinessBlock
    {
        public async Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            using var notification = stopNotification.Register(release.Set);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stopNotification); }
            catch (OperationCanceledException) when (stopNotification.IsCancellationRequested) { }
            return BusinessBlockResult.Stopped;
        }
    }
}
