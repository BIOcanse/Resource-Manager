using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace Resource_Manager_APP.Tests;

public sealed class NpuEngineOwnerIntegrationTests
{
    [Fact]
    public async Task RepeatedProbeKeepsOneRealDirectoryAndCurrentWhileReturningTwoFieldsPerEngine()
    {
        var recording = new NpuEngineRecordedResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = Core(owner, http);
        Assert.Empty(core.Catalog.Fields); Assert.Equal(0, recording.Opens);
        var fields = Fields(await core.DiscoverAsync());
        Assert.Equal(4, fields.Length); Assert.Equal(2, fields.Select(static field => field.DeviceIdentity).Distinct().Count());
        Assert.Equal(2, core.Catalog.Devices.Count(static device => device.Resource == "npu_engine"));
        Assert.Equal(1, recording.Opens); Assert.Equal(0, recording.Reads); Assert.Null(owner.ReadCachedSnapshot());
        await core.CollectAsync(fields);
        var current = owner.ReadCachedSnapshot()!;
        Assert.Equal<FieldReference>(fields, Fields(await core.DiscoverAsync()));
        Assert.Equal<FieldReference>(fields, Fields(await core.DiscoverAsync()));
        Assert.Equal(1, recording.Opens); Assert.Equal(1, recording.Reads);
        Assert.Equivalent(current, owner.ReadCachedSnapshot(), strict: true);
        foreach (var field in fields) Assert.False(core.Query(field).Write.Supported);
        owner.Dispose(); Assert.True(Assert.Single(recording.Opened).Disposed);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(9007199254740993UL)]
    [InlineData(18446744073709551615UL)]
    public async Task RawUnsignedWidthAndFirstRatioUnknownSurviveTheOriginalOwner(ulong counter)
    {
        var recording = new NpuEngineRecordedResources { Counter = counter, CounterStep = 0 };
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = Core(owner, http);
        var fields = Fields(await core.DiscoverAsync()); var first = await core.CollectAsync(fields);
        Assert.All(first.Where(static row => row.Field.FieldName == NpuEngineRecordedResources.Raw), row =>
        { Assert.Equal(MetricInteger.FromUnsigned128(counter), row.Value!.Integer); Assert.Null(row.Value.Number); });
        Assert.All(first.Where(static row => row.Field.FieldName == NpuEngineRecordedResources.Ratio), static row => Assert.Null(row.Value));
        var second = await core.CollectAsync(fields);
        Assert.All(second.Where(static row => row.Field.FieldName == NpuEngineRecordedResources.Ratio), static row => Assert.Equal(0d, row.Value!.Number));
        Assert.Equal(2, recording.Reads); Assert.Equal(256U, recording.LastMaximum);
        Assert.Equal(0d, Assert.Single(await core.CollectAsync(BusyFields(core.Catalog))).Value!.Number);
    }

    [Fact]
    public async Task NativeReportedRatioCanExceed100AndUsesOneSourceReadForBothFields()
    {
        var recording = new NpuEngineRecordedResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = Core(owner, http);
        var all = Fields(await core.DiscoverAsync());
        var fields = all.Where(field => field.DeviceIdentity == all[0].DeviceIdentity).ToImmutableArray();
        await core.CollectAsync(fields); var rows = await core.CollectAsync(fields);
        Assert.Equal(125d, rows.Single(static row => row.Field.FieldName == NpuEngineRecordedResources.Ratio).Value!.Number);
        Assert.Equal(2, recording.Reads); Assert.Single(recording.LastRequested);
        Assert.All(rows, row => Assert.Equal(row, core.ReadCached(row.Field)));
        Assert.Equal(0, recording.UnselectedReads);
    }

    [Fact]
    public async Task DeviceBusyUsesMaximumOverCompleteEngineSetAndPreservesValuesAbove100()
    {
        var recording = new NpuEngineRecordedResources();
        recording.EngineCounterSteps[0] = 500_000;
        recording.EngineCounterSteps[1] = 1_500_000;
        recording.EngineTickOffsets[1] = 500_000;
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = Core(owner, http);
        var busy = BusyFields(await core.DiscoverAsync());
        var field = Assert.Single(busy);
        var capability = core.Query(field);
        Assert.True(capability.Read.Supported); Assert.False(capability.Write.Supported);
        Assert.Empty(capability.Read.Dependencies); Assert.Equal(NativeMetricSnapshotSources.WindowsNpuEngine, capability.ReadSource);
        Assert.Equal("%", capability.Definition.Unit); Assert.Equal("float64", capability.Definition.ValueKind);

        Assert.Null(Assert.Single(await core.CollectAsync(busy)).Value);
        Assert.Equal(2, recording.LastRequested.Count);
        Assert.Equal(150d, Assert.Single(await core.CollectAsync(busy)).Value!.Number);
        Assert.Equal(2, recording.LastRequested.Count);

        recording.MissingEngine = 1;
        Assert.Null(Assert.Single(await core.CollectAsync(busy)).Value);
        recording.MissingEngine = null;
        Assert.Null(Assert.Single(await core.CollectAsync(busy)).Value);
        Assert.Equal(150d, Assert.Single(await core.CollectAsync(busy)).Value!.Number);
    }

    [Fact]
    public async Task DeviceBusyDoesNotPublishWhenQpcStallsOrCounterResetsAndWarmsAgain()
    {
        var recording = new NpuEngineRecordedResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = Core(owner, http);
        var busy = BusyFields(await core.DiscoverAsync());
        Assert.Null(Assert.Single(await core.CollectAsync(busy)).Value);
        Assert.Equal(125d, Assert.Single(await core.CollectAsync(busy)).Value!.Number);

        recording.TickStep = 0;
        // Read uses current Ticks before applying TickStep: this sample is 3,000,000
        // and the next is also 3,000,000 (the actual stall). After restoring the step,
        // the following sample is 4,000,000 and has a valid one-second interval.
        Assert.Equal(125d, Assert.Single(await core.CollectAsync(busy)).Value!.Number);
        recording.TickStep = 1_000_000;
        Assert.Null(Assert.Single(await core.CollectAsync(busy)).Value);
        Assert.Equal(125d, Assert.Single(await core.CollectAsync(busy)).Value!.Number);

        recording.ResetCounters(1);
        Assert.Null(Assert.Single(await core.CollectAsync(busy)).Value);
        Assert.Equal(125d, Assert.Single(await core.CollectAsync(busy)).Value!.Number);
    }

    [Fact]
    public async Task DeviceBusyKeepsCurrentAndBaselineOnCancellationAndReopensUnknownAfterDirectoryChange()
    {
        var recording = new NpuEngineRecordedResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = Core(owner, http);
        var busy = BusyFields(await core.DiscoverAsync());
        Assert.Null(Assert.Single(await core.CollectAsync(busy)).Value);
        Assert.Equal(125d, Assert.Single(await core.CollectAsync(busy)).Value!.Number);

        var current = owner.ReadCachedSnapshot();
        using (var canceled = new CancellationTokenSource())
        {
            recording.AfterRead = canceled.Cancel;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await core.CollectAsync(busy, canceled.Token));
            recording.AfterRead = null;
            Assert.Equivalent(current, owner.ReadCachedSnapshot(), strict: true);
        }
        Assert.Equal(125d, Assert.Single(await core.CollectAsync(busy)).Value!.Number);

        recording.Opened[0].Current = false;
        var replacement = BusyFields(await core.DiscoverAsync());
        Assert.Equal<FieldReference>(busy, replacement);
        Assert.Null(Assert.Single(await core.CollectAsync(replacement)).Value);
        Assert.Equal(125d, Assert.Single(await core.CollectAsync(replacement)).Value!.Number);
    }

    [Fact]
    public async Task FailureAndCounterOrFrequencyResetWarmTheOriginalNativeBaselineAgain()
    {
        var recording = new NpuEngineRecordedResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = Core(owner, http);
        var fields = Fields(await core.DiscoverAsync());
        await core.CollectAsync(fields); AssertRatios(await core.CollectAsync(fields), 125);
        recording.Fail = true; Assert.All(await core.CollectAsync(fields), static row => Assert.Null(row.Value));
        recording.Fail = false; AssertRatios(await core.CollectAsync(fields), null); AssertRatios(await core.CollectAsync(fields), 125);
        recording.ResetCounters(1); AssertRatios(await core.CollectAsync(fields), null); AssertRatios(await core.CollectAsync(fields), 125);
        recording.Frequency = 2_000_000; AssertRatios(await core.CollectAsync(fields), null); AssertRatios(await core.CollectAsync(fields), 250);
    }

    [Fact]
    public async Task DirectoryUnavailablePreservesDefinitionsButClearsValuesAndReopenStartsUnknown()
    {
        var recording = new NpuEngineRecordedResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = Core(owner, http);
        var fields = Fields(await core.DiscoverAsync()); await core.CollectAsync(fields); AssertRatios(await core.CollectAsync(fields), 125);
        recording.Unavailable = true; await core.DiscoverAsync();
        var reads = recording.Reads;
        Assert.All(await core.CollectAsync(fields), static row => Assert.Null(row.Value)); Assert.Equal(reads, recording.Reads);
        Assert.Equal<FieldReference>(fields, Fields(core.Catalog));
        recording.Unavailable = false; recording.Opened[0].Current = false;
        var reopened = Fields(await core.DiscoverAsync());
        Assert.False(fields.Select(static field => field.DeviceIdentity).Intersect(reopened.Select(static field => field.DeviceIdentity)).Any());
        AssertRatios(await core.CollectAsync(reopened), null); AssertRatios(await core.CollectAsync(reopened), 125);
        Assert.True(recording.Opened[0].Disposed);
    }

    [Fact]
    public async Task ObservableInvalidationEmptiesNpuWhileOtherSourcesCommitAndCancellationKeepsCurrent()
    {
        var recording = new NpuEngineRecordedResources();
        using var owner = new WindowsHardwareMetricOwner(WindowsMetricConfiguration.Default, recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = Core(owner, http);
        var fields = Fields(await core.DiscoverAsync()); await core.CollectAsync(fields); await core.CollectAsync(fields);
        var before = owner.ReadCachedSnapshot()!;
        recording.InvalidateDuringRead = true;
        recording.ReadMemory = () => new(128, 256, 50, true);
        var request = MetricSampleRequest.ForIds([
            "memory.usage",
            .. before.FieldBindings.Values.Where(binding => fields.Any(field =>
                field.DeviceIdentity == binding.DeviceIdentity && field.FieldName == binding.Field.Name))
                .Select(static binding => binding.RequestAddress)]);
        var invalidated = await owner.CaptureAsync(request);
        Assert.NotSame(before, owner.ReadCachedSnapshot());
        var npuValues = invalidated.Items.Values.Where(static value => value.FieldName is
            NpuEngineRecordedResources.Raw or NpuEngineRecordedResources.Ratio).ToArray();
        Assert.Empty(npuValues);
        Assert.All(fields, field =>
        {
            var binding = before.FieldBindings.Values.Single(binding =>
                binding.DeviceIdentity == field.DeviceIdentity && binding.Field.Name == field.FieldName);
            Assert.Equal(SamplingObservationStatus.Unavailable, invalidated.Datasets[binding.RequestAddress].Status);
            var reading = core.ReadCached(field);
            Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
            Assert.Null(reading.Value);
        });
        Assert.Equal(128UL, invalidated.Memory.UsedBytes);
        Assert.Equal(SamplingObservationStatus.Current,
            invalidated.Datasets["memory.usage"].Status);
        recording.InvalidateDuringRead = false;
        fields = Fields(await core.DiscoverAsync()); AssertRatios(await core.CollectAsync(fields), null);
        var current = owner.ReadCachedSnapshot();
        using var canceled = new CancellationTokenSource(); recording.AfterRead = canceled.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await core.CollectAsync(fields, canceled.Token));
        Assert.Equivalent(current, owner.ReadCachedSnapshot(), strict: true); recording.AfterRead = null;
        // Accepted→next accepted spans2 seconds and4.5mµs =>225. An incorrectly
        // advanced canceled baseline would instead span1 second and3.25mµs =>325.
        recording.AdvanceCounters(2_000_000);
        AssertRatios(await core.CollectAsync(fields), 225);
        owner.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await core.CollectAsync(fields));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void GlobalDirectoryBudgetIsAllOrNoneAndObservedEmptyLeaseCanBeRetained(int count, bool complete)
    {
        var recording = new NpuEngineRecordedResources { Count = count };
        var read = recording.Open(256, null);
        try
        {
            Assert.Equal(complete ? NativeMetricSnapshotCatalogTopologyStatus.Complete : NativeMetricSnapshotCatalogTopologyStatus.Unavailable, read.Status);
            Assert.Equal(complete ? count : 0, read.Identities.Count);
            if (!complete) return;
            var prior = read.Directory!; var repeated = recording.Open(256, prior);
            try { Assert.Same(prior, repeated.Directory); Assert.Equal(1, recording.Opens); }
            finally { repeated.InitialReference?.Dispose(); }
        }
        finally { read.InitialReference?.Dispose(); }
    }

    [Fact]
    public void EveryApplicableTemplateParticipatesAndNewDirectoryChangesEachNativeRuleFingerprint()
    {
        var config = WindowsMetricConfiguration.Default.CatalogConfiguration;
        var engine = config.RuleTemplates.First(static template => template.SourceId == NativeMetricSnapshotSources.WindowsNpuEngine && template.ScopeExpansion == 17);
        var busyTemplate = Assert.Single(config.RuleTemplates.Where(static template => template.MetricIdTemplate == "npu.{npu}.engineBusy"));
        Assert.Equal(16U, busyTemplate.ScopeExpansion); Assert.Equal(64U, busyTemplate.MaximumInstanceCount);
        Assert.Equal(256U, WindowsNpuEngineTopology.EffectiveMaximumInstanceCount(config.RuleTemplates));
        Assert.Equal(1U, WindowsNpuEngineTopology.EffectiveMaximumInstanceCount(config.RuleTemplates.Add(engine with { TemplateId = engine.TemplateId + ".narrow", MaximumInstanceCount = 1 })));
        Assert.Equal(0U, WindowsNpuEngineTopology.EffectiveMaximumInstanceCount(config.RuleTemplates.Where(static template => template.SourceId != NativeMetricSnapshotSources.WindowsNpuEngine)));
        using var first = new NpuEngineRecordedResources.Directory(2); using var second = new NpuEngineRecordedResources.Directory(2);
        var a = NativeMetricSnapshotCatalogProjection.Create(config, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty,
            npuEngineIdentities: first.Identities, npuEngineDirectory: first);
        var b = NativeMetricSnapshotCatalogProjection.Create(config, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty,
            npuEngineIdentities: second.Identities, npuEngineDirectory: second);
        var engineRulesA = a.RuleBindings.Values.Where(static rule => rule.NpuEngine is not null).ToArray();
        var engineRulesB = b.RuleBindings.Values.Where(static rule => rule.NpuEngine is not null).ToArray();
        var ar = engineRulesA.Select(rule => a.Rules.Single(native => native.RuleHandle == rule.RuleHandle).SemanticFingerprint).ToArray();
        var br = engineRulesB.Select(rule => b.Rules.Single(native => native.RuleHandle == rule.RuleHandle).SemanticFingerprint).ToArray();
        Assert.Equal<ulong>(engineRulesA.Select(static rule => rule.ScopeHandle), engineRulesB.Select(static rule => rule.ScopeHandle));
        Assert.Equal(4, ar.Length); Assert.True(ar.Zip(br).All(static pair => pair.First != pair.Second));
        using var emptyA = new NpuEngineRecordedResources.Directory(0); using var emptyB = new NpuEngineRecordedResources.Directory(0);
        var ca = NativeMetricSnapshotCatalogProjection.Create(config, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, npuEngineDirectory: emptyA);
        var cb = NativeMetricSnapshotCatalogProjection.Create(config, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, npuEngineDirectory: emptyB);
        Assert.False(WindowsMetricCatalogTopologyPublication.CanRetainCatalog(ca, cb));
    }

    [Fact]
    public void PublicCompleteResultWithoutOperationReferenceIsRejectedWithoutBorrowingForeignOwnership()
    {
        using var directory = new NpuEngineRecordedResources.Directory(1);
        var topology = new NativeMetricSnapshotCatalogTopologySnapshot(1, NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
            NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, []);
        var result = WindowsNpuEngineTopology.AddDirectory(topology, 1,
            (_, _) => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, directory.Identities) { Directory = directory }, null);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, result.NpuEngineStatus);
        Assert.Empty(result.NpuEngineIdentities); Assert.Null(result.NpuEngineDirectoryReference); Assert.Null(result.NpuEngineDirectory);
        Assert.Equal(0, directory.AcquireCalls); Assert.False(directory.Disposed);
        directory.Dispose(); Assert.True(directory.Disposed); // The provider retains and closes its own untransferred initial reference.
    }

    [Fact]
    public async Task NoApplicableEngineTemplateNeverOpensDirectoryOrReadsEngines()
    {
        var recording = new NpuEngineRecordedResources(); var configuration = WindowsMetricConfiguration.Default;
        configuration = configuration with { CatalogConfiguration = configuration.CatalogConfiguration with
        { RuleTemplates = configuration.CatalogConfiguration.RuleTemplates.Where(static template => template.SourceId != NativeMetricSnapshotSources.WindowsNpuEngine).ToImmutableArray() } };
        using var owner = new WindowsHardwareMetricOwner(configuration, recording.Create(), new(), unavailableCooldown: TimeSpan.Zero);
        using var http = new HttpClient(); var core = Core(owner, http);
        Assert.Empty(Fields(await core.DiscoverAsync())); Assert.Equal(0, recording.Opens); Assert.Equal(0, recording.Reads);
    }

    [Theory]
    [InlineData(NativeMetricSnapshotSources.WindowsNpuEngine)]
    [InlineData(NativeMetricSnapshotSources.WindowsNpuMemory)]
    public void BuiltInDxCoreSourcesDoNotPublishUnknownExternalDependencies(string source)
        => Assert.Empty(ResourceManager.ReadWrite.Fields.MetricReadWriteBackend.DependenciesOf(source));

    [Fact]
    public void SameNativeWorkspaceCannotCarryBaselineAcrossRebindingEvenWithIdenticalAllocatedHandlesAndLargerCounter()
    {
        var configuration = WindowsMetricConfiguration.Default;
        using var first = new NpuEngineRecordedResources.Directory(1); using var replacement = new NpuEngineRecordedResources.Directory(1);
        var a = NativeMetricSnapshotCatalogProjection.Create(configuration.CatalogConfiguration, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty,
            npuEngineIdentities: first.Identities, npuEngineDirectory: first);
        var b = NativeMetricSnapshotCatalogProjection.Create(configuration.CatalogConfiguration, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty,
            npuEngineIdentities: replacement.Identities, npuEngineDirectory: replacement);
        var oldRule = a.Rules.Single(rule => a.RuleBindings[rule.RuleHandle].FieldCode == MetricFieldCode.NpuEngineReportedRunningTimeRatioPercent);
        var newRule = b.Rules.Single(rule => b.RuleBindings[rule.RuleHandle].FieldCode == MetricFieldCode.NpuEngineReportedRunningTimeRatioPercent);
        Assert.Equal(oldRule.ScopeHandle, newRule.ScopeHandle); Assert.Equal(oldRule.RuleHandle, newRule.RuleHandle);
        Assert.Equal(oldRule.MetricHandle, newRule.MetricHandle); Assert.NotEqual(oldRule.SemanticFingerprint, newRule.SemanticFingerprint);
        var nativeConfiguration = configuration.Configuration;
        using var session = new NativeMetricSnapshotSession(in nativeConfiguration);
        var start = DateTimeOffset.UtcNow; ulong sequence = 0, operation = 0, planEpoch = 0;
        var catalogGeneration = configuration.CatalogConfiguration.CatalogGenerationBase;
        Replace(a, start);
        Assert.Null(Publish(a, first.Identities[0], 100, 1_000_000, start.AddMilliseconds(100)));
        Assert.Equal(125d, Publish(a, first.Identities[0], 1_250_100, 2_000_000, start.AddMilliseconds(200)));
        ++catalogGeneration;
        Replace(b, start.AddMilliseconds(300));
        Assert.Null(Publish(b, replacement.Identities[0], 20_000_000, 3_000_000, start.AddMilliseconds(400)));
        Assert.Equal(125d, Publish(b, replacement.Identities[0], 21_250_000, 4_000_000, start.AddMilliseconds(500)));

        double? Publish(NativeMetricSnapshotCatalogProjection catalog, ResourceManager.ReadWrite.Windows.Devices.Npu.NpuEngineCatalogIdentity engine,
            ulong counter, ulong ticks, DateTimeOffset at)
        {
            var binding = catalog.RuleBindings.Values.Single(static rule => rule.FieldCode == MetricFieldCode.NpuEngineReportedRunningTimeRatioPercent);
            var modes = catalog.Sources.Select(static source => new NativeMetricSnapshotSourceStateInput
            {
                StructSize = Size<NativeMetricSnapshotSourceStateInput>(), SourceHandle = source.SourceHandle,
                CapabilityGeneration = source.CapabilityMask,
                Availability = (uint)NativeMetricSnapshotSourceAvailability.Available
            }).ToArray();
            var input = new NativeMetricSnapshotPlanInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotPlanInput>(),
                ConfigurationGeneration = nativeConfiguration.Generation, CatalogGeneration = catalogGeneration,
                OperationEpoch = ++operation, PlanEpoch = ++planEpoch, CommandAtMilliseconds = Stamp(at),
                RequestedMetricCount = 1, SourceModeCount = (uint)modes.Length,
                SourcePlanCapacity = session.Capacity.SourcePlanCapacity, MetricPlanCapacity = session.Capacity.MetricPlanCapacity
            };
            NativeMetricSnapshotPlanMetricInput[] selected = [new() { StructSize = Size<NativeMetricSnapshotPlanMetricInput>(), MetricHandle = binding.MetricHandle }];
            var sourcePlans = new NativeMetricSnapshotSourcePlanOutput[session.Capacity.SourcePlanCapacity];
            var metricPlans = new NativeMetricSnapshotMetricPlanOutput[session.Capacity.MetricPlanCapacity];
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.Plan(in input, selected, modes, out var output, sourcePlans, metricPlans));
            var plan = new NativeMetricSnapshotCollectionPlan(output,
                sourcePlans.Take((int)output.SourcePlanCount).ToImmutableArray(),
                metricPlans.Take((int)output.MetricPlanCount).ToImmutableArray(), catalog);
            var source = Assert.Single(plan.Sources);
            var completion = WindowsHardwareMetricCapture.CreateNpuEngineCompletion(plan, source,
                new(source.SourceHandle, 1, ++sequence), at, [new(engine, new(counter, ticks - 1, ticks, 1_000_000))]);
            var requested = plan.Metrics.Where(static metric => (metric.Flags & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) != 0)
                .Select(static metric => new NativeMetricSnapshotRequestedMetricInput
                { StructSize = Size<NativeMetricSnapshotRequestedMetricInput>(), RuleHandle = metric.RuleHandle }).ToArray();
            var header = new NativeMetricSnapshotCompletionHeader
            {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotCompletionHeader>(),
                ConfigurationGeneration = nativeConfiguration.Generation, CatalogGeneration = catalogGeneration,
                OperationEpoch = ++operation, PlanEpoch = output.PlanEpoch, SourceHandle = source.SourceHandle,
                SourceIncarnation = completion.SourceIncarnation, SourceObservationSequence = completion.SourceObservationSequence,
                CapabilityGeneration = source.CapabilityGeneration, PlanTokenFingerprint = source.PlanTokenFingerprint,
                CommandAtMilliseconds = Stamp(at), CapturedAtMilliseconds = Stamp(at),
                RequestedRuleCount = (uint)requested.Length, ObservationCount = (uint)completion.Observations.Length,
                Status = (uint)completion.Status
            };
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.BeginCompletion(in header));
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.SubmitRequested(requested));
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.SubmitObservations(completion.Observations.AsSpan()));
            var finalize = new NativeMetricSnapshotFinalizeInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotFinalizeInput>(),
                ConfigurationGeneration = nativeConfiguration.Generation, CatalogGeneration = catalogGeneration,
                OperationEpoch = operation, PlanEpoch = output.PlanEpoch, SourceHandle = source.SourceHandle, CommandAtMilliseconds = Stamp(at)
            };
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.FinalizeCompletion(in finalize));
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.QueryHeader(out var snapshot));
            var read = new NativeMetricSnapshotReadInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotReadInput>(),
                ConfigurationGeneration = snapshot.ConfigurationGeneration, CatalogGeneration = snapshot.CatalogGeneration,
                CatalogFingerprint = snapshot.CatalogFingerprint, StateRevision = snapshot.StateRevision,
                CommittedGeneration = snapshot.CommittedGeneration
            };
            var values = new NativeMetricSnapshotMetricOutput[snapshot.MetricCount];
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.Read(in read,
                new NativeMetricSnapshotSourceOutput[snapshot.SourceCount], values,
                new NativeMetricSnapshotGpuInventoryOutput[snapshot.GpuAdapterCount], new NativeMetricSnapshotRuleStateOutput[snapshot.RuleCount]));
            var value = Assert.Single(values, value => value.MetricHandle == binding.MetricHandle);
            return value.Status == (uint)NativeMetricSnapshotMetricStatus.Current ? BitConverter.UInt64BitsToDouble(value.ValueBits) : null;
        }

        void Replace(NativeMetricSnapshotCatalogProjection catalog, DateTimeOffset at)
        {
            var sources = catalog.Sources.ToArray(); var rules = catalog.Rules.ToArray();
            var input = new NativeMetricSnapshotCatalogReplaceInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version, StructSize = Size<NativeMetricSnapshotCatalogReplaceInput>(),
                ConfigurationGeneration = nativeConfiguration.Generation, CatalogGeneration = catalogGeneration,
                OperationEpoch = ++operation, CommandAtMilliseconds = Stamp(at), SourceCount = (uint)sources.Length,
                RuleCount = (uint)rules.Length, MetricCount = (uint)rules.Select(static rule => rule.MetricHandle).Distinct().Count(),
                GpuInventorySourceCount = (uint)sources.Count(static source => source.SourceRole == (uint)NativeMetricSnapshotSourceRole.GpuInventory),
                SourceIndexCapacity = session.Capacity.SourceIndexCapacity, MetricIndexCapacity = session.Capacity.MetricIndexCapacity,
                RuleIndexCapacity = session.Capacity.RuleIndexCapacity,
                SemanticFingerprint = NativeMetricSnapshotSession.CalculateCatalogFingerprint(sources, rules)
            };
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.ReplaceCatalog(in input, sources, rules));
        }

        static uint Size<T>() => checked((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        static ulong Stamp(DateTimeOffset at) => checked((ulong)at.ToUnixTimeMilliseconds());
    }

    private static ReadWriteCore Core(WindowsHardwareMetricOwner owner, HttpClient http)
        => new(owner.CreateBackend(), new DependencyActions(http, []));
    internal static ImmutableArray<FieldReference> Fields(ReadWriteCatalog catalog)
        => catalog.Fields.Where(static field => field.Field.FieldName is NpuEngineRecordedResources.Raw or NpuEngineRecordedResources.Ratio)
            .Select(static field => field.Field).OrderBy(static field => field.DeviceIdentity, StringComparer.Ordinal)
            .ThenBy(static field => field.FieldName, StringComparer.Ordinal).ToImmutableArray();
    internal static ImmutableArray<FieldReference> BusyFields(ReadWriteCatalog catalog)
        => catalog.Fields.Where(static field => field.Field.FieldName == NpuEngineRecordedResources.Busy)
            .Select(static field => field.Field).ToImmutableArray();
    private static void AssertRatios(ImmutableArray<FieldReading> readings, double? expected)
        => Assert.All(readings.Where(static row => row.Field.FieldName == NpuEngineRecordedResources.Ratio), row => Assert.Equal(expected, row.Value?.Number));
}
