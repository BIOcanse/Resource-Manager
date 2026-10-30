using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed partial class KernelEtwNativeKeywordUpdateTests
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_NATIVE_ETW_TEST", "1")]
    public async Task DisposingDiskConsumerRetainsTheOtherLiveConsumerOnTheSameSession()
    {
        Assert.True(TraceEventSession.IsElevated());
        var factory = CreateNativeConsumerFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var reader = new EtwPhysicalDiskIoAttributionReader(broker, NullLogger<EtwPhysicalDiskIoAttributionReader>.Instance);
        await reader.StartAsync(CancellationToken.None);
        await UntilAsync(() => broker.GetSnapshot().SubscriptionCount == 1, "disk reader did not subscribe");
        reader.Read();
        var initial = broker.GetSnapshot();
        var children = new ConcurrentDictionary<int, ulong>();
        NativeKernelEtwSession? retainedSession = null;
        using var processLease = broker.Acquire(new("native-test.retained-process", KernelTraceEventParser.Keywords.Process,
            (outputs, _) => retainedSession = outputs.NativeSession) { NativeOutputs = KernelEtwNativeOutputs.File });
        Assert.Equal(2, broker.GetSnapshot().SubscriptionCount);
        reader.Dispose();
        var current = broker.GetSnapshot();
        Assert.Equal(1, current.SubscriptionCount);
        Assert.True(current.IsRunning, current.Message);
        Assert.Equal(initial.Generation, current.Generation);
        Assert.Equal(initial.StartedAt, current.StartedAt);
        Assert.Equal(KernelTraceEventParser.Keywords.Process, current.EnabledKeywords);
        var child = RunOwnedProcessProbe();
        ulong notificationSequence = 0;
        await UntilAsync(() =>
        {
            KernelEtwNativeNotifications.Read(retainedSession!, ref notificationSequence, (view, gap) =>
            {
                Assert.False(gap, "The retained native notification consumer skipped a publication.");
                foreach (var record in KernelEtwNativeNotifications.Records(view))
                    if (record.Kind == 7 && record.ParentProcessId == Environment.ProcessId)
                        children[checked((int)record.ProcessId)] = record.Key;
            });
            return children.ContainsKey(child);
        }, "retained native process publication stopped after disk consumer disposal");
        Assert.NotEqual(0ul, children[child]);
        Assert.Equal(1, factory.Created);
        processLease.Dispose();
        Assert.False(broker.GetSnapshot().IsRunning);
        Assert.DoesNotContain(ResourceManagerEtwSessionNames.KernelSessionName, TraceEventSession.GetActiveSessionNames());
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_NATIVE_ETW_TEST", "1")]
    public async Task StandaloneDiskReaderAttributesPhysicalWritesFromANewOwnedThread()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(TraceEventSession.IsElevated());
        var factory = CreateNativeConsumerFactory();
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        using var reader = new EtwPhysicalDiskIoAttributionReader(broker, NullLogger<EtwPhysicalDiskIoAttributionReader>.Instance);
        await reader.StartAsync(CancellationToken.None);
        await UntilAsync(() => broker.GetSnapshot().SubscriptionCount == 1, "disk reader did not subscribe");
        reader.Read(); // Only the production disk consumer requests this session.
        var initial = broker.GetSnapshot();
        Assert.True(initial.IsRunning, initial.Message);
        Assert.Equal(1, initial.SubscriptionCount);
        var directory = Path.Combine(Path.GetTempPath(), "rm-native-disk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "owned-write.bin");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Exception? writerFailure = null;
        var writer = new Thread(() =>
        {
            try
            {
                var bytes = new byte[256 * 1024];
                using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.WriteThrough);
                for (var index = 0; index < 40 && !stop.IsCancellationRequested; index++)
                {
                    output.Position = 0;
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                    if (stop.Token.WaitHandle.WaitOne(100)) break;
                }
            }
            catch (Exception error) { writerFailure = error; }
        }) { IsBackground = true };
        try
        {
            writer.Start();
            await UntilAsync(() => reader.Read().Processes.TryGetValue(Environment.ProcessId, out var process)
                && process.WriteBytesPerSecond > 0, "standalone physical disk consumer did not attribute the owned writes");
            var current = broker.GetSnapshot();
            Assert.Equal(initial.Generation, current.Generation);
            Assert.Equal(initial.StartedAt, current.StartedAt);
            Assert.Equal(1, factory.Created);
            Assert.Equal(0, current.EventsLost);
        }
        finally
        {
            stop.Cancel();
            Assert.True(writer.Join(TimeSpan.FromSeconds(5)), "Owned disk writer did not exit.");
            if (File.Exists(file)) File.Delete(file);
            Directory.Delete(directory);
        }
        Assert.Null(writerFailure);
        reader.Dispose();
        Assert.False(broker.GetSnapshot().IsRunning);
        Assert.DoesNotContain(ResourceManagerEtwSessionNames.KernelSessionName, TraceEventSession.GetActiveSessionNames());
    }
}
