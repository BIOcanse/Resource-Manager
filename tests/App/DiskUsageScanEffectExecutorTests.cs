using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Operations.Effects;

namespace Resource_Manager_APP.Tests;

public sealed class DiskUsageScanEffectExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressSettlesBeforeTreePublicationAndFailureCannotPublishSuccess(bool failProgress)
    {
        var request = new DiskUsageScanRequest(DiskUsageScanScopes.Folder, DiskUsageScanModes.Full, @"C:\fixture");
        var command = HostManagerOperationRequestCodec.DiskUsageScan(request);
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
            Assert.Null(store.Current);
        }
        finally
        {
            sink.Release.TrySetResult();
        }

        var completion = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(failProgress ? HostManagerOperationEffectOutcome.RetryableFailure
            : HostManagerOperationEffectOutcome.Succeeded, completion.Outcome);
        Assert.Equal(!failProgress, store.Current is not null);
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

    private sealed class ReportingScanner : IDiskUsageScanner
    {
        public async Task<DiskUsageScanResult> ScanAsync(DiskUsageScanRequest request,
            IProgress<DiskUsageScanProgress>? progress, CancellationToken cancellationToken)
        {
            await Task.Run(() => progress!.Report(new DiskUsageScanProgress(1, 20, request.Target, null)),
                cancellationToken);
            return new DiskUsageScanResult(new DiskUsageTreeBuilder().Build(),
                new DiskUsageScanSummary(request.Scope, request.Mode, request.Target, [],
                    DiskUsageScanKinds.DirectoryWalk, [], DateTimeOffset.UtcNow, 0, 0, 20, 1, 0, 0));
        }
    }
}
