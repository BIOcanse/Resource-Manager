using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Operations.Effects;

namespace Resource_Manager_APP.Tests;

public sealed class DiskUsageScanEffectExecutorTests
{
    [Fact]
    public async Task AScannerReturningAfterCancellationCannotPublishOrReportSuccess()
    {
        var request = new DiskUsageScanRequest(DiskUsageScanScopes.Folder, DiskUsageScanModes.Full, @"C:\fixture");
        var owner = new DiskUsageResultOwner("S-1-5-21-1001", 4);
        var command = HostManagerOperationRequestCodec.DiskUsageScan(request, owner);
        var entry = new HostManagerOperationPayloadEntry(command.RequestSchemaId,
            command.RequestSchemaVersion, HostManagerOperationPayloadRole.Request,
            default, default, default, default, command.CanonicalRequest, []);
        var ticket = new HostManagerOperationActionTicket(default, command.Kind, entry, default);
        var store = new DiskUsageTreeStore();
        using var cancellation = new CancellationTokenSource();
        var executor = new DiskUsageScanEffectExecutor(new ReportingScanner(cancellation.Cancel), store);
        var sink = new BlockingProgressSink(false);
        sink.Release.TrySetResult();
        var completion = await executor.ExecuteStartAsync(ticket, sink, cancellation.Token);
        Assert.Equal(HostManagerOperationEffectOutcome.Canceled, completion.Outcome);
        Assert.Null(store.CurrentSummary);
        Assert.Null(store.TryAcquire(owner));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressSettlesBeforeTreePublicationAndFailureCannotPublishSuccess(bool failProgress)
    {
        var request = new DiskUsageScanRequest(DiskUsageScanScopes.Folder, DiskUsageScanModes.Full, @"C:\fixture");
        var owner = new DiskUsageResultOwner("S-1-5-21-1001", 4);
        var command = HostManagerOperationRequestCodec.DiskUsageScan(request, owner);
        var entry = new HostManagerOperationPayloadEntry(command.RequestSchemaId,
            command.RequestSchemaVersion, HostManagerOperationPayloadRole.Request,
            default, default, default, default, command.CanonicalRequest, []);
        var ticket = new HostManagerOperationActionTicket(default, command.Kind, entry, default);
        var store = new DiskUsageTreeStore();
        var sink = new BlockingProgressSink(failProgress);
        var executor = new DiskUsageScanEffectExecutor(new ReportingScanner(), store);

        var operation = executor.ExecuteStartAsync(ticket, sink, CancellationToken.None).AsTask();
        try
        {
            await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
            Assert.Null(store.CurrentSummary);
        }
        finally
        {
            sink.Release.TrySetResult();
        }

        var completion = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(failProgress ? HostManagerOperationEffectOutcome.RetryableFailure
            : HostManagerOperationEffectOutcome.Succeeded, completion.Outcome);
        Assert.Equal(!failProgress, store.CurrentSummary is not null);
        using var read = store.TryAcquire(owner);
        Assert.Equal(!failProgress, read is not null);
        Assert.Null(store.TryAcquire(new DiskUsageResultOwner(owner.WindowsSid, owner.WindowsSessionId + 1)));
    }

    private sealed class BlockingProgressSink(bool fail) : IHostManagerOperationProgressSink
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ReportAsync(HostManagerOperationActionTicket ticket,
            HostManagerOperationProgressUpdate progress, CancellationToken cancellationToken)
        {
            Assert.Equal(HostManagerOperationKinds.DiskUsageScan, ticket.Kind);
            Assert.Equal((ulong)20, progress.BytesDone);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (fail) throw new IOException("Progress commit failed.");
        }
    }

    private sealed class ReportingScanner(Action? afterScan = null) : IDiskUsageScanner
    {
        public async Task<DiskUsageScanResult> ScanAsync(DiskUsageScanRequest request,
            IProgress<DiskUsageScanProgress>? progress, CancellationToken cancellationToken)
        {
            await Task.Run(() => progress!.Report(new DiskUsageScanProgress(1, 20, request.Target, null)),
                cancellationToken);
            afterScan?.Invoke();
            return new DiskUsageScanResult(new DiskUsageTreeBuilder().Build(),
                new DiskUsageScanSummary(request.Scope, request.Mode, request.Target, [],
                    DiskUsageScanKinds.DirectoryWalk, [], DateTimeOffset.UtcNow, 0, 0, 20, 1, 0, 0));
        }
    }
}
