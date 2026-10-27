using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed partial class CpuTopologyEndpointTests
{
    [Theory]
    [InlineData(CpuCoreResidencyUnavailableReasons.WindowWarming)]
    [InlineData(CpuCoreResidencyUnavailableReasons.EventsLost)]
    [InlineData(CpuCoreResidencyUnavailableReasons.SessionRestarted)]
    [InlineData(CpuCoreResidencyUnavailableReasons.SessionUnavailable)]
    [InlineData(CpuCoreResidencyUnavailableReasons.CaptureFailed)]
    [InlineData("complete")]
    public async Task ResidencyHttpAndPushPublishTheSameOwnerValue(string reason)
    {
        await using var topology = new CpuTopologyProviderTestFixture();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new PublicationBroker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
        var observed = new DemandObservedResidency(reader);
        await using var app = await StartEndpointsAsync(topology.Reader, observed);
        using var client = CreateClient(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var capture = (EtwCpuResidencyCaptureOwner)typeof(EtwCpuCoreResidencyReader)
            .GetProperty("Capture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        Assert.Equal(CpuCoreResidencyUnavailableReasons.NotRequested,
            Assert.IsType<CpuCoreResidencyUnavailable>(reader.ReadObservation()).ReasonCode);
        capture.EnsureStarted();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "residency", path = "/api/cpu/residency/subscribe?intervalMs=1000" } } })
        };
        var pushTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await observed.Demands.Reader.ReadAsync(timeout.Token);
        var getTask = client.GetAsync("/api/cpu/residency", timeout.Token);
        await observed.Demands.Reader.ReadAsync(timeout.Token);
        var coresTask = client.GetAsync("/api/cpu/residency/cores", timeout.Token);
        await observed.Demands.Reader.ReadAsync(timeout.Token);
        Assert.False(getTask.IsCompleted);

        switch (reason)
        {
            case CpuCoreResidencyUnavailableReasons.EventsLost: broker.EventsLost++; break;
            case CpuCoreResidencyUnavailableReasons.SessionRestarted: broker.Rebind(); break;
            case CpuCoreResidencyUnavailableReasons.SessionUnavailable: broker.Running = false; break;
            case CpuCoreResidencyUnavailableReasons.CaptureFailed: broker.ThrowSnapshot = true; break;
            case "complete": FeedPublicationWindow(capture, sampling); break;
        }
        capture.Collect(); // Production publication, deterministically driven; no ETW, timer or worker race.
        using var get = await getTask;
        using var cores = await coresTask;
        using var push = await pushTask;
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("application/json", get.Content.Headers.ContentType!.MediaType);
        var getValue = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
        using var stream = new StreamReader(await push.Content.ReadAsStreamAsync(timeout.Token));
        using var frame = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        var pushValue = frame.RootElement.GetProperty("value");
        Assert.Equal(JsonSerializer.Serialize(getValue), JsonSerializer.Serialize(pushValue));
        if (reason == "complete")
        {
            Assert.Equal("00:00:05", pushValue.GetProperty("window").GetString());
            Assert.NotEmpty(pushValue.GetProperty("processes").EnumerateArray());
        }
        else
        {
            Assert.Equal("unavailable", pushValue.GetProperty("state").GetString());
            Assert.Equal(reason, pushValue.GetProperty("reasonCode").GetString());
            Assert.Equal(JsonSerializer.Serialize(getValue), await cores.Content.ReadAsStringAsync());
            capture.Collect();
            using var stillUnavailable = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
            Assert.Equal(reason, stillUnavailable.RootElement.GetProperty("value").GetProperty("reasonCode").GetString());
        }

        // The same channel recovers with a real complete window after loss/restart/failure.
        broker.Running = true; broker.ThrowSnapshot = false;
        FeedPublicationWindow(capture, sampling);
        capture.Collect();
        using var recovered = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        Assert.Equal("00:00:05", recovered.RootElement.GetProperty("value").GetProperty("window").GetString());
        timeout.Cancel();
        push.Dispose();
        await app.StopAsync();
    }

    private static void FeedPublicationWindow(EtwCpuResidencyCaptureOwner capture, HostManagerSamplingSubscriptionTestFixture sampling)
    {
        var session = typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
        var buffer = (CpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
        var plan = sampling.Provider.Current.HostManager.CpuCoreResidency;
        var start = Stopwatch.GetTimestamp();
        var at = DateTimeOffset.UtcNow;
        // Keep QPC monotonic even when two recorded windows are fed without a wall-clock delay.
        var previous = buffer.Snapshot(plan.PhysicalCoreByLogicalProcessor.Keys.ToArray());
        start = Math.Max(start, previous.MeasuredThroughQpc + Stopwatch.Frequency);
        buffer.ObserveProcessStart(start, at, 100, 100, 1000, "recorded");
        foreach (var processor in plan.PhysicalCoreByLogicalProcessor.Keys)
        {
            buffer.ObserveThreadStart(start, at, 100, 1000 + processor);
            buffer.ObserveContextSwitch(start, at, processor, 0, 0, 100, "recorded", 1000 + processor);
        }
        var end = start + Stopwatch.Frequency * plan.ObservationWindowMilliseconds / 1000;
        foreach (var processor in plan.PhysicalCoreByLogicalProcessor.Keys)
            buffer.ObserveContextSwitch(end, at + plan.ObservationWindow, processor, 100, 1000 + processor, 0, null, 0);
    }

    // Signals only demand registration; all values and publication signals come from the production reader.
    private sealed class DemandObservedResidency(EtwCpuCoreResidencyReader reader) : ICpuCoreResidencyReader
    {
        internal Channel<string> Demands { get; } = Channel.CreateUnbounded<string>();
        public CpuCoreResidencySnapshot? Read() => reader.Read();
        public CpuCoreResidencyObservation ReadObservation() => reader.ReadObservation();
        public IDisposable AcquireSubscription(string id, TimeSpan interval) => reader.AcquireSubscription(id, interval);
        public async IAsyncEnumerable<CpuCoreResidencyObservation> SubscribeAsync(string id, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken token)
        {
            await using var values = reader.SubscribeAsync(id, interval, token).GetAsyncEnumerator(token);
            var pending = values.MoveNextAsync();
            Demands.Writer.TryWrite(id);
            while (await pending)
            {
                yield return values.Current;
                pending = values.MoveNextAsync();
            }
        }
    }

    private sealed class PublicationBroker : IKernelEtwSessionBroker
    {
        private KernelEtwSubscriptionRequest? request;
        private long generation = 1;
        internal int EventsLost;
        internal bool Running = true, ThrowSnapshot;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest value)
        {
            request = value;
            Bind();
            return new Lease(this);
        }
        private void Bind() => request!.Bind(new KernelEtwEventSource(), new(generation, DateTimeOffset.UtcNow));
        internal void Rebind() { generation++; Bind(); }
        public KernelEtwSessionSnapshot GetSnapshot()
            => ThrowSnapshot ? throw new InvalidOperationException("recorded failure")
                : new(Running ? "Running" : "Unavailable", "recorded", generation, DateTimeOffset.UnixEpoch,
                    request?.Keywords ?? 0, 1, EventsLost);
        private sealed class Lease(PublicationBroker broker) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => broker.GetSnapshot();
            public void Dispose() { }
        }
    }
}
