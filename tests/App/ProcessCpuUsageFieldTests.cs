using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;
using Basic = Resource_Manager_APP.Tests.ProcessBasicFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessCpuUsageFieldTests
{
    private const string Usage = "process_all_cpu_usage_actual";

    [Theory]
    [InlineData("times-before")] [InlineData("identity-before")]
    public async Task UnverifiedRawTimeClearsPercentAndRecoveryNeedsAnotherActualCapture(string fault)
    {
        var api = new Basic.Api(); using var fixture = Basic.Fixture(api); var request = Basic.Request(Usage);
        await fixture.Sampler.CaptureSnapshotAsync(request, default);
        api.Fault = fault;
        var failed = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        Assert.Null(Basic.Value(failed, Usage).NumericValue);
        api.Fault = "";
        var recoveredFirst = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        Assert.Null(Basic.Value(recoveredFirst, Usage).NumericValue);
        var recoveredSecond = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        Assert.Equal(0, Basic.Value(recoveredSecond, Usage).NumericValue);
        Assert.Equal(0, api.MemoryCalls + api.IoCalls + api.HandleCalls);
    }

    [Fact]
    public async Task CanonicalOnlyReadsVerifiedCpuAndPublishesFirstSampleThenZeroWithoutHardwareOrAttribution()
    {
        var api = new Basic.Api(); using var fixture = Basic.Fixture(api); var request = Basic.Request(Usage);
        var first = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        Assert.Null(Basic.Value(first, Usage).NumericValue);
        var initialSource = Assert.Single(first.ProcessFields!.Sources);
        Assert.Equal("windows.process.cpu-interval", initialSource.SourceId); Assert.Null(initialSource.WindowMilliseconds);
        var second = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        Assert.Equal(0, Basic.Value(second, Usage).NumericValue);
        Assert.NotNull(Assert.Single(second.ProcessFields!.Sources).WindowMilliseconds);
        Assert.Empty(second.Bars); Assert.Equal(2, api.Opens); Assert.Equal(2, api.TimeCalls); Assert.Equal(2, api.Closes);
        Assert.Equal(0, api.MemoryCalls + api.IoCalls + api.HandleCalls);
        Assert.False(WindowsResourceBreakdownSampler.RequiresProcessAttribution(request));
        Assert.Equal(new[] { SamplingDatasetIds.ProcessBasicMetrics, SamplingDatasetIds.ProcessInventory }.Order(),
            WindowsResourceBreakdownSampler.GetSubscriptionDatasetIds(request).Order());
    }

    [Fact]
    public async Task MixedCanonicalTimeMemoryAndPercentKeepQueryWindowSeparate()
    {
        var api = new Basic.Api(); using var fixture = Basic.Fixture(api); var request = Basic.Request(Usage, Basic.Names[0], Basic.Names[1]);
        await fixture.Sampler.CaptureSnapshotAsync(request, default);
        var second = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        var fields = second.ProcessFields!; Assert.Equal(2, fields.Sources.Count); Assert.Null(fields.WindowMilliseconds);
        var interval = fields.Select(new HashSet<string> { Usage });
        var query = fields.Select(new HashSet<string> { Basic.Names[0], Basic.Names[1] });
        Assert.NotNull(interval.WindowMilliseconds); Assert.Null(query.WindowMilliseconds);
        Assert.Equal("windows.process.query", Assert.Single(query.Sources).SourceId);
        Assert.Equal(interval.CapturedAt, query.CapturedAt); Assert.Equal(9.75, Basic.Value(second, Basic.Names[0]).NumericValue);
        Assert.Equal(MetricInteger.Parse("9007199254740993"), Basic.Value(second, Basic.Names[1]).IntegerValue);
        Assert.Equal(2, api.Opens); Assert.Equal(2, api.MemoryCalls); Assert.Equal(0, api.IoCalls + api.HandleCalls);
    }

    [Fact]
    public async Task PublicDirectAndSubscriptionShareIntervalPublicationAndCacheReadsDoNotCapture()
    {
        var api = new Basic.Api(); using var fixture = Basic.Fixture(api); var backend = new HostProcessFieldBackend(fixture.Sampler);
        await backend.DiscoverAsync(default);
        var capability = Assert.Single(backend.Catalog.Fields, item => item.Field.FieldName == Usage);
        Assert.True(capability.Read.Supported); Assert.False(capability.Write.Supported);
        Assert.Equal("windows.process.cpu-interval", capability.ReadSource);
        Assert.Equal($"windows-process|{Environment.ProcessId}:{Basic.Birth}", capability.Field.DeviceIdentity);
        Assert.Equal(1, api.Opens); // DiscoverAsync is the existing explicit direct capture and seeds DirectCpu.
        var discovered = backend.ReadCached(capability.Field);
        Assert.Equal(FieldObservationStatus.Unavailable, discovered.Status); // CPU usage has no interval baseline yet.
        var opens = api.Opens; _ = backend.Catalog; _ = backend.ReadCached(capability.Field); Assert.Equal(opens, api.Opens);

        await fixture.Sampler.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(2), timeout.Token); // the projection truncates CPU intervals to whole milliseconds
            var direct = Assert.Single(await backend.CollectAsync([capability.Field], timeout.Token));
            Assert.Equal(0, direct.Value?.Number); Assert.True(direct.SampleDurationMilliseconds is > 0);
            Assert.Equal(direct, backend.ReadCached(capability.Field));
            Assert.Equal(2, api.Opens); // discovery capture plus explicit direct query

            using var subscriptionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var stream = backend.SubscribeAsync("cpu-usage", [capability.Field], TimeSpan.FromSeconds(1),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), subscriptionTimeout.Token)
                .GetAsyncEnumerator(subscriptionTimeout.Token);
            Assert.True(await stream.MoveNextAsync());
            var firstHostedQuery = Assert.Single(stream.Current);
            Assert.Equal(FieldObservationStatus.Unavailable, firstHostedQuery.Status);
            Assert.Null(firstHostedQuery.Value); // The direct baseline is not the hosted worker's fresh-query result.
            Assert.Null(firstHostedQuery.SampleDurationMilliseconds);
            Assert.Equal(3, api.Opens); // this unavailable value still came from the first original worker query

            Assert.True(await stream.MoveNextAsync());
            var reading = Assert.Single(stream.Current);
            Assert.Equal(FieldObservationStatus.Current, reading.Status);
            Assert.Equal(0, reading.Value?.Number);
            Assert.True(reading.SampleDurationMilliseconds is > 0);
            Assert.Equal(4, api.Opens); // exactly one actual query on each fresh trigger
            Assert.Equal(reading, backend.ReadCached(capability.Field));
            var currentSnapshot = fixture.Sampler.ReadLatest(Basic.Request(Usage));
            var current = currentSnapshot.ProcessFields!;
            Assert.Equal(0, Basic.Value(currentSnapshot, Usage).NumericValue);
            var cpuSource = Assert.Single(current.Sources);
            Assert.Equal("windows.process.cpu-interval", cpuSource.SourceId);
            Assert.Equal((long)reading.SampleDurationMilliseconds!.Value, cpuSource.WindowMilliseconds);
            opens = api.Opens; _ = backend.Catalog; _ = backend.ReadCached(capability.Field); Assert.Equal(opens, api.Opens);

            var opensAtCancellation = api.Opens;
            var pending = stream.MoveNextAsync().AsTask();
            subscriptionTimeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            await Task.Delay(TimeSpan.FromMilliseconds(1_100));
            Assert.Equal(opensAtCancellation, api.Opens); // the canceled final lease must stop periodic CPU queries

            api.Fault = "identity-before";
            var failed = await backend.CollectAsync([capability.Field], timeout.Token);
            Assert.Null(Assert.Single(failed).Value); Assert.Null(backend.ReadCached(capability.Field).Value);
            Assert.Equal(opensAtCancellation + 1, api.Opens); // only the explicit failing direct query ran
        }
        finally
        {
            await fixture.Sampler.StopAsync(CancellationToken.None);
        }
    }
}
