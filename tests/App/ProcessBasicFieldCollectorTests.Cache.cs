using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using NetworkTests = Resource_Manager_APP.Tests.ProcessNetworkFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class ProcessBasicFieldCollectorTests
{
    [Fact]
    public async Task MixedSourcesKeepIndependentCaptureWindowsAndSelectionDoesNotReadSources()
    {
        var captured = DateTimeOffset.UtcNow.AddSeconds(-5);
        DateTimeOffset? beforeNetwork = null;
        var api = new Api(); var network = new NetworkTests.NetworkReader(_ =>
        {
            beforeNetwork = DateTimeOffset.UtcNow;
            Thread.Sleep(30);
            return Network(captured);
        });
        using var fixture = Fixture(api, network);
        var mixed = await fixture.Sampler.CaptureSnapshotAsync(Request([.. Names, .. NetworkNames]), CancellationToken.None);
        Assert.Equal(14, mixed.ProcessFields!.Fields.Count); Assert.Equal(2, mixed.ProcessFields.Sources.Count);
        Assert.Null(mixed.ProcessFields.WindowMilliseconds); Assert.Equal(14, Assert.Single(mixed.ProcessFields.Processes).Values.Count);
        var basic = mixed.ProcessFields.Select(Names.ToHashSet()); var net = mixed.ProcessFields.Select(NetworkNames.ToHashSet());
        Assert.Null(basic.WindowMilliseconds); Assert.True(basic.CapturedAt > captured);
        Assert.True(basic.CapturedAt <= beforeNetwork);
        Assert.Equal(2000, net.WindowMilliseconds); Assert.Equal(captured, net.CapturedAt);
        Assert.Equal("windows.process.query", Assert.Single(basic.Sources).SourceId);
        Assert.Equal("windows.etw.network", Assert.Single(net.Sources).SourceId);
        var state = new ResourceBreakdownSnapshotState(); var now = DateTimeOffset.UtcNow;
        state.ApplyDirect(Request([.. Names, .. NetworkNames]), mixed, now, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Equal(basic.CapturedAt, state.ReadOrCreate(now, Request(Names[1])).ProcessFields!.CapturedAt);
        Assert.Equal(captured, state.ReadOrCreate(now, Request(NetworkNames[0])).ProcessFields!.CapturedAt);
        Assert.Equal(1, api.Opens); Assert.Equal(1, network.Reads);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOrThrowingNetworkSourceDoesNotDiscardCurrentBasicCounters(bool throws)
    {
        var failed = false; var captured = DateTimeOffset.UtcNow;
        var network = new NetworkTests.NetworkReader(_ => failed
            ? throws ? throw new InvalidOperationException("fixture failure") : Network(captured) with { ObservedTraffic = null }
            : Network(captured));
        var api = new Api(); using var fixture = Fixture(api, network); var request = Request([.. Names, .. NetworkNames]);
        var first = await fixture.Sampler.CaptureSnapshotAsync(request, CancellationToken.None);
        Assert.Equal(MetricInteger.Parse("9007199254740993"), Value(first, NetworkNames[0]).IntegerValue);
        failed = true; var sample = await fixture.Sampler.CaptureSnapshotAsync(request, CancellationToken.None);
        Assert.Equal(MetricInteger.Parse("9007199254740993"), Value(sample, Names[1]).IntegerValue); Assert.Equal(9.75, Value(sample, Names[0]).NumericValue);
        Assert.Null(Value(sample, NetworkNames[0]).IntegerValue);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, Assert.Single(sample.Datasets, d => d.DatasetId == SamplingDatasetIds.ProcessBasicMetrics).Status);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Failed, Assert.Single(sample.Datasets, d => d.DatasetId == SamplingDatasetIds.ProcessObservedNetworkTraffic).Status);
        Assert.NotNull(Assert.Single(sample.ProcessFields!.Sources, s => s.SourceId == "windows.process.query").CapturedAt);
        Assert.Null(Assert.Single(sample.ProcessFields.Sources, s => s.SourceId == "windows.etw.network").CapturedAt);
        failed = false; var recovered = await fixture.Sampler.CaptureSnapshotAsync(request, CancellationToken.None);
        Assert.Equal(MetricInteger.Parse("9007199254740993"), Value(recovered, NetworkNames[0]).IntegerValue); Assert.Equal(3, api.Opens);
    }

    [Fact]
    public async Task CacheAndWireClearOnlyFailedDatasetEvenIfRawPayloadStillHasOldValues()
    {
        var captured = DateTimeOffset.UtcNow; var api = new Api();
        using var fixture = Fixture(api, new NetworkTests.NetworkReader(_ => Network(captured)));
        var request = Request([.. Names, .. NetworkNames]); var sample = await fixture.Sampler.CaptureSnapshotAsync(request, CancellationToken.None);
        var state = new ResourceBreakdownSnapshotState(); var now = DateTimeOffset.UtcNow;
        state.ApplyDirect(request, sample, now, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        state.MarkFailed(Request(Names[1]), now, new InvalidOperationException("basic source failed"));
        var failed = state.ReadOrCreate(now, request);
        Assert.Null(Value(failed, Names[1]).IntegerValue); Assert.Equal(MetricInteger.Parse("9007199254740993"), Value(failed, NetworkNames[0]).IntegerValue);
        Assert.Equal(captured, failed.ProcessFields!.CapturedAt);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(ResourceBreakdownWireSnapshot.Create(failed with { ProcessFields = sample.ProcessFields }, [.. Names, .. NetworkNames]), WebJson));
        var fields = wire.RootElement.GetProperty("processFields"); var values = fields.GetProperty("processes")[0].GetProperty("values");
        Assert.Equal(JsonValueKind.Null, Assert.Single(values.EnumerateArray(), v => v.GetProperty("field").GetString() == Names[1]).GetProperty("integerValue").ValueKind);
        Assert.Equal("9007199254740993", Assert.Single(values.EnumerateArray(), v => v.GetProperty("field").GetString() == NetworkNames[0]).GetProperty("integerValue").GetString());
        Assert.False(fields.GetProperty("fields")[0].TryGetProperty("readMask", out _)); Assert.False(fields.GetProperty("fields")[0].TryGetProperty("datasetId", out _));
        state.MarkFailed(now, new InvalidOperationException("whole source failed"));
        var allFailed = state.ReadOrCreate(now, request); Assert.Null(allFailed.ProcessFields!.CapturedAt);
        Assert.All(allFailed.ProcessFields.Processes[0].Values, v => { Assert.Null(v.IntegerValue); Assert.Null(v.NumericValue); });
        Assert.Equal(1, api.Opens);
    }

    [Fact]
    public void MergeDoesNotAttachAnotherCreationOfSamePidToOldCounterValues()
    {
        var basic = new ProcessMetricFieldSnapshot(DateTimeOffset.UtcNow, null, [ProcessMetricFieldRegistry.Find(Names[1])!],
            [new(42, "100", [new(Names[1], MetricInteger.Parse("5"), null)])]);
        var net = new ProcessMetricFieldSnapshot(DateTimeOffset.UtcNow, 2000, [ProcessMetricFieldRegistry.Find(NetworkNames[0])!],
            [new(42, "200", [new(NetworkNames[0], MetricInteger.Parse("7"), null)]), new(43, "100", [new(NetworkNames[0], MetricInteger.Parse("8"), null)])]);
        var merged = ProcessMetricFieldSnapshot.Merge([basic, net]); Assert.Equal(3, merged.Processes.Count);
        Assert.Equal(Names[1], Assert.Single(Assert.Single(merged.Processes, r => r.ProcessId == 42 && r.CreationWindowsFileTime100Ns == "100").Values).Field);
        Assert.Equal(NetworkNames[0], Assert.Single(Assert.Single(merged.Processes, r => r.ProcessId == 42 && r.CreationWindowsFileTime100Ns == "200").Values).Field);
    }

    [Fact]
    public async Task FailedDirectInventoryThrowsWithoutReadingSourcesAndEmptyRecoveryRemovesOldObjects()
    {
        var phase = 0; var api = new Api();
        using var fixture = Fixture(api, new NetworkTests.NetworkReader(_ => Network(DateTimeOffset.UtcNow)),
            () => phase switch { 1 => throw new InvalidOperationException("inventory failed"), 2 => [], _ => [Process.GetCurrentProcess()] });
        var request = Request([.. Names, .. NetworkNames]); var first = await fixture.Sampler.CaptureSnapshotAsync(request, CancellationToken.None);
        Assert.Single(first.ProcessFields!.Processes); phase = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Sampler.CaptureSnapshotAsync(request, CancellationToken.None));
        Assert.Equal(1, api.Opens);
        phase = 2; var empty = await fixture.Sampler.CaptureSnapshotAsync(request, CancellationToken.None);
        Assert.Empty(empty.ProcessFields!.Processes); Assert.Equal(14, empty.ProcessFields.Fields.Count); Assert.NotNull(empty.ProcessFields.CapturedAt); Assert.Equal(1, api.Opens);
    }
}
