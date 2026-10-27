using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed partial class AcpiInformationSubscriptionTests
{
    [Fact]
    public async Task PublicAcpiDirectCapturePublishesToOriginalStreamWithoutStartingWorker()
    {
        using var fixture = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables(), AcpiFirmwareTestData.Tables(7),
            null, AcpiFirmwareTestData.Tables(8)));
        var backend = new InformationReadWriteBackend(() => fixture.Provider.ReadState().AcpiInformationFields,
            fixture.Provider.CaptureAcpiInformationAsync, static _ => new(true, null, []));
        await backend.DiscoverAsync(default);
        var field = backend.Catalog.Fields.Single(item => item.Field.FieldName == "firmware_all_acpi_table_revision").Field;
        Assert.Equal(MetricInteger.Parse("6"), backend.ReadCached(field).Value?.Integer);
        Assert.Equal(1, fixture.Acpi.ReadCount);
        var subscriptions = new InformationFieldSubscriptionBackend(backend,
            (id, interval) => fixture.Provider.AcquireInformationSubscription(id, interval, acpi: true), fixture.Provider.SubscribeAcpiAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = subscriptions.SubscribeAsync("public", [field], TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask();
        Assert.Equal(MetricInteger.Parse("6"), Assert.Single(await backend.CollectAsync([field], cancellation.Token)).Value?.Integer);
        Assert.True(await next);
        Assert.Equal(MetricInteger.Parse("6"), Assert.Single(stream.Current).Value?.Integer);
        FieldReadingAssert.CurrentOrNewer(Assert.Single(stream.Current), backend.ReadCached(field));
        next = stream.MoveNextAsync().AsTask();
        Assert.Equal(MetricInteger.Parse("6"), Assert.Single(await backend.CollectAsync([field], cancellation.Token)).Value?.Integer);
        Assert.True(await next); Assert.NotNull(Assert.Single(stream.Current).ObservedAt);
        Assert.Equal(MetricInteger.Parse("6"), Assert.Single(await backend.CollectAsync([field], cancellation.Token)).Value?.Integer);
        Assert.Equal(1, fixture.Acpi.ReadCount);
        Assert.Equal(0, fixture.Topology.ReadCount); Assert.Equal(0, fixture.Store.SaveCount);
    }

    [Fact]
    public async Task PublicTopologyCancellationDuringReadKeepsOriginalCurrentAndAcpi()
    {
        using var fixture = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables()));
        var backend = new InformationReadWriteBackend(() => fixture.Provider.ReadState().InformationFields,
            fixture.Provider.CaptureInformationAsync, static _ => new(true, null, []));
        await backend.DiscoverAsync(default);
        await fixture.Provider.CaptureAcpiInformationAsync(default);
        var previous = fixture.Provider.ReadState().InformationFields;
        var acpi = fixture.Provider.ReadState().AcpiInformationFields;
        var field = backend.Catalog.Fields[0].Field;
        using var cancellation = new CancellationTokenSource();
        fixture.Topology.Capture = () => { cancellation.Cancel(); return DeviceInformationTestData.Snapshot(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.CollectAsync([field], cancellation.Token).AsTask());
        Assert.Same(previous, fixture.Provider.ReadState().InformationFields);
        Assert.Same(acpi, fixture.Provider.ReadState().AcpiInformationFields);
        Assert.Equal(2, fixture.Topology.ReadCount);
        _ = backend.ReadCached(field); Assert.Equal(2, fixture.Topology.ReadCount);
    }

    [Fact]
    public async Task PublicExplicitCapturesWaitForTheOriginalGateAndDoNotReturnAnOlderRead()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var count = 0;
        using var fixture = new Fixture(new AcpiReader(() => {
            var capture = Interlocked.Increment(ref count);
            if (capture == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
            return AcpiFirmwareTestData.Tables((byte)(capture + 5));
        }));
        var first = Task.Run(async () => await fixture.Provider.CaptureAcpiInformationAsync(default));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var second = fixture.Provider.CaptureAcpiInformationAsync(default).AsTask();
            Assert.False(second.IsCompleted);
            using var canceled = new CancellationTokenSource();
            var third = fixture.Provider.CaptureAcpiInformationAsync(canceled.Token).AsTask(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
            release.Set(); await first; var result = await second;
            Assert.Equal(1, fixture.Acpi.ReadCount);
            Assert.Equal("6", AcpiFirmwareTestData.Field(result, "firmware_all_acpi_table_revision").Value!.Value.GetString());
        }
        finally { release.Set(); await first; }
    }

    [Fact]
    public async Task ActualHostCompositionRoutesInformationCpuAndProcessIntoOneSelectedBatch()
    {
        using var devices = new Fixture(new AcpiReader(AcpiFirmwareTestData.Tables()));
        await using var cpu = new CpuTopologyProviderTestFixture();
        var processApi = new ProcessBasicFieldCollectorTests.Api();
        using var process = ProcessBasicFieldCollectorTests.Fixture(processApi);
        var processes = new ResourceManager.App.Infrastructure.ReadWrite.HostProcessFieldBackend(process.Sampler);
        var metrics = new ResourceManager.App.Infrastructure.ReadWrite.HostReadWriteBackend(null!, null!,
            new ResourceManager.App.Infrastructure.RuntimeSpecialization.HardwareMetricCatalogState(), null!, null!);
        var composed = new ResourceManager.App.Infrastructure.ReadWrite.HostFieldBackends(metrics,
            new ResourceManager.App.Infrastructure.ReadWrite.HostFieldSubscriptionBackend(metrics, null!, null!), devices.Provider, cpu.Reader, processes);
        await devices.Provider.CaptureInformationAsync(default);
        await devices.Provider.CaptureAcpiInformationAsync(default);
        cpu.Sampler.Complete(CpuTopologyInformationFieldTests.Snapshot()); await cpu.Reader.CaptureInformationAsync(default);
        Assert.Equal(1, await cpu.Sampler.WaitForCaptureAsync());
        await processes.DiscoverAsync(default);
        var catalog = composed.Reads.Catalog;
        var memory = catalog.Fields.Single(item => item.Field.FieldName == ProcessBasicFieldCollectorTests.Names[1]).Field;
        var machine = catalog.Fields.Single(item => item.Field.FieldName == "machine_all_model_declared").Field;
        var cores = catalog.Fields.Single(item => item.Field.FieldName == DeviceInformationFieldRegistry.CpuAllPhysicalCoreCountReported.Name).Field;
        var acpi = catalog.Fields.Single(item => item.Field.FieldName == "firmware_all_acpi_table_revision").Field;
        Assert.Equal(MetricInteger.Parse("2"), composed.Reads.ReadCached(cores).Value?.Integer);
        Assert.Equal(MetricInteger.Parse("6"), composed.Reads.ReadCached(acpi).Value?.Integer);
        var trace = new MixedFirstMoveTrace();
        var diagnosedSubscriptions = WithMixedFirstMoveDiagnostics(composed, trace,
            (memory, "process"), (machine, "information"), (cores, "cpu"), (acpi, "acpi"));
        string DiagnosticSummary() => trace.Describe(
            $"recorded sources: device-topology reads={devices.Topology.ReadCount}, ACPI reads={devices.Acpi.ReadCount}, " +
            $"CPU captures={cpu.Sampler.CaptureCount}, process opens={processApi.Opens}, " +
            $"process memory calls={processApi.MemoryCalls}, closes={processApi.Closes}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task WaitForCpuCaptureAsync(int expected)
        {
            try { Assert.Equal(expected, await cpu.Sampler.WaitForCaptureAsync()); }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"Expected CPU capture {expected} did not start. {DiagnosticSummary()}", error);
            }
        }
        await devices.Provider.StartAsync(default);
        await process.Sampler.StartAsync(default);
        try
        {
            await using var stream = diagnosedSubscriptions.SubscribeAsync("mixed", [memory, machine, cores, acpi],
                TimeSpan.FromSeconds(1), new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.HighestFrequencyShared),
                cancellation.Token).GetAsyncEnumerator();
            var next = stream.MoveNextAsync().AsTask();
            Assert.Equal((1u, 1u, 1u, 1_000UL), cpu.ReadDemand());
            // The first ordinary sample is due immediately; the first periodic trigger is due
            // one period later. Only the latter has a field-delivery trigger.
            var initialPeriodicSample = CpuTopologyInformationFieldTests.Snapshot();
            cpu.Sampler.Complete(initialPeriodicSample);
            await cpu.Reader.StartAsync(default);
            await WaitForCpuCaptureAsync(2);
            await WaitForCpuCaptureAsync(3);
            var firstTriggerSample = CpuTopologyInformationFieldTests.Snapshot();
            cpu.Sampler.Complete(firstTriggerSample);
            bool hasRound;
            try { hasRound = await next.WaitAsync(cancellation.Token); }
            catch (OperationCanceledException error) when (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException($"The mixed first round did not complete within the existing 10-second gate. {DiagnosticSummary()}", error);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"The mixed first MoveNext failed. {DiagnosticSummary()}", error);
            }
            Assert.True(hasRound, $"A mixed child stream ended before the first round. {DiagnosticSummary()}");
            Assert.Equal(new[] { memory, machine, cores, acpi }, stream.Current.Select(static read => read.Field));
            Assert.Equal(MetricInteger.Parse("9007199254740993"), stream.Current[0].Value?.Integer);
            Assert.Equal(MetricInteger.Parse("2"), stream.Current[2].Value?.Integer); Assert.Equal(MetricInteger.Parse("6"), stream.Current[3].Value?.Integer);
            // Shared-frequency delivery uses the latest cache when it reads it. The
            // ordinary capture and delivery at the same period have no ordering.
            Assert.True(stream.Current[2].ObservedAt == initialPeriodicSample.CapturedAt
                || stream.Current[2].ObservedAt == firstTriggerSample.CapturedAt,
                $"The CPU reading must retain an actual completed sample time. {DiagnosticSummary()}");
        }
        finally
        {
            await devices.Provider.StopAsync(default);
            await process.Sampler.StopAsync(default);
            cpu.Sampler.Complete(new OperationCanceledException("Fixture shutdown."));
            await cpu.Reader.StopAsync(default);
        }
    }

    private static CompositeFieldSubscriptionBackend WithMixedFirstMoveDiagnostics(
        ResourceManager.App.Infrastructure.ReadWrite.HostFieldBackends host,
        MixedFirstMoveTrace trace, params (FieldReference Field, string Owner)[] selected)
    {
        var bindingType = typeof(IReadOnlyDictionary<IReadWriteBackend, IFieldSubscriptionBackend>);
        var bindingField = typeof(CompositeFieldSubscriptionBackend)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => bindingType.IsAssignableFrom(field.FieldType));
        var originalBindings = bindingField.GetValue(host.Subscriptions) as IReadOnlyDictionary<IReadWriteBackend, IFieldSubscriptionBackend>
            ?? throw new InvalidOperationException("The host composite did not retain its original child subscription bindings.");
        var ownerNames = selected.ToDictionary(static item => item.Field, static item => item.Owner);
        var fields = selected.Select(static item => item.Field).ToImmutableArray();
        var diagnosticBindings = new Dictionary<IReadWriteBackend, IFieldSubscriptionBackend>();
        foreach (var group in host.Reads.Select(fields))
        {
            if (!originalBindings.TryGetValue(group.Backend, out var original))
                throw new InvalidOperationException("A selected host owner has no original child subscription binding.");
            var label = string.Join(" + ", group.Fields.Select(field => $"{ownerNames[field]}[{field.FieldName}]"));
            trace.Register(label);
            diagnosticBindings.Add(group.Backend, new FirstMoveDiagnosticBackend(original, label, trace));
        }
        return new(host.Reads, diagnosticBindings);
    }

    private sealed class MixedFirstMoveTrace
    {
        private readonly ConcurrentDictionary<string, string> childStates = new(StringComparer.Ordinal);

        internal void Register(string child) => childStates[child] = "not invoked";
        internal void SubscribeRequested(string child) => childStates[child] = "SubscribeAsync invoked";
        internal void MoveStarted(string child) => childStates[child] = "first MoveNext pending";
        internal void MoveCompleted(string child, bool present, ImmutableArray<FieldReading> readings)
        {
            childStates[child] = present
                ? $"first MoveNext yielded [{string.Join("; ", readings.Select(static reading =>
                    $"{reading.Field.FieldName}: {reading.Status}, value={(reading.Value is null ? "null" : "present")}, observedAt={reading.ObservedAt:O}"))}]"
                : "first MoveNext reached end-of-stream";
        }
        internal void MoveFailed(string child, Exception error)
            => childStates[child] = $"first MoveNext threw {error.GetType().Name}: {error.Message}";

        internal string Describe(string sourceProgress)
            => $"Child streams: {string.Join(" | ", childStates.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => $"{item.Key}={item.Value}"))}. {sourceProgress}.";
    }

    private sealed class FirstMoveDiagnosticBackend(IFieldSubscriptionBackend inner, string label,
        MixedFirstMoveTrace trace) : IFieldSubscriptionBackend
    {
        public bool SupportsPeriodicDelivery => inner.SupportsPeriodicDelivery;
        public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval)
            => inner.Acquire(subscriptionId, fields, interval);
        public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval,
            FieldSubscriptionOptions options)
            => inner.Acquire(subscriptionId, fields, interval, options);
        public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
            ImmutableArray<FieldReference> fields, TimeSpan interval, CancellationToken cancellationToken)
        {
            trace.SubscribeRequested(label);
            return Observe(inner.SubscribeAsync(subscriptionId, fields, interval, cancellationToken), cancellationToken);
        }
        public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
            ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
            CancellationToken cancellationToken)
        {
            trace.SubscribeRequested(label);
            return Observe(inner.SubscribeAsync(subscriptionId, fields, interval, options, cancellationToken), cancellationToken);
        }

        private async IAsyncEnumerable<ImmutableArray<FieldReading>> Observe(
            IAsyncEnumerable<ImmutableArray<FieldReading>> source,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var iterator = source.GetAsyncEnumerator(cancellationToken);
            var first = true;
            while (true)
            {
                if (first) trace.MoveStarted(label);
                bool present;
                try { present = await iterator.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    if (first) trace.MoveFailed(label, error);
                    throw;
                }
                if (first)
                {
                    trace.MoveCompleted(label, present, present ? iterator.Current : []);
                    first = false;
                }
                if (!present) yield break;
                yield return iterator.Current;
            }
        }
    }
}
