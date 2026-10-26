using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockHostedPartialStartupTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task HostStopAwaitsExternallyStartedServiceWhoseAliasStartWasNeverReached()
    {
        var earlier = new FailingStartService();
        var later = new GatedStopService();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockController();
                services.AddSingleton<IHostedService>(earlier);
                services.AddBusinessBlockHostedService<GatedStopService>("later", [], [], _ => later);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        await scheduler.SetDemandAsync("external", "later", new(BusinessBlockRequestKind.User,
            "partial-startup-test", "external-start", 0)).WaitAsync(TestTimeout);
        await WaitUntilAsync(() => scheduler.ReadState("later") == BusinessBlockState.Running);
        Assert.Equal(1, later.StartCount);
        var failure = await Assert.ThrowsAsync<IOException>(() => host.StartAsync().WaitAsync(TestTimeout));
        Assert.Same(earlier.StartFailure, failure);

        Task? stopping = null;
        try
        {
            stopping = host.StopAsync();
            await Task.WhenAny(later.StopStarted.Task, stopping).WaitAsync(TestTimeout);
            Assert.False(stopping.IsCompleted);
            Assert.True(later.StopStarted.Task.IsCompletedSuccessfully);
            Assert.Equal(1, later.StopCount);
        }
        finally
        {
            // A failing regression must also release the stranded original run.
            var block = host.Services.GetRequiredService<HostedBusinessBlockRegistry>().GetOrAdd("later", () => later);
            if (!later.StopStarted.Task.IsCompleted) block.SetHostStopTurn();
            later.ReleaseStop.TrySetResult();
            await (stopping ?? host.StopAsync()).WaitAsync(TestTimeout);
            await block.CurrentStopCompletion().WaitAsync(TestTimeout);
            await WaitUntilAsync(() => scheduler.ReadState("later") == BusinessBlockState.Stopped);
        }

        Assert.Equal(1, later.StartCount);
        Assert.Equal(1, later.StopCount);
        Assert.Equal(CancellationToken.None, later.LastStopToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FailingStartService : IHostedService
    {
        internal IOException StartFailure { get; } = new("earlier host startup failed");
        internal CancellationToken? LastStopToken { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => throw StartFailure;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            LastStopToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class GatedStopService : IHostedService
    {
        internal TaskCompletionSource StopStarted { get; } = NewSignal();
        internal TaskCompletionSource ReleaseStop { get; } = NewSignal();
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        internal CancellationToken? LastStopToken { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            LastStopToken = cancellationToken;
            StopStarted.TrySetResult();
            return ReleaseStop.Task;
        }
    }
}
