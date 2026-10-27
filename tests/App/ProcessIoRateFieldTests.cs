using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.Subscription;
using Basic = Resource_Manager_APP.Tests.ProcessBasicFieldCollectorTests;
using Network = Resource_Manager_APP.Tests.ProcessNetworkFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessIoRateFieldTests
{
    private const string Rate = "process_all_io_read_bytes_rate_actual";
    private const string Raw = "process_all_io_read_bytes_actual";
    private static readonly string[] Rates = ProcessMetricFieldRegistry.Definitions
        .Where(static field => field.SourceId == "windows.process.io-interval").Select(static field => field.Name).ToArray();
    private static readonly string[] IoFields = ProcessMetricFieldRegistry.Definitions
        .Where(static field => field.ReadMask == ProcessMetricReadMask.Io).Select(static field => field.Name).ToArray();

    [Fact]
    public async Task SixRatesAndSixRawDirectionsUseOneReadAndIntegerDeltaAboveDoublePrecision()
    {
        const ulong large = 9_007_199_254_740_993;
        var api = new Basic.Api { IoValue = new(large, large, large, large, large, large) };
        var clock = new Clock(); using var fixture = Basic.Fixture(api, clock: clock);
        var first = await fixture.Sampler.CaptureSnapshotAsync(Basic.Request(IoFields), default);
        Assert.Equal(13, first.ProcessFields!.Fields.Count); Assert.Equal(6, Rates.Length);
        Assert.All(first.ProcessFields.Processes.Single().Values.Where(value => Rates.Contains(value.Field)), static value => Assert.Null(value.NumericValue));
        Assert.Null(first.ProcessFields.Processes.Single().IoInterval);
        Assert.Equal(MetricInteger.FromUnsigned128(large), Basic.Value(first, "process_all_io_read_operations_actual").IntegerValue);
        Assert.Equal(MetricInteger.FromUnsigned128(((UInt128)large * 3)), Basic.Value(first, "process_all_io_operations_actual").IntegerValue);
        clock.Ticks = 3000; api.IoValue = new(large + 2, large + 4, large + 6, large + 8, large + 10, large);
        var second = await fixture.Sampler.CaptureSnapshotAsync(Basic.Request(IoFields), default);
        double[] expected = [1, 2, 3, 4, 5, 0];
        Assert.Equal<double>(expected, Rates.Select(name => Basic.Value(second, name).NumericValue!.Value));
        var interval = Assert.IsType<ProcessIoInterval>(second.ProcessFields!.Processes.Single().IoInterval);
        Assert.Equal(1000, interval.BeginCounter); Assert.Equal(3000, interval.EndCounter); Assert.Equal(1000, interval.CounterFrequency);
        Assert.Equal(2000UL, interval.WindowMilliseconds); Assert.Null(second.ProcessFields.WindowMilliseconds);
        Assert.All(second.ProcessFields.Sources, static source => Assert.Null(source.WindowMilliseconds));
        Assert.Equal(2, api.IoCalls); Assert.Equal(2, api.Opens); Assert.Equal(2, api.Closes);
        Assert.Equal(0, api.MemoryCalls + api.HandleCalls + api.ImageCalls + api.ParentCalls + api.SidCalls);
        var rawOnly = second.ProcessFields.Select(new HashSet<string> { Raw }); Assert.Null(rawOnly.Processes.Single().IoInterval);
        Assert.Null(second.ProcessFields.Clear().Processes.Single().IoInterval);
        var json = JsonSerializer.Serialize(second.ProcessFields, Basic.WebJson);
        var restored = JsonSerializer.Deserialize<ProcessMetricFieldSnapshot>(json, Basic.WebJson)!;
        Assert.Equal(interval, restored.Processes.Single().IoInterval);
        var mixed = ProcessMetricFieldSnapshot.Merge([new(DateTimeOffset.UtcNow, null, [], [new(Environment.ProcessId, Basic.Birth.ToString(CultureInfo.InvariantCulture), [])]), second.ProcessFields]);
        Assert.Equal(interval, mixed.Processes.Single().IoInterval);
    }

    [Fact]
    public void RatesUseTwoQueryReturnsAndEachIdentityOwnsItsActualWindow()
    {
        var tracker = new ProcessIoRateTracker();
        var first = tracker.Prepare([Sample(11, 100, 10, 100, 200), Sample(12, 200, 10, 300, 500)]); tracker.Commit(first);
        var next = tracker.Prepare([Sample(11, 100, 21, 1000, 1300), Sample(12, 200, 20, 1000, 2500)]);
        Assert.Equal(10, next.Find(11, 100)!.ReadBytes); //11 over1100 ticks, not the300-tick query duration
        Assert.Equal(5, next.Find(12, 200)!.ReadBytes); //10 over this identity's2000 ticks
        Assert.Equal(200, next.Find(11, 100)!.Interval.BeginCounter);
        Assert.Equal(500, next.Find(12, 200)!.Interval.BeginCounter);
        Assert.Null(next.Find(11, 101));
        const long largeTick = 9_007_199_254_740_993;
        var precise = new ProcessIoRateTracker(); precise.Commit(precise.Prepare([Sample(11, 100, 10, largeTick, largeTick)]));
        Assert.Equal(1000, precise.Prepare([Sample(11, 100, 11, largeTick + 1, largeTick + 1)]).Find(11, 100)!.ReadBytes);
    }

    [Theory]
    [InlineData("io")] [InlineData("throw-io")] [InlineData("times-before")]
    [InlineData("times-after")] [InlineData("identity-before")] [InlineData("identity-after")]
    [InlineData("close")] [InlineData("throw-close")]
    public async Task AcceptedFailureBreaksHistoryAndRecoveryRequiresTwoSuccessfulActualQueries(string fault)
    {
        var api = new Basic.Api { IoValue = Counts(100) }; var clock = new Clock();
        using var fixture = Basic.Fixture(api, clock: clock); var request = Basic.Request(Rate, Raw);
        await fixture.Sampler.CaptureSnapshotAsync(request, default);
        clock.Ticks = 2000; api.IoValue = Counts(200);
        Assert.Equal(100, Basic.Value(await fixture.Sampler.CaptureSnapshotAsync(request, default), Rate).NumericValue);
        api.Fault = fault; clock.Ticks = 3000;
        var failed = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        Assert.Null(Basic.Value(failed, Rate).NumericValue); Assert.Null(Basic.Value(failed, Raw).IntegerValue);
        Assert.Null(failed.ProcessFields!.Processes.Single().IoInterval);
        api.Fault = ""; api.IoValue = Counts(500); clock.Ticks = 4000;
        Assert.Null(Basic.Value(await fixture.Sampler.CaptureSnapshotAsync(request, default), Rate).NumericValue);
        api.IoValue = Counts(500); clock.Ticks = 5000;
        Assert.Equal(0, Basic.Value(await fixture.Sampler.CaptureSnapshotAsync(request, default), Rate).NumericValue);
    }

    [Theory]
    [InlineData("counter")] [InlineData("single-counter")] [InlineData("frequency")] [InlineData("same-tick")]
    [InlineData("backward-tick")] [InlineData("invalid-clock")]
    public async Task RollbackFrequencyAndInvalidTimeNeverInventAValueButKeepSuccessfulRaw(string change)
    {
        var api = new Basic.Api { IoValue = Counts(100) }; var clock = new Clock();
        using var fixture = Basic.Fixture(api, clock: clock); var request = Basic.Request(Rate, Raw);
        await fixture.Sampler.CaptureSnapshotAsync(request, default);
        clock.Ticks = change == "same-tick" ? 1000 : change == "backward-tick" ? 500 : 2000;
        clock.Frequency = change == "frequency" ? 2000 : change == "invalid-clock" ? 0 : 1000;
        api.IoValue = change == "single-counter" ? new(200, 50, 200, 200, 200, 200) : Counts(change == "counter" ? 50UL : 200UL);
        var unknown = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        Assert.Null(Basic.Value(unknown, Rate).NumericValue); Assert.NotNull(Basic.Value(unknown, Raw).IntegerValue);
        Assert.Null(unknown.ProcessFields!.Processes.Single().IoInterval);
        clock.Ticks += 1000; clock.Frequency = change == "frequency" ? 2000 : 1000;
        api.IoValue = Counts(change == "counter" ? 60UL : 210UL);
        var recovery = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        if (change == "invalid-clock")
        {
            Assert.Null(Basic.Value(recovery, Rate).NumericValue);
            clock.Ticks += 1000; api.IoValue = Counts(220);
            Assert.Equal(10, Basic.Value(await fixture.Sampler.CaptureSnapshotAsync(request, default), Rate).NumericValue);
        }
        else Assert.Equal(change == "frequency" ? 20d : 10d, Basic.Value(recovery, Rate).NumericValue);
    }

    [Fact]
    public void IdentityReuseDisappearanceAndMalformedQueryClockCannotReuseHistory()
    {
        var tracker = new ProcessIoRateTracker(); tracker.Commit(tracker.Prepare([Sample(11, 100, 10, 1000, 1000)]));
        var reused = tracker.Prepare([Sample(11, 200, 20, 2000, 2000)]); Assert.Null(reused.Find(11, 200)); tracker.Commit(reused);
        var oldIdentity = tracker.Prepare([Sample(11, 100, 30, 3000, 3000)]); Assert.Null(oldIdentity.Find(11, 100)); tracker.Commit(oldIdentity);
        tracker.Commit(tracker.Prepare([])); Assert.Null(tracker.Prepare([Sample(11, 100, 40, 4000, 4000)]).Find(11, 100));
        var invalid = Sample(11, 100, 50, 6000, 5000); tracker.Commit(tracker.Prepare([invalid]));
        Assert.Null(tracker.Prepare([Sample(11, 100, 60, 7000, 7000)]).Find(11, 100));
    }

    [Fact]
    public async Task OriginalOwnerRejectedAndCanceledQueriesCannotAdvanceBaselineAndFailureClearsIt()
    {
        var api = new Basic.Api { IoValue = Counts(100) }; var clock = new Clock();
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState())
        { Inventory = new Inventory(() => [Process.GetCurrentProcess()]), BasicReader = new(api, clock) };
        await owner.CollectAsync([Rate]);
        await owner.CollectAsync(["process_all_image_path_actual"]); Assert.Equal(1, api.IoCalls);
        api.IoValue = Counts(200); clock.Ticks = 2000;
        var accepted = await owner.CollectAsync([Rate]); Assert.Equal(100, Numeric(accepted));
        api.IoValue = Counts(500); clock.Ticks = 3000; await owner.CollectAsync([Rate], permit: new RejectedPermit());
        Assert.Equal(100, Numeric(owner.ReadCachedSnapshot()!));
        api.IoValue = Counts(1100); clock.Ticks = 4000;
        Assert.Equal(450, Numeric(await owner.CollectAsync([Rate]))); // wrong rejected baseline would yield600
        using var cancellation = new CancellationTokenSource(); api.AfterIo = cancellation.Cancel;
        api.IoValue = Counts(2100); clock.Ticks = 5000;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CollectAsync([Rate], cancellation.Token));
        api.AfterIo = null; api.IoValue = Counts(4100); clock.Ticks = 6000;
        Assert.Equal(1500, Numeric(await owner.CollectAsync([Rate]))); // wrong canceled baseline would yield2000
        owner.Inventory = new Inventory(() => throw new InvalidOperationException("Recorded inventory failure."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CollectAsync([Rate]));
        owner.Inventory = new Inventory(() => [Process.GetCurrentProcess()]); api.IoValue = Counts(5100); clock.Ticks = 7000;
        Assert.Null(Numeric(await owner.CollectAsync([Rate])));
        owner.Inventory = new Inventory(() => []); await owner.CollectAsync([Rate]);
        owner.Inventory = new Inventory(() => [Process.GetCurrentProcess()]); clock.Ticks = 8000;
        Assert.Null(Numeric(await owner.CollectAsync([Rate])));
    }

    [Fact]
    public async Task PublicHostDirectAndPeriodicSubscriptionKeepLaneWindowsAndPassiveCurrent()
    {
        var api = new Basic.Api { IoValue = Counts(100) }; var clock = new Clock { Step = 500 };
        using var fixture = Basic.Fixture(api, clock: clock); var backend = new HostProcessFieldBackend(fixture.Sampler);
        await backend.DiscoverAsync(default); var field = backend.Catalog.Fields.Single(item => item.Definition.Name == Rate).Field;
        Assert.Null(backend.ReadCached(field).Value);
        var direct = (await backend.CollectAsync([field], default)).Single(); Assert.Equal(0, direct.Value?.Number);
        Assert.Equal(1000UL, direct.SampleDurationMilliseconds); Assert.Equal(direct, backend.ReadCached(field));
        var reads = api.IoCalls; _ = backend.ReadCached(field); _ = backend.Catalog; Assert.Equal(reads, api.IoCalls);
        await fixture.Sampler.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var stream = backend.SubscribeAsync("process-io-rate", [field], TimeSpan.FromSeconds(1),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await stream.MoveNextAsync()); Assert.Null(stream.Current.Single().Value); // Hosted is not Direct history
            Assert.True(await stream.MoveNextAsync()); Assert.Equal(0, stream.Current.Single().Value?.Number);
            Assert.Equal(1000UL, stream.Current.Single().SampleDurationMilliseconds);
            FieldReadingAssert.CurrentOrNewer(stream.Current.Single(), backend.ReadCached(field));
        }
        finally { await fixture.Sampler.StopAsync(default); }
    }

    [Fact]
    public async Task HttpUnifiedStreamCarriesTypedPerProcessIntervalAndGetFetchesOnce()
    {
        var api = new Basic.Api { IoValue = Counts(100) }; var clock = new Clock { Step = 500 };
        using var fixture = Basic.Fixture(api, clock: clock); await using var app = await Network.StartAsync(fixture);
        using var client = Network.Client(app); var snapshotUrl = "/api/resource-breakdown/snapshot?ids=" + Raw + "," + Rate;
        using (var initial = JsonDocument.Parse(await client.GetStringAsync(snapshotUrl))) Assert.True(api.IoCalls > 0);
        var initialReads = api.IoCalls;
        await Task.Delay(1300); Assert.Equal(initialReads, api.IoCalls);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var path = "/api/resource-monitor/subscribe?scope=bars&intervalMs=1000&ids=" + Raw + "," + Rate;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "io-rate", path } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var found = false;
        while (!timeout.IsCancellationRequested)
        {
            using var frame = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            var fields = frame.RootElement.GetProperty("value").GetProperty("breakdown").GetProperty("processFields");
            if (fields.GetProperty("processes").GetArrayLength() == 0) continue;
            var process = fields.GetProperty("processes")[0];
            var rate = process.GetProperty("values").EnumerateArray().Single(value => value.GetProperty("field").GetString() == Rate);
            if (rate.GetProperty("numericValue").ValueKind != JsonValueKind.Number) continue;
            Assert.Equal(0, rate.GetProperty("numericValue").GetDouble());
            Assert.Equal(JsonValueKind.String, process.GetProperty("ioInterval").GetProperty("beginCounter").ValueKind);
            Assert.Equal(JsonValueKind.String, process.GetProperty("ioInterval").GetProperty("endCounter").ValueKind);
            Assert.Equal(JsonValueKind.Null, fields.GetProperty("windowMilliseconds").ValueKind);
            Assert.All(fields.GetProperty("sources").EnumerateArray(), static source => Assert.Equal(JsonValueKind.Null, source.GetProperty("windowMilliseconds").ValueKind));
            found = true; break;
        }
        Assert.True(found); await timeout.CancelAsync(); response.Dispose(); await Task.Delay(300);
        var reads = api.IoCalls; using var fresh = JsonDocument.Parse(await client.GetStringAsync(snapshotUrl));
        Assert.True(api.IoCalls > reads);
        var fetchedReads = api.IoCalls; await Task.Delay(1300); Assert.Equal(fetchedReads, api.IoCalls);
        await fixture.Sampler.StopAsync(default); await app.StopAsync();
    }

    private static double? Numeric(ProcessMetricFieldSnapshot snapshot) => snapshot.Processes.Single().Values.Single().NumericValue;
    private static ProcessIoObservation Counts(ulong count) => new(count, count, count, count, count, count);
    private static ProcessFieldProjectionSample Sample(int pid, long birth, ulong count, long start, long end)
        => new(pid, birth, new(null, null, Counts(count) with { Timing = new(start, end, 1000) }, null), 0, false);
    private sealed class Clock : TimeProvider
    {
        internal long Ticks = 1000, Frequency = 1000, Step;
        public override long TimestampFrequency => Frequency;
        public override long GetTimestamp() { var value = Ticks; Ticks += Step; return value; }
    }
    private sealed class Inventory(Func<Process[]> read) : IWindowsProcessInventoryReader { public Process[] GetProcesses() => read(); }
    private sealed class RejectedPermit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => false;
        public bool TryPublish(Action commit) => false;
    }
}
