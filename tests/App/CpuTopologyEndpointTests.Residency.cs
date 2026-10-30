using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing.Parsers;
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
        using var broker = new PublicationBroker();
        using var reader = new EtwCpuCoreResidencyReader(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        var observed = new DemandObservedResidency(reader);
        await using var app = await StartEndpointsAsync(topology.Reader, observed);
        using var client = CreateClient(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var capture = (EtwCpuResidencyCaptureOwner)typeof(EtwCpuCoreResidencyReader)
            .GetProperty("Capture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        Assert.Equal(CpuCoreResidencyUnavailableReasons.NotRequested,
            Assert.IsType<CpuCoreResidencyUnavailable>(reader.ReadObservation()).ReasonCode);
        capture.EnsureStarted();
        capture.BeginWindow();

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
            case CpuCoreResidencyUnavailableReasons.EventsLost: broker.Resource.Loss(broker.Resource.Tick + 1, 1); broker.Resource.Publish(broker.Resource.Tick); break;
            case CpuCoreResidencyUnavailableReasons.SessionRestarted: broker.Rebind(); break;
            case CpuCoreResidencyUnavailableReasons.SessionUnavailable: broker.ForceUnavailable = true; break;
            case CpuCoreResidencyUnavailableReasons.CaptureFailed: broker.ThrowSnapshot = true; break;
            case "complete": FeedPublicationWindow(capture, sampling, broker); break;
        }
        capture.Collect(); // Actual product commit from a controlled native bank; endpoint demand adds no second capture.
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
        broker.ForceUnavailable = false; broker.ThrowSnapshot = false;
        FeedPublicationWindow(capture, sampling, broker);
        capture.Collect();
        using var recovered = JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        Assert.Equal("00:00:05", recovered.RootElement.GetProperty("value").GetProperty("window").GetString());
        timeout.Cancel();
        push.Dispose();
        await app.StopAsync();
    }

    private static void FeedPublicationWindow(EtwCpuResidencyCaptureOwner capture,
        HostManagerSamplingSubscriptionTestFixture sampling, PublicationBroker broker)
    {
        var plan = sampling.Provider.Current.HostManager.CpuCoreResidency;
        broker.PreviousEnd = NativeCpuReaderTestFeed.ClosedWindow(capture,
            plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray(), plan.ObservationWindowMilliseconds,
            firstTid: 1000, previousEnd: broker.PreviousEnd);
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

    private sealed class PublicationBroker : CpuResidencyHeldTestBroker
    {
        internal bool ThrowSnapshot, ForceUnavailable;
        internal long PreviousEnd;
        internal PublicationBroker() : base(1) { }
        public override KernelEtwSessionSnapshot GetSnapshot() => ThrowSnapshot
            ? throw new InvalidOperationException("recorded status-query failure")
            : ForceUnavailable ? base.GetSnapshot() with { State = "Unavailable", Message = "explicit broker-status fault injection over a real native owner" } : base.GetSnapshot();
    }
}
