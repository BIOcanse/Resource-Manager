using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class FanCurveReceiptTests
{
    private const string SoftwareReceipt = "\"ok\":true,\"execution\":\"software\",\"curve\":" + CompleteCurve;
    private const string AutomaticReceipt = "\"ok\":true,\"fans\":[{\"index\":0,\"automaticControl\":true},{\"index\":1,\"automaticControl\":true}]";

    [Fact]
    public async Task ExplicitSoftwareApplyBootstrapsAHelperThatRejectsPlansWithoutFreshTemperature()
    {
        await using var helper = new CurveHelper(SoftwareReceipt, requireSeed: true);
        var capability = new ControlCapability("fan.curve", "Curve", ControlValueKinds.Curve, true);
        var setting = new ControlSetting("fan.curve", Curve: [new(60, 35)], CurveExecution: ControlFanCurveExecutions.Software);
        var metrics = new TemperatureSubscriptions();
        using var feed = new FanCurveTemperatureFeed(metrics, helper.Client, new DashboardMonitoringCatalogState());
        await feed.StartAsync(default);
        var unseeded = await new FanControlWriter(helper.Client).WriteAsync(Target, capability, setting, default);
        Assert.Equal(ControlApplyStatuses.Failed, unseeded.Status);
        Assert.False(helper.Client.NeedsTemperatures());
        Assert.Equal(0, metrics.ActiveCount);

        var sampler = new ApplyTemperatureSampler(0);
        var outcome = await new FanControlWriter(helper.Client, sampler: sampler).WriteAsync(Target, capability, setting, default);
        Assert.Equal(ControlApplyStatuses.Applied, outcome.Status);
        Assert.Equal(1, sampler.Captures);
        Assert.Equal(["describe", "setcurve", "temp", "setcurve"], File.ReadAllLines(helper.Operations));
        await WaitForLeaseAsync(metrics);
        helper.Client.Dispose();
        Assert.Equal(0, metrics.ActiveCount);
        await feed.StopAsync(default);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(1)]
    [InlineData(null)]
    public async Task SoftwareApplyWithoutConfirmedCurrentTemperatureNeverSubmitsAPlanOrStartsALease(int? sampleAgeSeconds)
    {
        await using var helper = new CurveHelper(SoftwareReceipt, requireSeed: true);
        var sampler = new ApplyTemperatureSampler(sampleAgeSeconds);
        var outcome = await new FanControlWriter(helper.Client, sampler: sampler).WriteAsync(Target,
            new("fan.curve", "Curve", ControlValueKinds.Curve, true),
            new("fan.curve", Curve: [new(60, 35)], CurveExecution: ControlFanCurveExecutions.Software), default);
        Assert.Equal(ControlApplyStatuses.Failed, outcome.Status);
        Assert.Equal(1, sampler.Captures);
        Assert.Equal(["describe"], File.ReadAllLines(helper.Operations));
        Assert.False(helper.Client.NeedsTemperatures());
    }

    [Fact]
    public async Task FailedApplyAfterTemperatureBootstrapDoesNotCreateOngoingDemand()
    {
        await using var helper = new CurveHelper("\"ok\":false", requireSeed: true);
        var outcome = await new FanControlWriter(helper.Client, sampler: new ApplyTemperatureSampler(0)).WriteAsync(Target,
            new("fan.curve", "Curve", ControlValueKinds.Curve, true),
            new("fan.curve", Curve: [new(60, 35)], CurveExecution: ControlFanCurveExecutions.Software), default);
        Assert.Equal(ControlApplyStatuses.Failed, outcome.Status);
        Assert.Equal(["describe", "temp", "setcurve"], File.ReadAllLines(helper.Operations));
        Assert.False(helper.Client.NeedsTemperatures());
    }

    [Fact]
    public async Task FirmwareEditingSeesExternalChangesAndDoesNotReuseAnEarlierReadAfterFailure()
    {
        await using var changed = new CurveHelper($"\"ok\":true,\"execution\":\"firmware\",\"curve\":{CompleteCurve}",
            "\"ok\":true,\"execution\":\"firmware\",\"curve\":[{\"temperatureCelsius\":60,\"percent\":75}]");
        var first = await changed.Client.ReadCurveAsync(0, default);
        var current = await changed.Client.ReadCurveAsync(0, default);
        Assert.NotEqual(first, current);
        Assert.Equal(75, Assert.Single(current!).Percent);
        Assert.Equal(2, File.ReadAllLines(changed.Operations).Count(operation => operation == "curve"));

        await using var unavailable = new CurveHelper($"\"ok\":true,\"execution\":\"firmware\",\"curve\":{CompleteCurve}",
            "\"ok\":false,\"curve\":null");
        Assert.NotNull(await unavailable.Client.ReadCurveAsync(0, default));
        Assert.Null(await unavailable.Client.ReadCurveAsync(0, default));
    }

    [Fact]
    public async Task CapabilityAndReadsDoNotAcquireDemandAndOnlyLastAppliedSoftwareReleaseDropsTheLease()
    {
        await using var helper = new CurveHelper(SoftwareReceipt, SoftwareReceipt, AutomaticReceipt);
        var metrics = new TemperatureSubscriptions();
        using var feed = new FanCurveTemperatureFeed(metrics, helper.Client, new DashboardMonitoringCatalogState());
        await feed.StartAsync(default);
        Assert.True(helper.Client.Describe().All(fan => fan.SupportsSoftwareCurve));
        Assert.False(helper.Client.NeedsTemperatures());
        Assert.NotNull(await helper.Client.ReadCurveAsync(0, default));
        Assert.False(helper.Client.NeedsTemperatures());
        Assert.Equal(0, metrics.ActiveCount);
        var writer = new FanControlWriter(helper.Client);
        var capability = new ControlCapability("fan.curve", "Curve", ControlValueKinds.Curve, true);
        var setting = new ControlSetting("fan.curve", Curve: [new(60, 35), new(120, 100)],
            CurveExecution: ControlFanCurveExecutions.Software);
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Target, capability, setting, default)).Status);
        await WaitForLeaseAsync(metrics);
        Assert.Equal(FanCurveTemperatureFeed.Interval, metrics.Interval);
        Assert.Equal(["cpu.temperature"], metrics.Request!.Ids);
        var other = Target with { Id = "fan:oem1" };
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(other, capability, setting, default)).Status);
        Assert.Equal(1, metrics.Acquisitions);
        Assert.True(await writer.RestoreAsync(Target, capability, new HashSet<string>(), default, setting));
        Assert.True(helper.Client.NeedsTemperatures());
        Assert.Equal(1, metrics.ActiveCount);
        Assert.True(await writer.RestoreAsync(other, capability, new HashSet<string>(), default, setting));
        Assert.False(helper.Client.NeedsTemperatures());
        Assert.Equal(0, metrics.ActiveCount); // Disposed before RestoreAsync returns, without a polling delay.
        await feed.StopAsync(default);
    }

    [Theory]
    [InlineData("firmware")]
    [InlineData("duty")]
    [InlineData("boost")]
    [InlineData("dispose")]
    public async Task EndingSoftwareExecutionDropsDemandSynchronously(string action)
    {
        await using var helper = new CurveHelper(SoftwareReceipt,
            $"\"ok\":true,\"execution\":\"firmware\",\"curve\":{CompleteCurve}", AutomaticReceipt);
        var metrics = new TemperatureSubscriptions();
        using var feed = new FanCurveTemperatureFeed(metrics, helper.Client, new DashboardMonitoringCatalogState());
        await feed.StartAsync(default);
        var writer = new FanControlWriter(helper.Client);
        var capability = new ControlCapability("fan.curve", "Curve", ControlValueKinds.Curve, true);
        var setting = new ControlSetting("fan.curve", Curve: [new(60, 35), new(120, 100)],
            CurveExecution: ControlFanCurveExecutions.Software);
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Target, capability, setting, default)).Status);
        await WaitForLeaseAsync(metrics);
        if (action == "dispose") helper.Client.Dispose();
        else
        {
            var id = action == "firmware" ? "fan.curve" : action == "duty" ? "fan.duty" : "fan.lock-maximum";
            var next = setting with { CapabilityId = id, CurveExecution = ControlFanCurveExecutions.Firmware, Number = 55, Toggle = true };
            // The boost fixture reports automatic, so the writer rejects its full-duty readback;
            // a successful helper boost nevertheless ends the previous software plan.
            await writer.WriteAsync(Target, capability with { Id = id }, next, default);
        }
        Assert.False(helper.Client.NeedsTemperatures());
        Assert.Equal(0, metrics.ActiveCount);
        await feed.StopAsync(default);
    }

    [Theory]
    [InlineData("\"ok\":false,\"execution\":\"software\",\"curve\":null")]
    [InlineData("\"ok\":true,\"execution\":\"software\",\"curve\":[{}]")]
    public async Task FailedSoftwareApplyNeverStartsDemand(string receipt)
    {
        await using var helper = new CurveHelper(receipt);
        var outcome = await new FanControlWriter(helper.Client).WriteAsync(Target,
            new("fan.curve", "Curve", ControlValueKinds.Curve, true),
            new("fan.curve", Curve: [new(60, 35)], CurveExecution: ControlFanCurveExecutions.Software), default);
        Assert.Equal(ControlApplyStatuses.Failed, outcome.Status);
        Assert.False(helper.Client.NeedsTemperatures());
    }

    [Fact]
    public async Task ExitedHelperReleasesItsTemperatureLeaseWithoutAnotherHardwareRequest()
    {
        await using var helper = new CurveHelper(SoftwareReceipt);
        var metrics = new TemperatureSubscriptions();
        using var feed = new FanCurveTemperatureFeed(metrics, helper.Client, new DashboardMonitoringCatalogState());
        await feed.StartAsync(default);
        Assert.Equal(ControlApplyStatuses.Applied, (await new FanControlWriter(helper.Client).WriteAsync(Target,
            new("fan.curve", "Curve", ControlValueKinds.Curve, true),
            new("fan.curve", Curve: [new(60, 35)], CurveExecution: ControlFanCurveExecutions.Software), default)).Status);
        await WaitForLeaseAsync(metrics);
        await helper.Client.SendAsync("exit-fixture", null, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (metrics.ActiveCount != 0) await Task.Delay(10, deadline.Token);
        Assert.False(helper.Client.NeedsTemperatures());
        await feed.StopAsync(default);
    }

    private static async Task WaitForLeaseAsync(TemperatureSubscriptions metrics)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (metrics.ActiveCount == 0) await Task.Delay(10, deadline.Token);
        Assert.Equal(1, metrics.ActiveCount);
    }

    private sealed class TemperatureSubscriptions : IMetricSnapshotObservationSource
    {
        private int active;
        internal int ActiveCount => Volatile.Read(ref active);
        internal int Acquisitions { get; private set; }
        internal TimeSpan Interval { get; private set; }
        internal MetricSampleRequest? Request { get; private set; }
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan refreshInterval)
        {
            Assert.Equal("control.fan-curve.temperature", id);
            Request = request;
            Interval = refreshInterval;
            Acquisitions++;
            Interlocked.Increment(ref active);
            return new Lease(this);
        }
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => null;
        private sealed class Lease(TemperatureSubscriptions owner) : IDisposable
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Decrement(ref owner.active);
            }
        }
    }

    private sealed class ApplyTemperatureSampler(int? sampleAgeSeconds) : IMetricSampler
    {
        internal int Captures { get; private set; }
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken token)
        {
            Captures++;
            Assert.Equal(["cpu.temperature"], request.Ids);
            var now = DateTimeOffset.UtcNow;
            var items = new Dictionary<string, MetricValue>();
            var datasets = new Dictionary<string, HardwareMetricDatasetObservation>();
            if (sampleAgeSeconds is { } offset)
            {
                items["cpu.temperature"] = new("cpu.temperature", "CPU", "temperature", "", 60d, "°C", null, null);
                datasets["cpu.temperature"] = new("cpu.temperature", SamplingObservationStatus.Current, 1,
                    now.AddSeconds(offset).UtcTicks, 1, 1, 1, 1);
            }
            return Task.FromResult(new HardwareMetricSnapshot(now, null!, null!, null!, [], null!, items) { Datasets = datasets });
        }
    }
}
