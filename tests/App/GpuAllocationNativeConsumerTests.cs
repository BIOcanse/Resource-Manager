using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

[Collection("Owned Windows kernel ETW")]
public sealed class GpuAllocationNativeConsumerTests(ITestOutputHelper output)
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_ALLOCATION_NATIVE_TEST", "1")]
    public async Task RealAllocationEventsPublishACompleteRescanAndReleaseTheirSession()
    {
        Assert.True(TraceEventSession.IsElevated());
        var factory = new OwnedFactory();
        var logger = new CollectorLog();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monitoring:GpuAllocationRescanSeconds"] = "2"
        }).Build();
        using var collector = new GpuAllocationCollector(logger, configuration, factory, TimeProvider.System);
        var processes = CaptureProcessIdentities();
        var lease = collector.AcquireSubscription();
        try
        {
            var deadline = Stopwatch.GetTimestamp() + 20 * Stopwatch.Frequency;
            GpuAllocationReading? reading = null;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                reading = collector.ReadCurrent(processes);
                if (collector.CompletedRescans > 0 && reading is not null) break;
                await Task.Delay(100);
            }
            foreach (var entry in factory.Events.OrderBy(item => item.Key))
                output.WriteLine($"Event {entry.Key}: {entry.Value}");
            foreach (var error in logger.Errors) output.WriteLine(error);
            Assert.True(factory.Events.Count > 0, "No actual DxgKrnl allocation events were observed.");
            Assert.True(collector.CompletedRescans > 0 && reading is not null,
                "The production collector did not publish a complete native rescan. " + string.Join(" | ", logger.Errors.Take(10)));
            Assert.Contains(reading!.Adapters.Values.SelectMany(adapter => adapter.Values), amount => amount.TotalBytes > 0);
            Assert.Equal(0, factory.Session!.EventsLost);
            lease.Dispose();
            Assert.DoesNotContain(factory.Name!, TraceEventSession.GetActiveSessionNames());
            Assert.False(factory.Session.IsProcessing);
            Assert.Null(collector.ReadCurrent(processes));
        }
        finally { lease.Dispose(); }
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_GPU_ALLOCATION_NATIVE_TEST", "1")]
    public async Task RequestedReaderRecoversAfterItsOwnedNativeEventStreamEnds()
    {
        Assert.True(TraceEventSession.IsElevated());
        var factory = new OwnedFactory();
        var logger = new CollectorLog();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Monitoring:GpuAllocationRescanSeconds"] = "2"
        }).Build();
        using var collector = new GpuAllocationCollector(logger, configuration, factory, TimeProvider.System);
        using var lease = collector.AcquireSubscription();
        var processes = CaptureProcessIdentities();
        var first = await AwaitCompleteReadingAsync(collector, processes, 0, logger);
        var previousRescans = collector.CompletedRescans;
        var old = factory.Session!;
        var stop = old.Stop(TimeSpan.FromSeconds(3));
        Assert.True(stop.IsReleased, string.Join(" | ", stop.Failures));
        Assert.False(old.IsProcessing);
        Assert.DoesNotContain(factory.Name!, TraceEventSession.GetActiveSessionNames());
        var recovered = await AwaitCompleteReadingAsync(collector, processes, first.Generation, logger, previousRescans);
        Assert.NotSame(old, factory.Session);
        Assert.False(old.IsProcessing);
        Assert.Equal(2, factory.Created);
        Assert.True(recovered.Generation > first.Generation);
        Assert.Equal(0, factory.Session!.EventsLost);
        lease.Dispose();
        Assert.False(factory.Session.IsProcessing);
        Assert.DoesNotContain(factory.Name!, TraceEventSession.GetActiveSessionNames());
    }

    private static async Task<GpuAllocationReading> AwaitCompleteReadingAsync(GpuAllocationCollector collector,
        IReadOnlyDictionary<int, long> processes, ulong previousGeneration, CollectorLog logger, long previousRescans = 0)
    {
        var deadline = Stopwatch.GetTimestamp() + 20 * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var reading = collector.ReadCurrent(processes);
            if (collector.CompletedRescans > previousRescans && reading is not null && reading.Generation > previousGeneration
                && reading.Adapters.Values.SelectMany(adapter => adapter.Values).Any(amount => amount.TotalBytes > 0))
                return reading;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException("The native allocation stream did not recover a complete positive reading. "
            + string.Join(" | ", logger.Errors.Take(10)));
    }

    private static IReadOnlyDictionary<int, long> CaptureProcessIdentities()
    {
        var identities = new Dictionary<int, long>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { identities[process.Id] = process.StartTime.ToUniversalTime().ToFileTimeUtc(); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return identities;
    }

    private sealed class OwnedFactory : IGpuAllocationSessionFactory
    {
        private static readonly Guid Provider = new("802ec45a-1e99-4b83-9920-87c98277ba9d");
        public ConcurrentDictionary<int, string> Events { get; } = new();
        public string? Name { get; private set; }
        public IGpuAllocationSession? Session { get; private set; }
        public int Created { get; private set; }
        public IGpuAllocationSession Create(string name)
        {
            Assert.True(Session is null || !Session.IsProcessing, "A second runtime cannot overlap the old stream.");
            Assert.DoesNotContain(name, TraceEventSession.GetActiveSessionNames());
            var session = new TraceEventSession(name, TraceEventSessionOptions.Create)
            {
                StopOnDispose = true, BufferSizeMB = 16, EnableProviderTimeoutMSec = 2000
            };
            try
            {
                session.Source.Dynamic.All += data =>
                {
                    if (data.ProviderGuid == Provider)
                        Events.TryAdd((int)data.ID, string.Join(",", data.PayloadNames));
                };
                Name = name;
                Created++;
                return Session = new TraceEventGpuAllocationSession(session);
            }
            catch { session.Dispose(); throw; }
        }
    }

    private sealed class CollectorLog : ILogger<GpuAllocationCollector>
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) && Errors.Count < 32)
                Errors.Enqueue(formatter(state, exception) + ": " + exception);
        }
    }
}
