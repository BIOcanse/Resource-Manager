using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class ProcessNetworkFieldCollectorTests
{
    private static readonly string[] Names = ProcessMetricFieldRegistry.Definitions.Where(static field => field.DatasetId == SamplingDatasetIds.ProcessObservedNetworkTraffic).Select(static field => field.Name).ToArray();
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static ResourceBreakdownSampleRequest Request(params string[] names)
        => ResourceBreakdownSampleRequest.ForResourceTable(names.Length == 0 ? Names : names, new Dictionary<string, string>());
    internal static string Birth
    {
        get { using var process = Process.GetCurrentProcess(); return process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture); }
    }

    [Fact]
    public async Task CaptureBindsExactBirthAndUnsignedCountsWithoutHardwareOrSoftwareAttribution()
    {
        var captured = DateTimeOffset.UtcNow.AddSeconds(-5);
        var network = new NetworkReader(_ => Window(captured, Birth, ulong.MaxValue.ToString(CultureInfo.InvariantCulture), "7"));
        using var fixture = new Fixture(network);

        var snapshot = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);

        Assert.Empty(snapshot.Bars);
        var fields = Assert.IsType<ProcessMetricFieldSnapshot>(snapshot.ProcessFields);
        Assert.Equal(captured, fields.CapturedAt);
        Assert.Equal(2000, fields.WindowMilliseconds);
        Assert.Equal(4, fields.Fields.Count);
        var row = Assert.Single(fields.Processes);
        Assert.Equal(Environment.ProcessId, row.ProcessId);
        Assert.Equal(Birth, row.CreationWindowsFileTime100Ns);
        Assert.Equal(MetricInteger.FromUnsigned128(ulong.MaxValue), row.Values[0].IntegerValue);
        Assert.Null(row.Values[0].NumericValue);
        Assert.Equal(3.5, row.Values[3].NumericValue);
        Assert.Equal(NetworkAttributionReadMode.ObservedTraffic, Assert.Single(network.Modes));
        Assert.All(fields.Fields, field => { Assert.Equal("process", field.Resource); Assert.Equal("all", field.Vendor); Assert.Equal("windows.etw.network", field.SourceId); });
        var dataset = Assert.Single(snapshot.Datasets);
        Assert.Equal(SamplingDatasetIds.ProcessObservedNetworkTraffic, dataset.DatasetId);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, dataset.Status);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ResourceBreakdownWireSnapshot.Create(snapshot, Names), WebJson));
        var wire = json.RootElement.GetProperty("processFields");
        Assert.Equal(captured, wire.GetProperty("capturedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.String, wire.GetProperty("processes")[0].GetProperty("values")[0].GetProperty("integerValue").ValueKind);
        Assert.False(wire.GetProperty("fields")[0].TryGetProperty("code", out _));
    }

    [Theory]
    [InlineData("wrong-birth")]
    [InlineData("unknown-birth")]
    [InlineData("invalid-count")]
    [InlineData("wrong-duration")]
    [InlineData("incomplete-window")]
    public async Task InvalidSourceCannotBecomeAnEstimatedOrStaleValue(string fault)
    {
        var good = Window(DateTimeOffset.UtcNow, Birth, "11", "13");
        var invalid = fault switch
        {
            "wrong-birth" => Window(DateTimeOffset.UtcNow, "1", "999", "999"),
            "unknown-birth" => Window(DateTimeOffset.UtcNow, null, "999", "999"),
            "invalid-count" => Window(DateTimeOffset.UtcNow, Birth, "18446744073709551616", "-1"),
            "wrong-duration" => good with { ObservedTraffic = good.ObservedTraffic! with { WindowMilliseconds = 1000 } },
            _ => good with { ObservedTraffic = null }
        };
        var current = good;
        var network = new NetworkReader(_ => current);
        using var fixture = new Fixture(network);
        var first = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Equal(MetricInteger.Parse("11"), first.ProcessFields!.Processes[0].Values[0].IntegerValue);
        current = invalid;

        var failed = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);

        Assert.All(Assert.Single(failed.ProcessFields!.Processes).Values, value => { Assert.Null(value.IntegerValue); Assert.Null(value.NumericValue); });
        current = good;
        var recovered = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Equal(MetricInteger.Parse("11"), recovered.ProcessFields!.Processes[0].Values[0].IntegerValue);
    }

    [Fact]
    public async Task CompleteEmptyWindowPublishesZeroAndEmptyInventoryRemovesOldProcessObjects()
    {
        var network = new NetworkReader(_ => new(DateTimeOffset.UtcNow, new Dictionary<int, NetworkProcessAttribution>(),
            new("etw-network-tcpip", NetworkAttributionProviderStates.Running, "fixture"))
            { ObservedTraffic = new(DateTimeOffset.UtcNow, 2000, []) });
        var inventoryEmpty = false;
        using var fixture = new Fixture(network, () => inventoryEmpty ? [] : [Process.GetCurrentProcess()]);
        var first = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Equal(MetricInteger.Parse("0"), first.ProcessFields!.Processes[0].Values[0].IntegerValue);
        Assert.Equal(0, first.ProcessFields.Processes[0].Values[2].NumericValue);
        inventoryEmpty = true;
        var empty = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        Assert.Empty(empty.ProcessFields!.Processes);
        Assert.Equal(4, empty.ProcessFields.Fields.Count);
    }

    [Fact]
    public void DatasetMappingsAreExplicitAndDoNotAddLegacyNetworkOrSoftwareDemands()
    {
        foreach (var field in ProcessMetricFieldRegistry.Definitions.Where(static field => field.DatasetId == SamplingDatasetIds.ProcessObservedNetworkTraffic))
            Assert.Equal(SamplingDatasetIds.ProcessObservedNetworkTraffic, SamplingDatasetIds.ForProcessMetric(field.Name));
        var datasets = WindowsResourceBreakdownSampler.GetSubscriptionDatasetIds(Request()).ToHashSet();
        Assert.Equal(2, datasets.Count);
        Assert.Contains(SamplingDatasetIds.ProcessObservedNetworkTraffic, datasets);
        Assert.Contains(SamplingDatasetIds.ProcessInventory, datasets);
        Assert.DoesNotContain(SamplingDatasetIds.ProcessAttribution, datasets);
        Assert.False(WindowsResourceBreakdownSampler.RequiresProcessAttribution(Request()));
        Assert.Null(ProcessMetricFieldRegistry.Find("process_all_network_unknown_attributed"));
    }

    [Fact]
    public async Task CacheFailureClearsValuesAndReadSelectionDoesNotAlterOtherDatasets()
    {
        var network = new NetworkReader(_ => Window(DateTimeOffset.UtcNow, Birth, "9007199254740993", "7"));
        using var fixture = new Fixture(network);
        var sample = await fixture.Sampler.CaptureSnapshotAsync(Request(), CancellationToken.None);
        var state = new ResourceBreakdownSnapshotState();
        var now = DateTimeOffset.UtcNow;
        state.ApplyDirect(Request(), sample, now, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        var selected = state.ReadOrCreate(now, Request(Names[0]));
        Assert.Single(selected.ProcessFields!.Fields);
        Assert.Single(selected.ProcessFields.Processes[0].Values);
        Assert.Equal(MetricInteger.Parse("9007199254740993"), selected.ProcessFields.Processes[0].Values[0].IntegerValue);
        state.MarkFailed(Request(), now, new InvalidOperationException("private source failure"));
        var failed = state.ReadOrCreate(now, Request());
        Assert.Null(failed.ProcessFields!.CapturedAt);
        Assert.Null(failed.ProcessFields.WindowMilliseconds);
        Assert.Equal(4, failed.ProcessFields.Fields.Count);
        Assert.All(failed.ProcessFields.Processes[0].Values, value => Assert.Null(value.IntegerValue));
        Assert.Equal(ResourceBreakdownSamplingStatuses.Failed, failed.Sampling.Status);
        Assert.Equal(1, network.Reads);
        // A raw source payload with a failed dataset must also be emptied at the wire boundary.
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(ResourceBreakdownWireSnapshot.Create(
            failed with { ProcessFields = sample.ProcessFields }, Names), WebJson));
        Assert.Equal(JsonValueKind.Null, wire.RootElement.GetProperty("processFields").GetProperty("processes")[0]
            .GetProperty("values")[0].GetProperty("integerValue").ValueKind);
        using var legacy = JsonDocument.Parse(JsonSerializer.Serialize(ResourceBreakdownWireSnapshot.Create(sample, [ResourceBreakdownMetricIds.MemoryUsage]), WebJson));
        Assert.False(legacy.RootElement.TryGetProperty("processFields", out _));
    }

    private static NetworkAttributionSnapshot Window(DateTimeOffset captured, string? birth, string? receive, string? send)
        => new(captured, new Dictionary<int, NetworkProcessAttribution>(), new("etw-network-tcpip", NetworkAttributionProviderStates.Running, "fixture"))
        { ObservedTraffic = new(captured, 2000, [new(Environment.ProcessId, birth, receive, send, 99999, 99999)]) };

    internal sealed class NetworkReader(Func<NetworkAttributionReadRequest, NetworkAttributionSnapshot> read) : INetworkAttributionReader
    {
        private int reads;
        internal int Reads => Volatile.Read(ref reads);
        internal List<NetworkAttributionReadMode> Modes { get; } = [];
        public NetworkAttributionSnapshot Read(NetworkAttributionReadRequest request)
        {
            Interlocked.Increment(ref reads);
            lock (Modes) Modes.Add(request.Mode);
            Assert.Equal(NetworkAttributionReadMode.ObservedTraffic, request.Mode);
            Assert.Equal(0, request.ReceiveBytesPerSecond);
            Assert.Equal(0, request.SendBytesPerSecond);
            return read(request);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        internal HostManagerSamplingSubscriptionTestFixture Subscriptions { get; } = new();
        internal WindowsResourceBreakdownSampler Sampler { get; }
        internal Fixture(NetworkReader reader, Func<Process[]>? inventory = null, WindowsProcessBasicMetricReader? basic = null)
        {
            // Null dependencies make any accidental hardware or software catalog call fail this test.
            Sampler = new(null!, null!, new StaticResourceResidualBreakdownProvider(), null!, reader,
                Subscriptions.Provider, Subscriptions.Owner, new PdhProcessGpuReader(),
                new InventoryReader(inventory ?? (() => [Process.GetCurrentProcess()])), basic);
        }
        public void Dispose() { Sampler.Dispose(); Subscriptions.Dispose(); }
    }

    private sealed class InventoryReader(Func<Process[]> read) : IWindowsProcessInventoryReader
    {
        public Process[] GetProcesses() => read();
    }
}
