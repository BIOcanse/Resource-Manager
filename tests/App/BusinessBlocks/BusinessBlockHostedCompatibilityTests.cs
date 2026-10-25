using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockHostedCompatibilityTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task HostStopNotifiesEveryServiceInReverseOrderWithTheSameExpiredToken()
    {
        using var stopToken = new CancellationTokenSource();
        var observations = new ConcurrentQueue<StopObservation>();
        using var host = BuildHost(services =>
        {
            services.AddBusinessBlockHostedService<FirstStopService>(_ => new FirstStopService(observations));
            services.AddBusinessBlockHostedService<SecondStopService>(_ => new SecondStopService(observations));
            services.AddBusinessBlockHostedService<ThirdStopService>(_ =>
                new ThirdStopService(observations, _ => stopToken.Cancel()));
        });

        await host.StartAsync();
        _ = await Record.ExceptionAsync(() => host.StopAsync(stopToken.Token));

        var stopped = observations.ToArray();
        Assert.Equal(new[] { "third", "second", "first" }, stopped.Select(value => value.Name));
        var sharedHostToken = stopped[0].Token;
        Assert.All(stopped, value =>
        {
            Assert.Equal(sharedHostToken, value.Token);
            Assert.True(value.Token.IsCancellationRequested);
        });
    }

    [Fact]
    public async Task DefaultHostedStopAwaitsTheOriginalStopTaskAfterCallerCancellation()
    {
        using var hostDeadline = new CancellationTokenSource();
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new LifecycleService(_ => releaseStop.Task);
        using var host = BuildHost(services =>
            services.AddBusinessBlockHostedService<LifecycleService>(_ => service));

        await host.StartAsync();
        var facts = Assert.Single(host.Services.GetRequiredService<IBusinessBlockQuery>().Read().Blocks);
        Assert.Null(facts.Policy.StopDeadline);

        var hosted = Assert.Single(host.Services.GetServices<IHostedService>().Where(static service => service is not BusinessBlockDependencyLifetime));
        var stopping = hosted.StopAsync(hostDeadline.Token);
        try
        {
            await service.StopStarted.Task.WaitAsync(TestTimeout);
            hostDeadline.Cancel();
            await Task.Delay(30);

            Assert.False(stopping.IsCompleted);
            Assert.Equal(hostDeadline.Token, service.LastStopToken);

            releaseStop.TrySetResult();
            await stopping.WaitAsync(TestTimeout);
        }
        finally
        {
            hostDeadline.Cancel();
            releaseStop.TrySetResult();
            try { await stopping.WaitAsync(TestTimeout); }
            catch (OperationCanceledException) { }
        }

        Assert.Equal(1, service.StopCount);
    }

    [Fact]
    public async Task ServiceTimeoutExceptionIsPropagatedAsTheOriginalFailure()
    {
        var failure = new TimeoutException("service-owned timeout");
        var service = new LifecycleService(_ => Task.FromException(failure));
        using var host = BuildHost(services =>
            services.AddBusinessBlockHostedService<LifecycleService>(_ => service));

        await host.StartAsync();
        var hosted = Assert.Single(host.Services.GetServices<IHostedService>().Where(static service => service is not BusinessBlockDependencyLifetime));
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => hosted.StopAsync(CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Equal(1, service.StopCount);
        Assert.Equal(BusinessBlockTransitionOutcome.Failed,
            Assert.Single(host.Services.GetRequiredService<IBusinessBlockQuery>().Read().Blocks).Outcome);
    }

    [Fact]
    public async Task ContinueWaitingPropagatesATimeoutExceptionThrownAfterTheDeadline()
    {
        var failure = new TimeoutException("late service-owned timeout");
        var service = new LifecycleService(async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            throw failure;
        });
        using var host = BuildHost(
            services => services.AddBusinessBlockHostedService<LifecycleService>(_ => service),
            options => options.Blocks[nameof(LifecycleService)] = new BusinessBlockPolicy
            {
                StopDeadline = TimeSpan.FromMilliseconds(35),
                OnTimeout = BusinessBlockTimeoutPolicy.ContinueWaiting
            });

        await host.StartAsync();
        var hosted = Assert.Single(host.Services.GetServices<IHostedService>().Where(static service => service is not BusinessBlockDependencyLifetime));
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => hosted.StopAsync(CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Equal(1, service.StopCount);
        Assert.Equal(BusinessBlockTransitionOutcome.Failed,
            Assert.Single(host.Services.GetRequiredService<IBusinessBlockQuery>().Read().Blocks).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAfterCanceledStopIsBoundedAndDoesNotIssueAnotherStop(bool useContinueWaitingDeadline)
    {
        var serviceStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backgroundRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LifecycleService? service = null;
        GatedBackgroundService? backgroundService = null;
        using var stopCancellation = new CancellationTokenSource();
        var host = BuildHost(
            services =>
            {
                if (useContinueWaitingDeadline)
                {
                    var created = new LifecycleService(_ => serviceStop.Task);
                    service = created;
                    services.AddBusinessBlockHostedService<LifecycleService>(_ => created);
                }
                else
                {
                    var created = new GatedBackgroundService(backgroundRelease.Task);
                    backgroundService = created;
                    services.AddBusinessBlockHostedService<GatedBackgroundService>(_ => created);
                }
            },
            options => options.Blocks[useContinueWaitingDeadline
                ? nameof(LifecycleService)
                : nameof(GatedBackgroundService)] = new BusinessBlockPolicy
                {
                    StopDeadline = useContinueWaitingDeadline ? TimeSpan.FromMilliseconds(35) : null,
                    OnTimeout = BusinessBlockTimeoutPolicy.ContinueWaiting
                });
        Task? stopTask = null;
        Task? disposeTask = null;
        BusinessBlockController? controller = null;

        try
        {
            await host.StartAsync();
            if (!useContinueWaitingDeadline) await backgroundService!.ExecutionStarted.Task.WaitAsync(TestTimeout);
            var activeController = host.Services.GetRequiredService<BusinessBlockController>();
            controller = activeController;
            var hosted = Assert.Single(host.Services.GetServices<IHostedService>().Where(static service => service is not BusinessBlockDependencyLifetime));
            var stopping = hosted.StopAsync(stopCancellation.Token);
            stopTask = stopping;
            var stopStarted = useContinueWaitingDeadline
                ? service!.StopStarted.Task
                : backgroundService!.StopStarted.Task;
            await stopStarted.WaitAsync(TestTimeout);

            if (useContinueWaitingDeadline)
            {
                await WaitUntilAsync(() => Assert.Single(activeController.Read().Blocks).Outcome == BusinessBlockTransitionOutcome.StopTimeout);
                stopCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
            }
            else
            {
                stopCancellation.Cancel();
                await stopping.WaitAsync(TimeSpan.FromSeconds(1));
                var facts = Assert.Single(activeController.Read().Blocks);
                Assert.True(facts.ExecutionOutstanding);
                Assert.Equal(stopCancellation.Token, backgroundService!.LastStopToken);
            }

            var elapsed = Stopwatch.StartNew();
            disposeTask = activeController.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(1));
            elapsed.Stop();

            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Controller disposal took {elapsed.Elapsed}.");
            Assert.Equal(1, useContinueWaitingDeadline ? service!.StopCount : backgroundService!.StopCount);
        }
        finally
        {
            stopCancellation.Cancel();
            serviceStop.TrySetResult();
            backgroundRelease.TrySetResult();
            if (stopTask is not null)
            {
                try { await stopTask.WaitAsync(TestTimeout); }
                catch (OperationCanceledException) { }
            }
            if (disposeTask is not null) await disposeTask.WaitAsync(TestTimeout);
            if (controller is not null)
            {
                try
                {
                    await controller.GetCompletion(useContinueWaitingDeadline
                        ? nameof(LifecycleService)
                        : nameof(GatedBackgroundService)).WaitAsync(TestTimeout);
                }
                catch (InvalidOperationException) { }
            }
            host.Dispose();
        }
    }

    [Fact]
    public void DirectServicesAreConstructedInEnumerationOrderWhileGenericAliasesStayLazy()
    {
        var events = new ConcurrentQueue<string>();
        using var host = BuildHost(services =>
        {
            services.AddSingleton<LazyAliasService>(_ =>
            {
                events.Enqueue("alias:construct");
                return new LazyAliasService(events);
            });
            services.AddHostedServiceAlias<LazyAliasService>();
            services.AddBusinessBlockHostedService<FirstConstructedService>(_ =>
            {
                events.Enqueue("first:construct");
                return new FirstConstructedService(events);
            });
            services.AddBusinessBlockHostedService<SecondConstructedService>(_ =>
            {
                events.Enqueue("second:construct");
                return new SecondConstructedService(events);
            });
        });

        var hosted = host.Services.GetServices<IHostedService>().Where(static service => service is not BusinessBlockDependencyLifetime).ToArray();

        Assert.Collection(events,
            value => Assert.Equal("first:construct", value),
            value => Assert.Equal("second:construct", value));
        Assert.Equal(3, hosted.Length);
        Assert.DoesNotContain(events, value => value.EndsWith(":start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectConstructorFailureAbortsHostEnumerationBeforeAnyServiceStarts()
    {
        var events = new ConcurrentQueue<string>();
        var failure = new InvalidOperationException("direct service constructor failed");
        using var host = BuildHost(services =>
        {
            services.AddSingleton<LazyAliasService>(_ =>
            {
                events.Enqueue("alias:construct");
                return new LazyAliasService(events);
            });
            services.AddHostedServiceAlias<LazyAliasService>();
            services.AddBusinessBlockHostedService<ThrowingConstructedService>(_ =>
            {
                events.Enqueue("direct:construct");
                throw failure;
            });
        });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Same(failure, thrown);
        Assert.Equal(new[] { "direct:construct" }, events);
        Assert.DoesNotContain(events, value => value.EndsWith(":start", StringComparison.Ordinal));
    }

    private static IHost BuildHost(Action<IServiceCollection> configure,
        Action<BusinessBlockOptions>? configureOptions = null)
        => Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                configure(services);
                services.Configure<BusinessBlockOptions>(options =>
                {
                    options.MetricsInterval = null;
                    options.Policy = new BusinessBlockPolicy { StopDeadline = null };
                    configureOptions?.Invoke(options);
                });
            })
            .Build();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected business-block state was not reached.");
            await Task.Delay(5);
        }
    }

    private abstract class OrderedStopService(string name, ConcurrentQueue<StopObservation> observations,
        Action<CancellationToken>? afterStop = null) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            observations.Enqueue(new StopObservation(name, cancellationToken));
            afterStop?.Invoke(cancellationToken);
            return Task.CompletedTask;
        }
    }

    private sealed class FirstStopService(ConcurrentQueue<StopObservation> observations)
        : OrderedStopService("first", observations);

    private sealed class SecondStopService(ConcurrentQueue<StopObservation> observations)
        : OrderedStopService("second", observations);

    private sealed class ThirdStopService(ConcurrentQueue<StopObservation> observations,
        Action<CancellationToken> afterStop)
        : OrderedStopService("third", observations, afterStop);

    private sealed record StopObservation(string Name, CancellationToken Token);

    private sealed class LifecycleService(Func<CancellationToken, Task>? stop = null) : IHostedService
    {
        private readonly Func<CancellationToken, Task> stopOperation = stop ?? (_ => Task.CompletedTask);
        private int startCount;
        private int stopCount;

        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCount => Volatile.Read(ref startCount);
        public int StopCount => Volatile.Read(ref stopCount);
        public CancellationToken? LastStopToken { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref startCount);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref stopCount);
            LastStopToken = cancellationToken;
            StopStarted.TrySetResult();
            return stopOperation(cancellationToken);
        }
    }

    private sealed class GatedBackgroundService(Task executionRelease) : BackgroundService
    {
        private int stopCount;

        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExecutionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCount => Volatile.Read(ref stopCount);
        public CancellationToken? LastStopToken { get; private set; }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            ExecutionStarted.TrySetResult();
            // Deliberately remains gated after stoppingToken is canceled so the controller
            // can report the outstanding execution while BackgroundService.StopAsync returns.
            return executionRelease;
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref stopCount);
            LastStopToken = cancellationToken;
            StopStarted.TrySetResult();
            return base.StopAsync(cancellationToken);
        }
    }

    private abstract class ConstructionService(ConcurrentQueue<string> events, string name) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Enqueue(name + ":start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LazyAliasService(ConcurrentQueue<string> events) : ConstructionService(events, "alias");
    private sealed class FirstConstructedService(ConcurrentQueue<string> events) : ConstructionService(events, "first");
    private sealed class SecondConstructedService(ConcurrentQueue<string> events) : ConstructionService(events, "second");
    private sealed class ThrowingConstructedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
