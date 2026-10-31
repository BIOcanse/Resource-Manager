using System.Text.Json;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

namespace Resource_Manager_APP.Tests;

/// <summary>Recording gaps and concrete reasons (docs/targeted_recording_views_2026-10-30.md §2.1, §3.1, §3.2,
/// §3.5): one point per source publication at its observation time, source-not-refreshed after the configured
/// share of the interval, source reasons passed through, whole-span not-applicable/no-source, gpu.target.*, the
/// default profile and the one-time migration of the old default.</summary>
public sealed class TargetedRecordingGapsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 5, 0, 0, TimeSpan.Zero);
    private static readonly TargetedRecordingCatalogItem MetricEntry =
        new(TargetedRecordingItemKinds.Metric, "cpu.usage", "%", ["load@cpu0"], null, TargetedRecordingItemUses.Both);

    private static TargetedRecordingService.ItemPlan SampledPlan(int intervalMs = 1000, int percent = 150)
    {
        var plan = new TargetedRecordingService.ItemPlan(new(TargetedRecordingItemKinds.Metric, "cpu.usage"), MetricEntry,
            "%", intervalMs, TargetedRecordingIntervalOrigins.ProfileDefault);
        plan.Begin(Start, percent, 8192);
        return plan;
    }

    private static TargetedRecordingItemReading Value(double value, int observedMs)
        => new(value, "load@cpu0", "%", Start.AddMilliseconds(observedMs));

    [Fact]
    public void EachPublicationIsOnePointAtTheSourceObservationTime()
    {
        var plan = SampledPlan();
        plan.Accept(Value(10, 990), Start.AddMilliseconds(1100));
        // The same observation delivered again is the same point, not a new one.
        plan.Accept(Value(10, 990), Start.AddMilliseconds(2000));
        plan.Accept(Value(11, 1990), Start.AddMilliseconds(2050));
        plan.Accept(Value(11, 2980), Start.AddMilliseconds(3010));
        var points = plan.Drain();
        Assert.Equal(new[] { 990, 1990, 2980 }, points.Select(static point => (int)(point.At - Start).TotalMilliseconds));
        Assert.Equal(new double[] { 10, 11, 11 }, points.Select(static point => point.Value));
        Assert.Empty(plan.Drain());
        foreach (var point in points) plan.RecordPoint((long)(point.At - Start).TotalMilliseconds);
        var report = plan.FinalReport(Start.AddMilliseconds(3500), 3500, false);
        Assert.Equal(3, report.PointCount);
        Assert.Empty(report.Gaps);
        Assert.Equal(new[] { "load@cpu0" }, report.ActualSources);
        Assert.Equal(1000, report.ActualIntervalMs);
    }

    [Fact]
    public void SilenceLongerThanTheConfiguredShareOfTheIntervalIsASourceNotRefreshedGap()
    {
        var plan = SampledPlan();
        plan.Accept(Value(1, 1000), Start.AddMilliseconds(1000));
        // 1.4 intervals: no gap.
        plan.Accept(Value(1, 2400), Start.AddMilliseconds(2400));
        // 1.6 intervals: a gap from one interval after the earlier point to the later point.
        plan.Accept(Value(1, 4000), Start.AddMilliseconds(4000));
        var report = plan.FinalReport(Start.AddMilliseconds(7000), 7000, false);
        Assert.Equal(
            new[]
            {
                new TargetedRecordingGap(3400, 4000, TargetedRecordingGapReasons.SourceNotRefreshed),
                new TargetedRecordingGap(5000, 7000, TargetedRecordingGapReasons.SourceNotRefreshed)
            }, report.Gaps);

        // The share is the freedom point's value: at 200 % the same 1.6-interval silence is no gap.
        var wide = SampledPlan(percent: 200);
        wide.Accept(Value(1, 1000), Start.AddMilliseconds(1000));
        wide.Accept(Value(1, 2600), Start.AddMilliseconds(2600));
        Assert.Empty(wide.FinalReport(Start.AddMilliseconds(3000), 3000, false).Gaps);
    }

    [Fact]
    public void ReasonsTheSourceGivesDuringASilenceReplaceSourceNotRefreshed()
    {
        var plan = SampledPlan();
        plan.Accept(Value(1, 1000), Start.AddMilliseconds(1000));
        plan.Accept(TargetedRecordingItemReading.Missing("gpu-dedicated-memory-metrics-unsupported"), Start.AddMilliseconds(2500));
        plan.Accept(TargetedRecordingItemReading.Missing("gpu-dedicated-memory-metrics-unsupported"), Start.AddMilliseconds(3500));
        plan.Accept(TargetedRecordingItemReading.Missing(TargetedRecordingReadReasons.MetricWarming), Start.AddMilliseconds(4000));
        plan.Accept(Value(2, 5000), Start.AddMilliseconds(5000));
        var report = plan.FinalReport(Start.AddMilliseconds(5200), 5200, false);
        Assert.Equal(
            new[]
            {
                new TargetedRecordingGap(2000, 4000, "gpu-dedicated-memory-metrics-unsupported"),
                new TargetedRecordingGap(4000, 5000, TargetedRecordingReadReasons.MetricWarming)
            }, report.Gaps);
    }

    [Fact]
    public void ASourceThatNeverPublishesLeavesOneGapOverTheWholeSpanWithItsReason()
    {
        var plan = SampledPlan();
        plan.Accept(TargetedRecordingItemReading.Missing(TargetedRecordingReadReasons.MetricUnsupported), Start.AddMilliseconds(100));
        var report = plan.FinalReport(Start.AddMilliseconds(4000), 4000, false);
        Assert.Equal(new[] { new TargetedRecordingGap(0, 4000, TargetedRecordingReadReasons.MetricUnsupported) }, report.Gaps);
        Assert.Equal(0, report.PointCount);
    }

    [Theory]
    [InlineData(TargetedRecordingGapReasons.NotApplicable)]
    [InlineData(TargetedRecordingGapReasons.NoSource)]
    public void ItemsNotApplicableOrWithoutSourceAreOneWholeSpanGapAndAreNotSubscribed(string reason)
    {
        var plan = new TargetedRecordingService.ItemPlan(new(TargetedRecordingItemKinds.Metric, "gpu.0.vram"),
            MetricEntry with { Id = "gpu.0.vram", Sources = [], UnavailableReason = reason }, "B", 1000,
            TargetedRecordingIntervalOrigins.ProfileDefault);
        Assert.False(plan.IsSampled);
        Assert.Equal(new[] { new TargetedRecordingGap(0, 9000, reason) }, plan.FinalReport(Start.AddSeconds(9), 9000, false).Gaps);
        var missing = new TargetedRecordingService.ItemPlan(new(TargetedRecordingItemKinds.Metric, "gpu.7.usage"), null, "", 1000,
            TargetedRecordingIntervalOrigins.ProfileDefault);
        Assert.False(missing.IsSampled);
        Assert.Equal(TargetedRecordingGapReasons.ItemUnavailable, Assert.Single(missing.FinalReport(Start, 10, false).Gaps).Reason);
    }

    [Fact]
    public void MetricPublicationsGiveTheValueAtTheDatasetObservationOrTheConcreteReason()
    {
        var observed = Start.AddMilliseconds(1234);
        var current = Publication(Start.AddSeconds(2), "cpu.usage", 37.5, SamplingObservationStatus.Current, observed, null);
        var reading = TargetedRecordingItemReader.ReadMetric(current, "cpu.usage");
        Assert.Equal(37.5, reading.Value);
        Assert.Equal(observed, reading.ObservedAt);
        Assert.Equal("load@cpu0", reading.ActualSource);

        Assert.Equal("adlx-call-failed", TargetedRecordingItemReader.ReadMetric(
            Publication(Start, "cpu.usage", 37.5, SamplingObservationStatus.Current, observed, "adlx-call-failed"), "cpu.usage").Reason);
        Assert.Equal(TargetedRecordingReadReasons.MetricUnsupported, TargetedRecordingItemReader.ReadMetric(
            Publication(Start, "cpu.usage", null, SamplingObservationStatus.Unsupported, null, null), "cpu.usage").Reason);
        Assert.Equal(TargetedRecordingReadReasons.MetricWarming, TargetedRecordingItemReader.ReadMetric(
            Publication(Start, "cpu.usage", null, SamplingObservationStatus.Warming, null, null), "cpu.usage").Reason);
        Assert.Equal(TargetedRecordingReadReasons.MetricUnavailable, TargetedRecordingItemReader.ReadMetric(
            Publication(Start, "cpu.usage", 3, SamplingObservationStatus.Unavailable, observed, null), "cpu.usage").Reason);
        Assert.Equal(TargetedRecordingReadReasons.MetricUnknown, TargetedRecordingItemReader.ReadMetric(
            Publication(Start, "cpu.usage", 3, SamplingObservationStatus.Current, observed, null), "gpu.3.usage").Reason);
    }

    [Fact]
    public void SoftwareReadsPassTheBreakdownReasonThroughAndTotalsListContributingGpus()
    {
        IReadOnlySet<string> target = new HashSet<string> { "game" };
        var unsupported = new ResourceBreakdownBar("gpu.0.vram", "B", "capacity", null, null, null, [],
            SamplingObservationStatus.Unsupported) { UnavailableReason = "gpu-dedicated-memory-metrics-unsupported" };
        var snapshot = new ResourceBreakdownSnapshot(Start, [unsupported, GpuBar(0, 0), GpuBar(1, 50)]);
        static bool Any(int pid, long key) => true;

        Assert.Equal("gpu-dedicated-memory-metrics-unsupported", TargetedRecordingItemReader.ReadSoftware(snapshot,
            new(TargetedRecordingItemKinds.Software, "gpu.0.vram"), target, Any).Reason);
        Assert.Equal("gpu-dedicated-memory-metrics-unsupported", TargetedRecordingItemReader.ReadSoftware(snapshot,
            new(TargetedRecordingItemKinds.Software, TargetedRecordingItemCatalogRules.GpuVramTotalId), target, Any).Reason);
        var total = TargetedRecordingItemReader.ReadSoftware(snapshot,
            new(TargetedRecordingItemKinds.Software, TargetedRecordingItemCatalogRules.GpuUsageTotalId), target, Any);
        Assert.Equal(50, total.Value);
        Assert.Equal(new[] { "resource-breakdown:gpu.1.usage" }, total.ContributingSources);
        Assert.Equal(TargetedRecordingReadReasons.BreakdownBarMissing, TargetedRecordingItemReader.ReadSoftware(snapshot,
            new(TargetedRecordingItemKinds.Software, "disk.read"), target, Any).Reason);
        Assert.Equal(TargetedRecordingReadReasons.BreakdownWarming, TargetedRecordingItemReader.ReadSoftware(null,
            new(TargetedRecordingItemKinds.Software, "cpu.usage"), target, Any).Reason);
        var failed = snapshot with { Sampling = new ResourceBreakdownSamplingState(ResourceBreakdownSamplingStatuses.Failed, 1, Start, null, "etw-session-lost", null) };
        Assert.Equal("etw-session-lost", TargetedRecordingItemReader.ReadSoftware(failed,
            new(TargetedRecordingItemKinds.Software, "cpu.usage"), target, Any).Reason);
    }

    [Fact]
    public void TheTargetGpuIsTheOneCarryingMostOfTheTargetsUsage()
    {
        IReadOnlySet<string> target = new HashSet<string> { "game" };
        Assert.Equal(1, TargetedRecordingTargets.TargetGpu(new(Start, [GpuBar(0, 5), GpuBar(1, 40)]), target));
        Assert.Equal(0, TargetedRecordingTargets.TargetGpu(new(Start, [GpuBar(0, 30), GpuBar(1, 2)]), target));
        // Ties go to the lower index; no share at all is unresolved.
        Assert.Equal(0, TargetedRecordingTargets.TargetGpu(new(Start, [GpuBar(1, 20), GpuBar(0, 20)]), target));
        Assert.Null(TargetedRecordingTargets.TargetGpu(new(Start, [GpuBar(0, 0), GpuBar(1, 0)]), target));
        Assert.Null(TargetedRecordingTargets.TargetGpu(null, target));
    }

    [Fact]
    public void TheCatalogCarriesNotApplicableAndNoSourceAndTheTargetGpuItems()
    {
        var catalog = TargetedRecordingItemCatalogRules.Build(
        [
            Metric("gpu.0.usage", "%", ["busy@igpu"]),
            Metric("gpu.1.usage", "%", ["busy@dgpu"]),
            Metric("gpu.0.vram", "B", [], notApplicable: true),
            Metric("gpu.1.vram", "B", ["fb@dgpu"]),
            Metric("gpu.0.temperature", "°C", ["temp@igpu"]),
            Metric("gpu.1.current", "A", [])
        ], []);
        string? Reason(string id) => TargetedRecordingItemCatalogRules.Find(catalog, TargetedRecordingItemKinds.Metric, id)!.UnavailableReason;
        Assert.Equal(TargetedRecordingGapReasons.NotApplicable, Reason("gpu.0.vram"));
        Assert.Equal(TargetedRecordingGapReasons.NoSource, Reason("gpu.1.current"));
        Assert.Null(Reason("gpu.1.vram"));
        foreach (var quantity in TargetedRecordingItemCatalogRules.TargetGpuQuantities)
        {
            var entry = TargetedRecordingItemCatalogRules.Find(catalog, TargetedRecordingItemKinds.Metric, "gpu.target." + quantity)!;
            Assert.Empty(entry.Sources);
            Assert.Equal(TargetedRecordingItemUses.Both, entry.Uses);
            Assert.Null(entry.UnavailableReason);
        }
        Assert.Equal("B", TargetedRecordingItemCatalogRules.Find(catalog, TargetedRecordingItemKinds.Metric, "gpu.target.vram")!.Unit);
        Assert.Equal("targeted-recording-item-source-invalid", TargetedRecordingItemCatalogRules.Validate(
            new(TargetedRecordingItemKinds.Metric, "gpu.target.usage", "busy@dgpu"), catalog));

        // Every GPU's vram not applicable: the target item is not applicable too.
        var integratedOnly = TargetedRecordingItemCatalogRules.Build(
            [Metric("gpu.0.usage", "%", ["busy@igpu"]), Metric("gpu.0.vram", "B", [], notApplicable: true)], []);
        Assert.Equal(TargetedRecordingGapReasons.NotApplicable, TargetedRecordingItemCatalogRules.Find(integratedOnly,
            TargetedRecordingItemKinds.Metric, "gpu.target.vram")!.UnavailableReason);
        Assert.Equal(TargetedRecordingGapReasons.NoSource, TargetedRecordingItemCatalogRules.Find(integratedOnly,
            TargetedRecordingItemKinds.Metric, "gpu.target.temperature")!.UnavailableReason);
        // No per-GPU usage bars: the target GPU cannot be resolved, so the items are not offered.
        Assert.Null(TargetedRecordingItemCatalogRules.Find(TargetedRecordingItemCatalogRules.Build([], []),
            TargetedRecordingItemKinds.Metric, "gpu.target.usage"));
    }

    [Fact]
    public void TheDefaultProfileRecordsTheTargetsGpuTotalsAndTheGpuItUsesMost()
    {
        var profile = TargetedRecordingProfileDefaults.CreateDefaultProfile();
        Assert.Equal(TargetedRecordingProfileDefaults.DefaultProfileId, profile.Id);
        Assert.Equal("默认", profile.Name);
        Assert.Equal(1000, profile.DefaultIntervalMs);
        Assert.Equal(new[]
        {
            "frames:frames.present", "software:cpu.usage", "software:memory.usage", "software:virtualMemory.usage",
            "software:disk.read", "software:disk.write", "software:network.receive", "software:network.send",
            "software:gpu.usage", "software:gpu.vram", "metric:cpu.usage", "metric:cpu.temperature", "metric:cpu.frequency",
            "metric:memory.usage", "metric:gpu.target.usage", "metric:gpu.target.temperature", "metric:gpu.target.vram"
        }, profile.Items.Select(static item => $"{item.Kind}:{item.Id}"));
        Assert.All(profile.Items, static item =>
        {
            Assert.Equal(TargetedRecordingProfileDefaults.AutoSource, item.Source);
            Assert.Null(item.IntervalMs);
            Assert.Equal(TargetedRecordingSyncPrecisions.Mid, item.SyncPrecision);
        });
        Assert.DoesNotContain(profile.Items, static item => item.Id.StartsWith("gpu.0.", StringComparison.Ordinal));
    }

    [Fact]
    public void MigrationReplacesOnlyAProfileExactlyEqualToTheOldDefault()
    {
        var old = OldDefault();
        Assert.True(TargetedRecordingDefaultMigration.IsOldDefault(old));
        var migrated = TargetedRecordingDefaultMigration.Apply(new([old, old with { Id = "mine", Name = "Mine" }]));
        Assert.Equal(TargetedRecordingProfileDefaults.CreateDefaultProfile().Items, migrated.Profiles[0].Items);
        Assert.Equal("mine", migrated.Profiles[1].Id);
        Assert.Equal(old.Items, migrated.Profiles[1].Items);

        // Any difference keeps the stored profile.
        foreach (var changed in new[]
        {
            old with { Name = "My default" },
            old with { DefaultIntervalMs = 500 },
            old with { Items = old.Items.Take(16).ToArray() },
            old with { Items = old.Items.Select((item, index) => index == 3 ? item with { IntervalMs = 2000 } : item).ToArray() },
            old with { Items = old.Items.Select((item, index) => index == 10 ? item with { SyncPrecision = "high" } : item).ToArray() },
            old with { Items = old.Items.Reverse().ToArray() }
        })
        {
            var settings = new AppTargetedRecordingSettings([changed]);
            Assert.Same(settings, TargetedRecordingDefaultMigration.Apply(settings));
        }

        // A settings file holding the old default is rewritten once; after the rewrite it holds the new default.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var stored = JsonDocument.Parse(JsonSerializer.Serialize(new { targetedRecording = new AppTargetedRecordingSettings([old]) }, options));
        Assert.True(AppSettingsMigrator.RequiresRewrite(stored.RootElement));
    }

    [Fact]
    public void RecordingDefaultMigrationPreservesCrashDumpSettingsInTheSameFile()
    {
        var defaults = AppSettingsDefaults.Create();
        var crashDumps = new AppCrashDumpSettings(AppCrashDumpModes.Off, AppCrashDumpTypes.Full, 9);
        var stored = defaults with
        {
            SystemIntegration = defaults.SystemIntegration with { CrashDumps = crashDumps },
            TargetedRecording = new([OldDefault()])
        };
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(stored, JsonSerializerOptions.Web));
        Assert.True(AppSettingsMigrator.RequiresRewrite(input.RootElement));
        var migrated = AppSettingsMigrator.MigrateForRead(input.RootElement);
        Assert.Equal(crashDumps, migrated.SystemIntegration.CrashDumps);
        Assert.Equal(TargetedRecordingProfileDefaults.CreateDefaultProfile().Items,
            Assert.Single(migrated.TargetedRecording!.Profiles).Items);
        using var rewritten = JsonDocument.Parse(JsonSerializer.Serialize(migrated, JsonSerializerOptions.Web));
        Assert.False(AppSettingsMigrator.RequiresRewrite(rewritten.RootElement));
    }

    [Fact]
    public void OverlayItemsAnswerTheReadersConcreteReasons()
    {
        var notApplicable = new ResourceManagerEndpointRouteBuilderExtensions.PerformanceOverlayItemPlan(
            new(TargetedRecordingItemKinds.Metric, "gpu.0.vram"),
            MetricEntry with { Id = "gpu.0.vram", Sources = [], UnavailableReason = TargetedRecordingGapReasons.NotApplicable },
            1000, TargetedRecordingIntervalOrigins.ProfileDefault);
        var failing = new ResourceManagerEndpointRouteBuilderExtensions.PerformanceOverlayItemPlan(
            new(TargetedRecordingItemKinds.Metric, "cpu.usage"), MetricEntry, 1000, TargetedRecordingIntervalOrigins.ProfileDefault);
        var metrics = new PublicationMetrics(Publication(Start, "cpu.usage", null, SamplingObservationStatus.Unavailable, null, "pdh-counter-missing"));
        var values = ResourceManagerEndpointRouteBuilderExtensions.UpdatePerformanceOverlayItems([notApplicable, failing], "overlay",
            ["game"], [new SchedulingProcessFact(10, 100, "Game", null, "game", "Game", "game", "game", 0,
                SchedulingProcessMetricMask.None, 0, 0, 1, [])], metrics, new EmptyBreakdown(), new NoFrames(),
            TimeSpan.FromSeconds(1), Start, TimeSpan.FromMilliseconds(50), out _);
        ResourceManagerEndpointRouteBuilderExtensions.ReleasePerformanceOverlayItems([notApplicable, failing]);
        Assert.Equal(TargetedRecordingGapReasons.NotApplicable, values[0].Reason);
        Assert.Null(notApplicable.Lease);
        Assert.Equal(1, metrics.Acquired);
        Assert.Equal("pdh-counter-missing", values[1].Reason);
        Assert.Equal("unavailable", values[1].State);
    }

    internal static TargetedRecordingProfile OldDefault() => new(TargetedRecordingProfileDefaults.DefaultProfileId, "默认", 1000,
    [
        new(TargetedRecordingItemKinds.Frames, "frames.present"),
        new(TargetedRecordingItemKinds.Software, "cpu.usage"),
        new(TargetedRecordingItemKinds.Software, "memory.usage"),
        new(TargetedRecordingItemKinds.Software, "virtualMemory.usage"),
        new(TargetedRecordingItemKinds.Software, "disk.read"),
        new(TargetedRecordingItemKinds.Software, "disk.write"),
        new(TargetedRecordingItemKinds.Software, "network.receive"),
        new(TargetedRecordingItemKinds.Software, "network.send"),
        new(TargetedRecordingItemKinds.Software, "gpu.0.usage"),
        new(TargetedRecordingItemKinds.Software, "gpu.0.vram"),
        new(TargetedRecordingItemKinds.Metric, "cpu.usage"),
        new(TargetedRecordingItemKinds.Metric, "cpu.temperature"),
        new(TargetedRecordingItemKinds.Metric, "cpu.frequency"),
        new(TargetedRecordingItemKinds.Metric, "memory.usage"),
        new(TargetedRecordingItemKinds.Metric, "gpu.0.usage"),
        new(TargetedRecordingItemKinds.Metric, "gpu.0.temperature"),
        new(TargetedRecordingItemKinds.Metric, "gpu.0.vram")
    ]);

    internal static ResourceBreakdownBar GpuBar(int gpu, double targetShare, string softwareId = "game")
        => new($"gpu.{gpu}.usage", "%", "capacity", 100, 100, 100,
            [new ResourceSoftwareSegment(softwareId, softwareId, "game", "game", targetShare, targetShare, 1, [])]);

    private static MetricDefinition Metric(string id, string unit, string[] sources, bool notApplicable = false)
        => new(id, unit, ResourceManager.ReadWrite.ReadVolatility.Sampled, Selectable: !notApplicable,
            DisabledReason: notApplicable ? BackendMessage.Create(BackendMessageDomains.Metric, BackendMessageCodes.Metric.NotApplicable) : null)
        {
            CandidateSources = sources.Select(static source => source.Split('@'))
                .Select(static parts => new MetricSourceCandidate(parts[0], parts[1], parts[0], null, "available", true)).ToArray()
        };

    /// <summary>A delivered publication holding one item: value, dataset status, observation time and failure code.</summary>
    internal static HardwareMetricSnapshot Publication(DateTimeOffset capturedAt, string id, double? value,
        SamplingObservationStatus status, DateTimeOffset? observedAt, string? failureCode, string device = "cpu0")
    {
        var gpuInventory = new SchedulingGpuInventorySnapshot(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []);
        var item = new MetricReadingValue(id, value, "%") { FieldName = "load", DeviceIdentity = device, Status = status };
        return new HardwareMetricSnapshot(capturedAt,
            new CpuMetrics("CPU", 0, false, CpuMetricObservationStatus.Unavailable, 0, 0, 0, 0, 0, "fixture",
                new CpuSensorMetrics(new HardwareSensorProviderState("fixture", HardwareSensorProviderStates.Unavailable, null),
                    null, null, null, null)),
            new MemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsUsageAvailable: false),
            new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false),
            [], gpuInventory, new Dictionary<string, MetricReadingValue>(StringComparer.OrdinalIgnoreCase) { [id] = item })
        {
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(StringComparer.OrdinalIgnoreCase)
            {
                [id] = new(id, status, 1, observedAt?.UtcTicks ?? 0, 0, 0, 0, 0) { FailureCode = failureCode }
            }
        };
    }

    /// <summary>Overlay metric source: ReadItems sees the publication through the default projection.</summary>
    private sealed class PublicationMetrics(HardwareMetricSnapshot publication) : ResourceManager.App.Application.Metrics.IMetricSnapshotObservationSource
    {
        public int Acquired;
        public IDisposable AcquireSubscription(string subscriptionId, MetricSampleRequest request, TimeSpan refreshInterval)
        {
            Acquired++;
            return new Release();
        }
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => publication;
    }

    private sealed class EmptyBreakdown : ResourceManager.App.Application.ResourceBreakdown.IResourceBreakdownObservationSource
    {
        public IDisposable AcquireSubscription(string subscriptionId, ResourceBreakdownSampleRequest request, TimeSpan refreshInterval)
            => new Release();
        public ResourceBreakdownSnapshot ReadLatest(ResourceBreakdownSampleRequest request) => new(Start, []);
    }

    private sealed class NoFrames : ResourceManager.ReadWrite.Windows.Frames.IFrameTimingObservationSource
    {
        public TimeSpan Retention => ResourceManager.App.Infrastructure.Monitoring.MonitoringSettings.ProductDefaults.FramePipeline.Retention;
        public ResourceManager.ReadWrite.Windows.Frames.IFrameSubscriptionLease AcquireSubscription(
            ResourceManager.ReadWrite.Windows.Frames.FrameSubscriptionRequest request) => new FrameLeaseAdapter(AcquireSubscription());
        public IDisposable AcquireSubscription() => new Release();
        public ResourceManager.ReadWrite.Windows.Frames.FrameTimingSnapshot? Read(TimeSpan window) => null;
        public ResourceManager.ReadWrite.Windows.Frames.FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through) => null;
    }

    private sealed class Release : IDisposable
    {
        public void Dispose() { }
    }
}
