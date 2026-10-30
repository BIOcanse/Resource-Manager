using System.Net;
using System.Net.Sockets;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed partial class KernelEtwNativeKeywordUpdateTests
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_NATIVE_ETW_TEST", "1")]
    public async Task LiveCpuNetworkInterruptAndDiskConsumersRetainTheirOneSession()
    {
        Assert.True(TraceEventSession.IsElevated());
        var dpcEvents = 0;
        var factory = new OwnedRuntimeFactory
        {
            ObserveParser = parser => parser.PerfInfoDPC += data =>
            {
                if (data.ElapsedTimeMSec > 0) Interlocked.Increment(ref dpcEvents);
            }
        };
        using var broker = new KernelEtwSessionBroker(factory, NullLogger<KernelEtwSessionBroker>.Instance);
        var topology = new WindowsCpuTopologyReader(new EmptyConsumerOverrides()).CaptureTopology();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture(
            HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology));
        using var cpu = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        using var cpuDemand = cpu.AcquireSubscription("owned.native.cpu", TimeSpan.FromMilliseconds(100));
        using var network = new EtwNetworkTcpIpAttributionReader(broker,
            NullLogger<EtwNetworkTcpIpAttributionReader>.Instance);
        using var interrupts = new EtwSystemInterruptSnapshotSource(broker, sampling.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);
        using var disk = new EtwPhysicalDiskIoAttributionReader(broker,
            NullLogger<EtwPhysicalDiskIoAttributionReader>.Instance);
        var directory = Path.Combine(Path.GetTempPath(), "rm-native-consumers-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "owned.bin");
        Directory.CreateDirectory(directory);
        using var stopWriter = new CancellationTokenSource();
        Thread? writer = null;
        Exception? writerFailure = null;
        await cpu.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(() => cpu.Read() is { } value && value.Processes.Any(process =>
                process.ProcessId == Environment.ProcessId && process.ExecutionTimeMilliseconds > 0),
                "CPU consumer did not publish owned execution durations", TimeSpan.FromSeconds(20));
            var initial = broker.GetSnapshot();
            Assert.Equal(1, initial.SubscriptionCount);
            await network.StartAsync(CancellationToken.None);
            network.Read();
            var interruptInterval = TimeSpan.FromMilliseconds(sampling.Provider.Current.HostManager.RequirePublished()
                .HotPublish.SamplingSubscription.Roles.Single(role => role.RoleId == 5).MinimumIntervalMilliseconds);
            using var interruptDemand = interrupts.AcquireSubscription("owned.native.interrupts", interruptInterval);
            await interrupts.StartAsync(CancellationToken.None);
            await disk.StartAsync(CancellationToken.None);
            disk.Read();
            await UntilAsync(() => broker.GetSnapshot().SubscriptionCount == 4, "four production consumers did not subscribe");
            var combined = broker.GetSnapshot();
            Assert.Equal(initial.Generation, combined.Generation);
            Assert.Equal(initial.StartedAt, combined.StartedAt);
            Assert.True(combined.EnabledKeywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch));
            Assert.True(combined.EnabledKeywords.HasFlag(KernelTraceEventParser.Keywords.NetworkTCPIP));
            Assert.True(combined.EnabledKeywords.HasFlag(KernelTraceEventParser.Keywords.Interrupt));
            Assert.True(combined.EnabledKeywords.HasFlag(KernelTraceEventParser.Keywords.DeferedProcedureCalls));
            Assert.True(combined.EnabledKeywords.HasFlag(KernelTraceEventParser.Keywords.DiskIO));
            await Task.Delay(2200); // Respect the production readers' two-second complete-window boundary.
            writer = new Thread(() =>
            {
                try
                {
                    using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                        4096, FileOptions.WriteThrough);
                    var bytes = new byte[256 * 1024];
                    for (var index = 0; index < 40 && !stopWriter.IsCancellationRequested; index++)
                    {
                        output.Position = 0;
                        output.Write(bytes);
                        output.Flush(flushToDisk: true);
                        if (stopWriter.Token.WaitHandle.WaitOne(100)) break;
                    }
                }
                catch (Exception error) { writerFailure = error; }
            }) { IsBackground = true };
            stopWriter.CancelAfter(TimeSpan.FromSeconds(10));
            writer.Start();
            await SendOwnedLoopbackAsync();
            await UntilAsync(() => network.Read().RawProcessValues.TryGetValue(Environment.ProcessId, out var row)
                && row.SendBytesPerSecond > 0 && row.ReceiveBytesPerSecond > 0,
                "network consumer did not attribute owned loopback transfer");
            Assert.False(network.Read().Processes.ContainsKey(Environment.ProcessId),
                "Owned loopback transfer must not be reported as external network traffic.");
            await UntilAsync(() => disk.Read().Processes.TryGetValue(Environment.ProcessId, out var row)
                && row.WriteBytesPerSecond > 0, "disk consumer did not attribute owned physical writes");
            await UntilAsync(() => interrupts.Read() is { Available: true, EventCount: > 0 },
                "interrupt consumer did not publish real ISR/DPC events",
                SystemInterruptWindowAggregator.BucketDuration + TimeSpan.FromSeconds(5));
            Assert.Equal(initial.Generation, interrupts.Read().SourceGeneration);
            Assert.True(Volatile.Read(ref dpcEvents) > 0, "The real parser did not receive DPC durations.");
            Assert.Equal(0, broker.GetSnapshot().EventsLost);
            var measuredThrough = cpu.Read()!.MeasuredThrough;
            stopWriter.Cancel();
            Assert.True(writer.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(writerFailure);
            await disk.StopAsync(CancellationToken.None);
            await network.StopAsync(CancellationToken.None);
            interruptDemand.Dispose();
            await interrupts.StopAsync(CancellationToken.None);
            var retained = broker.GetSnapshot();
            Assert.Equal(1, retained.SubscriptionCount);
            Assert.Equal(initial.Generation, retained.Generation);
            Assert.Equal(initial.StartedAt, retained.StartedAt);
            Assert.Equal(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
                | KernelTraceEventParser.Keywords.ContextSwitch, retained.EnabledKeywords);
            await UntilAsync(() => cpu.Read() is { } next && next.MeasuredThrough > measuredThrough,
                "retained CPU consumer did not advance its real measured window after other consumers left");
            Assert.Equal(1, factory.Created);
            cpuDemand.Dispose();
            await UntilAsync(() => !broker.GetSnapshot().IsRunning, "the last consumer did not release the session");
            await UntilAsync(() => !TraceEventSession.GetActiveSessionNames().Contains(
                ResourceManagerEtwSessionNames.KernelSessionName), "the owned native session did not finish releasing");
        }
        finally
        {
            stopWriter.Cancel();
            if (writer is not null) Assert.True(writer.Join(TimeSpan.FromSeconds(5)));
            await disk.StopAsync(CancellationToken.None);
            await network.StopAsync(CancellationToken.None);
            await interrupts.StopAsync(CancellationToken.None);
            await cpu.StopAsync(CancellationToken.None);
            if (File.Exists(file)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static async Task SendOwnedLoopbackAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync(deadline.Token);
        using var sender = new TcpClient();
        await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
        using var receiver = await accepting;
        using var input = receiver.GetStream();
        using var output = sender.GetStream();
        var payload = new byte[256 * 1024];
        var reading = input.ReadExactlyAsync(new byte[payload.Length], deadline.Token);
        await output.WriteAsync(payload, deadline.Token);
        await reading;
    }

    private sealed class EmptyConsumerOverrides : ICpuCorePerformanceOverrideStore
    {
        public CpuPerformanceOverrides LoadConfiguration(string cpuName) => new(new Dictionary<int, double>(), null);
        public IReadOnlyDictionary<int, double> LoadScores(string cpuName) => new Dictionary<int, double>();
        public void SaveBaselineRatio(double ratio) => throw new NotSupportedException();
        public void ResetBaselineRatio() => throw new NotSupportedException();
        public CpuCorePerformanceOverrideResult Save(CpuCorePerformanceOverrideRequest request) => throw new NotSupportedException();
        public CpuCorePerformanceOverrideResult Reset(string cpuName) => throw new NotSupportedException();
    }
}
