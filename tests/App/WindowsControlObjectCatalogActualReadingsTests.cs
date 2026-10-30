using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsControlObjectCatalogActualReadingsTests
{
    [Fact]
    public async Task PublishedTopologyNeedsNoMetricHoldOrFetchAndLaterRoundUsesPublishedFanValue()
    {
        var state = new HardwareMetricCatalogState();
        var declared = Topology();
        state.PublishCatalogProbe(declared);
        var source = new Metrics(state);
        var opened = 0;
        var closed = 0;
        var discoveries = 0;
        await using var fan = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            opened++;
            return new DelegateFanSession(request =>
            {
                if (request.GetProperty("op").GetString() == "describe") discoveries++;
                return JsonSerializer.SerializeToElement(new { ok = true, available = true, fans = Array.Empty<object>() });
            }, () => closed++);
        });
        var writer = new Writer(source);
        var catalog = Catalog(state, source, fan, writer);
        var clock = new ControlActualStateReaderTests.ControlledClock();
        using var reader = new ControlActualStateReader(catalog, [writer], clock);
        using var lease = reader.AcquireSubscription(TimeSpan.FromSeconds(10));
        await reader.StartAsync(default);
        await ControlActualStateReaderTests.WaitUntilAsync(() => reader.Current.Values.Count > 0);
        Assert.Equal(DateTimeOffset.UnixEpoch, reader.Current.ReadAt);
        Assert.Contains(reader.Current.Values, value => value.CapabilityId == "cpu.power-limit"
            && value.Number is null && value.UnreadableReason is not null);
        Assert.Equal(0, source.Subscriptions);
        Assert.Equal(0, source.Fetches);
        Assert.Equal(1, discoveries);

        source.Latest = declared with { Items = new Dictionary<string, MetricReadingValue>
            { ["cpu.fanRpm"] = new("cpu.fanRpm", 1200, "RPM") } };
        await clock.WaitForDueAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        await ControlActualStateReaderTests.WaitUntilAsync(() =>
            reader.Current.Values.Any(value => value.CapabilityId == "fan.rpm" && value.Number == 1200));
        await clock.WaitForDueAsync(TimeSpan.FromSeconds(20));
        clock.Advance(TimeSpan.FromSeconds(10));
        await ControlActualStateReaderTests.WaitUntilAsync(() => reader.Current.ReadAt == DateTimeOffset.UnixEpoch.AddSeconds(20));
        Assert.Equal(0, source.Subscriptions);
        Assert.Equal(0, source.Fetches);
        Assert.Equal(1, discoveries);
        Assert.Equal(1, opened);
        Assert.Equal(1, closed); // Finite discovery released its resource; rounds never open sessions.
        await reader.StopAsync(default);
    }

    [Fact]
    public async Task NeverPublishedTopologyGetsOnlyOneCatalogProbeThenAllRoundsReadCurrent()
    {
        var state = new HardwareMetricCatalogState();
        var source = new Metrics(state);
        await using var fan = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
            new DelegateFanSession(_ => JsonSerializer.SerializeToElement(new
                { ok = true, available = true, fans = Array.Empty<object>() })));
        var writer = new Writer(source);
        var catalog = Catalog(state, source, fan, writer);
        var clock = new ControlActualStateReaderTests.ControlledClock();
        using var reader = new ControlActualStateReader(catalog, [writer], clock);
        using var lease = reader.AcquireSubscription(TimeSpan.FromSeconds(10));
        await reader.StartAsync(default);
        await ControlActualStateReaderTests.WaitUntilAsync(() => reader.Current.Values.Count > 0);
        Assert.Equal(1, source.Fetches);
        Assert.Equal("control.actual-state-reader:topology", source.Holder);
        Assert.True(source.Request!.IsCatalogProbe);
        Assert.Equal(0, source.Subscriptions);
        await clock.WaitForDueAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        await ControlActualStateReaderTests.WaitUntilAsync(() => reader.Current.ReadAt == DateTimeOffset.UnixEpoch.AddSeconds(10));
        Assert.Equal(1, source.Fetches);
        await reader.StopAsync(default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3500)]
    public void SharedStopBufferDefaultAndOverridesAreCompiledAndExported(int milliseconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(10), SubscriptionSourceSettings.ProductStopBuffer);
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.SingleRequestStopBuffer)["value"] = milliseconds);
        Assert.Equal(TimeSpan.FromSeconds(10), baseline.SingleRequestStopBuffer);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), changed.SingleRequestStopBuffer);
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
        var point = Assert.Single(changed.FreedomPoints.EnumeratePoints(),
            point => point.Address == BackendFreedomPointPaths.SingleRequestStopBuffer);
        Assert.Equal("single_request_stop_buffer_ms", point.Id);
        Assert.Equal(milliseconds, point.Value!.Value.GetInt32());
        Assert.Equal("SubscriptionSourceSettings.CompileStopBuffer", Assert.Single(point.Consumers));
    }

    private static WindowsControlObjectCatalog Catalog(HardwareMetricCatalogState state, Metrics source,
        FanControlCoreClient fan, IControlWriter writer)
        => new(state, [writer], new Access(), fan, new WindowsChassisKindReader(() => ControlChassisKinds.Unknown),
            source, source, new WindowsCpuIdentityReader(queryIdentity: _ => "recorded"));

    private static HardwareMetricSnapshot Topology()
        => new HardwareMetricCatalog(new("Recorded Intel CPU", 0, "recorded"), [],
            new Dictionary<string, HardwareMetricItemDescriptor>(), new Dictionary<string, MetricDeviceFieldBinding>(),
            1, 1, 1).CreateSnapshot();

    private sealed class Access : IControlAccessLevel
    {
        public string Current => ControlAccessLevels.Root;
        public Task SetAsync(string level, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Metrics(HardwareMetricCatalogState state) : IMetricSnapshotObservationSource, IMetricSnapshotPushSource
    {
        internal HardwareMetricSnapshot? Latest;
        internal int Subscriptions, Fetches;
        internal string? Holder;
        internal MetricSampleRequest? Request;
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval)
        {
            Subscriptions++;
            throw new InvalidOperationException("The actual reader must not hold metrics.");
        }
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => Latest;
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string holder, MetricSampleRequest request,
            TimeSpan interval, [EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Holder = holder;
            Request = request;
            Fetches++;
            Assert.True(request.IsCatalogProbe);
            var declared = Topology();
            state.PublishCatalogProbe(declared);
            await Task.CompletedTask;
            yield return declared;
        }
    }

    private sealed class Writer(Metrics source) : IControlWriter
    {
        public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
            => capability.Id is "cpu.power-limit" or "fan.rpm" ? ControlWriteAvailability.Yes()
                : ControlWriteAvailability.No("control-setting-read-unavailable");
        public Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability capability, CancellationToken token)
            => Task.FromResult<ControlActualValue?>(source.Latest is null ? null
                : new(target.Id, capability.Id, capability.Id == "fan.rpm"
                    ? source.Latest.Items["cpu.fanRpm"].NumericValue : 42));
        public Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability,
            ControlSetting setting, CancellationToken token) => throw new NotSupportedException();
    }
}
