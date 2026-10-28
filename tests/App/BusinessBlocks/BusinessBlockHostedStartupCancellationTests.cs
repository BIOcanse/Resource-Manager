using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockHostedStartupCancellationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StopDuringStartCancelsStartupAndRetainsOwnershipUntilStartupCleanupDrains()
    {
        var service = new GatedStartupCleanup();
        var (block, diagnostics) = CreateBlock(service);
        var session = block.PrepareStart(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var runningReports = 0;
        var run = block.RunAsync(new(stop.Token, () => Interlocked.Increment(ref runningReports)));
        try
        {
            await service.Entered.Task.WaitAsync(Timeout);
            stop.Cancel();
            await service.CancellationObserved.Task.WaitAsync(Timeout);
            Assert.False(run.IsCompleted);
            Assert.False(session.StartFinished.Task.IsCompleted);
            Assert.False(session.StopCompleted.Task.IsCompleted);
            Assert.False(session.Readiness.Task.IsCompleted);
            Assert.Equal(0, service.StopCount);
            Assert.Equal(0, runningReports);
        }
        finally
        {
            stop.Cancel();
            service.ReleaseCleanup.TrySetResult();
            await run.WaitAsync(Timeout);
        }
        Assert.True(service.StartCleanupCompleted);
        Assert.True(session.StartFinished.Task.IsCompletedSuccessfully);
        Assert.True(session.StopCompleted.Task.IsCompletedSuccessfully);
        Assert.True(session.Readiness.Task.IsCanceled);
        Assert.Equal(1, service.StopCount);
        Assert.Equal(CancellationToken.None, service.StopToken);
        Assert.False(service.StartStopOverlapped);
        Assert.False(block.ExecutionFailure.IsCompleted);
        Assert.Equal(BusinessBlockOutcome.Stopped, diagnostics.Read(BackendUids.ControlActualStateReader).Result!.Outcome);
    }

    [Fact]
    public async Task ExternalStartupCancellationRemainsAStartFailureWithoutRunStop()
    {
        var service = new GatedStartupCleanup();
        var (block, diagnostics) = CreateBlock(service);
        using var startup = new CancellationTokenSource();
        var session = block.PrepareStart(startup.Token);
        var run = block.RunAsync(new(CancellationToken.None, () => throw new InvalidOperationException("Startup must not report running.")));
        try
        {
            await service.Entered.Task.WaitAsync(Timeout);
            startup.Cancel();
            await service.CancellationObserved.Task.WaitAsync(Timeout);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            startup.Cancel();
            service.ReleaseCleanup.TrySetResult();
            await run.WaitAsync(Timeout);
        }
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Readiness.Task);
        Assert.Same(failure, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => block.ExecutionFailure));
        Assert.True(session.Readiness.Task.IsFaulted);
        Assert.True(block.ExecutionFailure.IsFaulted);
        var result = diagnostics.Read(BackendUids.ControlActualStateReader).Result!;
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Start, result.FailureStage);
        Assert.Equal(1, service.StopCount);
        Assert.True(service.StartCleanupCompleted);
        Assert.False(service.StartStopOverlapped);
    }

    private static (HostedBusinessBlock Block, BusinessBlockDiagnosticsRegistry Diagnostics) CreateBlock(IHostedService service)
    {
        var registration = new BusinessBlockRegistration(BackendUids.ControlActualStateReader, _ => throw new NotSupportedException());
        var diagnostics = new BusinessBlockDiagnosticsRegistry([registration], NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        var registry = new HostedBusinessBlockRegistry(diagnostics, [registration]);
        return (registry.GetOrAdd(registration.Id, () => service), diagnostics);
    }

    private sealed class GatedStartupCleanup : IHostedService
    {
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal TaskCompletionSource CancellationObserved { get; } = NewSignal();
        internal TaskCompletionSource ReleaseCleanup { get; } = NewSignal();
        internal bool StartCleanupCompleted;
        internal bool StartStopOverlapped;
        internal int StopCount;
        internal CancellationToken StopToken;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await ReleaseCleanup.Task;
                StartCleanupCompleted = true;
                throw;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            StopToken = cancellationToken;
            StartStopOverlapped = !StartCleanupCompleted;
            return Task.CompletedTask;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
