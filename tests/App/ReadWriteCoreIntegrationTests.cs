using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;

namespace Resource_Manager_APP.Tests;

public sealed class ReadWriteCoreIntegrationTests
{
    [Theory]
    [InlineData("fan_all_rotational_speed", "fan_all_speed_rpm")]
    [InlineData("fan_all_pwm_reported", "fan_all_reported_pwm_duty")]
    public async Task AliasAndCanonicalSelectionsRetainPublicIdentityButCollectOneSourceAddress(string alias, string canonical)
    {
        var definition = MetricFieldRegistry.FindByName(canonical)!;
        using var fixture = new Fixture(Bind(definition, "recorded-fan", NativeMetricSnapshotSources.NotebookOemFan, 0));
        ImmutableArray<FieldReference> fields = [new("recorded-fan", alias), new("recorded-fan", canonical), new("recorded-fan", alias)];
        foreach (var field in fields)
        {
            Assert.Equal(field, fixture.Core.Query(field).Field);
            Assert.True(fixture.Core.Query(field).Read.Supported);
            Assert.False(fixture.Core.Query(field).Write.Supported);
        }
        var readings = await fixture.Core.CollectAsync(fields);
        Assert.Equal(fields.ToArray(), readings.Select(static reading => reading.Field).ToArray());
        Assert.All(readings, reading => { Assert.Equal(FieldObservationStatus.Current, reading.Status); Assert.Equal(0, reading.Value!.Number); });
        Assert.Equal([Assert.Single(fixture.Source.Current.FieldBindings).Key], Assert.Single(fixture.Source.Captured).Ids);
        Assert.Equal(fields[0], fixture.Core.ReadCached(fields[0]).Field);
        var composite = new CompositeReadWriteBackend([fixture.CoreBackend]);
        var composed = await composite.CollectAsync(fields, default);
        Assert.Equal(readings.ToArray(), composed.ToArray());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("device")]
    [InlineData("name")]
    [InlineData("unit")]
    [InlineData("kind")]
    public async Task ReusedRequestAddressCannotRedirectAnExistingFieldReading(string change)
    {
        var original = MetricDeviceFieldTests.CreateSnapshot(SamplingObservationStatus.Current);
        using var fixture = new Fixture(original);
        var field = Assert.Single(fixture.Core.Catalog.Fields).Field;
        var binding = Assert.Single(original.FieldBindings).Value;
        var replacement = change switch
        {
            "device" => binding with { DeviceIdentity = "different-device" },
            "name" => binding with { Field = binding.Field with { Name = "different-field" } },
            "unit" => binding with { Field = binding.Field with { Unit = "different-unit" } },
            "kind" => binding with { Field = binding.Field with { ValueKind = "unsigned64" } },
            _ => binding
        };
        fixture.Source.Current = original with
        {
            FieldBindings = change == "missing" ? new Dictionary<string, MetricDeviceFieldBinding>()
                : new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = replacement }
        };
        var cached = fixture.Core.ReadCached(field);
        Assert.Equal(FieldObservationStatus.Unavailable, cached.Status);
        Assert.Null(cached.Value);
        Assert.Null(Assert.Single(await fixture.Core.CollectAsync([field])).Value);
        fixture.Source.Current = original;
        Assert.Equal(1024, fixture.Core.ReadCached(field).Value!.Number);
    }

    [Fact]
    public void DirectoryAndCacheReadsDoNotCollectProbeControlsOrSubscribe()
    {
        using var fixture = new Fixture(MetricDeviceFieldTests.CreateSnapshot(SamplingObservationStatus.Current));
        var field = Assert.Single(fixture.Core.Catalog.Fields).Field;
        Assert.True(fixture.Core.Query(field).Read.Supported);
        Assert.Empty(fixture.Core.Query(field).Read.Dependencies);
        Assert.False(fixture.Core.Query(field).Write.Supported);
        var reading = fixture.Core.ReadCached(field);
        Assert.Equal(1024, reading.Value!.Number);
        Assert.Equal(FieldObservationStatus.Current, reading.Status);
        Assert.Equal([Assert.Single(fixture.Source.Current.FieldBindings).Key], fixture.Source.LastCachedRequest!.Ids);
        Assert.Throws<ArgumentException>(() => fixture.Core.ReadCached(field with { DeviceIdentity = "other-device" }));
        Assert.Empty(fixture.Source.Captured);
        Assert.Equal(0, fixture.Controls.ReadCount);
    }

    [Fact]
    public async Task DirectCollectionUsesTheOriginalCapturePathAndExactRequestedAddresses()
    {
        using var fixture = new Fixture(NetworkCounterInformationCollectorTests.CommittedSnapshot());
        var exactBinding = Assert.Single(fixture.Source.Current.FieldBindings.Values, binding => binding.Field.Code == MetricFieldCode.NetworkRxErrors
            && fixture.Source.Current.Items[binding.RequestAddress].IntegerValue == ulong.MaxValue.ToString(CultureInfo.InvariantCulture));
        var field = Assert.Single(fixture.Core.Catalog.Fields,
            entry => entry.Field.DeviceIdentity == exactBinding.DeviceIdentity && entry.Definition.Name == exactBinding.Field.Name).Field;
        var reading = Assert.Single(await fixture.Core.CollectAsync([field]));
        Assert.Equal(ulong.MaxValue.ToString(CultureInfo.InvariantCulture), reading.Value!.Integer);
        Assert.Null(reading.Value.Number);
        Assert.Equal(FieldObservationStatus.Current, reading.Status);
        Assert.Equal([exactBinding.RequestAddress],
            Assert.Single(fixture.Source.Captured).Ids);
        Assert.Equal(0, fixture.Controls.ReadCount);
    }

    [Theory]
    [InlineData("wide")]
    [InlineData("flag")]
    [InlineData("bytes")]
    [InlineData("zero")]
    public async Task TypedValuesKeepExactIntegersFalseEmptyBytesAndZero(string kind)
    {
        var field = kind switch
        {
            "wide" => MetricFieldRegistry.Definitions.Single(item => item.Code == MetricFieldCode.NvmeDataUnitsRead),
            "flag" => MetricFieldRegistry.Definitions.Single(item => item.Code == MetricFieldCode.IgclPowerLimited),
            "bytes" => MetricFieldRegistry.Definitions.Single(item => item.Code == MetricFieldCode.NvmeHealthLogRaw),
            _ => MetricFieldRegistry.FindByTemplate("memory.availableBytes")!
        };
        var snapshot = Bind(field, "device", "source", number: kind == "zero" ? 0 : null,
            integer: kind == "wide" ? UInt128.MaxValue.ToString(CultureInfo.InvariantCulture) : null,
            boolean: kind == "flag" ? false : null, bytes: kind == "bytes" ? [] : null);
        using var fixture = new Fixture(snapshot);
        var reference = Assert.Single(fixture.Core.Catalog.Fields).Field;
        var value = Assert.Single(await fixture.Core.CollectAsync([reference])).Value!;
        if (kind == "wide") { Assert.Equal(UInt128.MaxValue.ToString(CultureInfo.InvariantCulture), value.Integer); Assert.Null(value.Number); }
        else if (kind == "flag") { Assert.False(value.Boolean); Assert.Null(value.Number); }
        else if (kind == "bytes") { Assert.True(value.Bytes.HasValue); Assert.Empty(value.Bytes.Value); }
        else Assert.Equal(0, value.Number);
    }

    [Fact]
    public async Task FailedReadingClearsPayloadAndCancellationDoesNotCollect()
    {
        using var fixture = new Fixture(MetricDeviceFieldTests.CreateSnapshot(SamplingObservationStatus.Current));
        var field = Assert.Single(fixture.Core.Catalog.Fields).Field;
        fixture.Source.Current = fixture.Source.Current with
        {
            Datasets = fixture.Source.Current.Datasets.ToDictionary(pair => pair.Key,
                pair => pair.Value with { Status = SamplingObservationStatus.Unavailable })
        };
        var reading = Assert.Single(await fixture.Core.CollectAsync([field]));
        Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
        Assert.Null(reading.Value);
        Assert.Null(reading.ObservedAt);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.Core.CollectAsync([field], cancellation.Token));
        Assert.Single(fixture.Source.Captured);
    }

    [Fact]
    public void WorkspaceReplacementRetainsUnchangedFieldsAndDoesNotReassignADeletedDevicesValue()
    {
        var memory = MetricDeviceFieldTests.CreateSnapshot(SamplingObservationStatus.Current);
        var definition = MetricFieldRegistry.Definitions.Single(item => item.Code == MetricFieldCode.GpuPowerLimit);
        var oldGpu = Bind(definition, "gpu-A", "nvidia.nvml", 100, adapterKey: 0xA1);
        var oldSnapshot = memory with
        {
            FieldBindings = memory.FieldBindings.Concat(oldGpu.FieldBindings).ToDictionary(),
            Items = memory.Items.Concat(oldGpu.Items).ToDictionary(),
            Datasets = memory.Datasets.Concat(oldGpu.Datasets).ToDictionary()
        };
        var cache = new LastSuccessfulHardwareMetricSnapshot();
        Assert.True(cache.Publish(oldSnapshot, MetricSampleRequest.ForIds(oldSnapshot.FieldBindings.Keys)).IsSuccessful);
        var newGpu = Bind(definition, "gpu-B", "nvidia.nvml", 125, adapterKey: 0xB2);
        var newSnapshot = newGpu with
        {
            WorkspaceIdentity = 2,
            CapturedAt = oldSnapshot.CapturedAt.AddSeconds(1),
            FieldBindings = memory.FieldBindings.Concat(newGpu.FieldBindings).ToDictionary(),
            Datasets = memory.Datasets.Concat(newGpu.Datasets).ToDictionary(pair => pair.Key,
                pair => pair.Value with { WorkspaceIdentity = 2 })
        };
        Assert.True(cache.Publish(newSnapshot, MetricSampleRequest.ForIds(newGpu.FieldBindings.Keys)).IsSuccessful);
        var published = cache.Read()!;
        var retained = Assert.Single(Assert.Single(MetricDeviceFields.Read(published, "memory")).Fields);
        Assert.Equal(SamplingObservationStatus.Current, retained.Status);
        Assert.Equal(1024, retained.NumericValue);
        Assert.DoesNotContain(Assert.Single(oldGpu.FieldBindings).Key, published.Datasets.Keys);
        Assert.DoesNotContain(Assert.Single(oldGpu.FieldBindings).Key, published.Items.Keys);
        var replacement = Assert.Single(Assert.Single(MetricDeviceFields.Read(published, "gpu-B")).Fields);
        Assert.Equal(125, replacement.NumericValue);
        Assert.DoesNotContain(published.FieldBindings.Values, binding => binding.DeviceIdentity == "gpu-A");
    }

    [Fact]
    public async Task PowerLimitWritesMatchPhysicalIdentityDespiteDisplayOrderAndUseTheOriginalWriteLayer()
    {
        var field = MetricFieldRegistry.Definitions.Single(item => item.Code == MetricFieldCode.GpuPowerLimit);
        using var fixture = new Fixture(Bind(field, "gpu-physical-A", "nvidia.nvml", 100, adapterKey: 0xA1));
        fixture.Controls.Objects = [Gpu("gpu-B", 0xB2, 0), Gpu("gpu-A", 0xA1, 9)];
        var reference = Assert.Single(fixture.Core.Catalog.Fields).Field;
        Assert.False(fixture.Core.Query(reference).Write.Supported);
        Assert.Equal(0, fixture.Controls.ReadCount);
        await fixture.Core.DiscoverAsync();
        Assert.True(Assert.Single(fixture.Source.Captured).IsCatalogProbe);
        Assert.Equal(new[] { "nvidia.nvml" }, fixture.Core.Query(reference).Write.Dependencies.ToArray());
        fixture.Controls.Objects = [Gpu("gpu-A", 0xA1, 0), Gpu("gpu-B", 0xB2, 9)];
        var result = await fixture.Core.WriteAsync(reference, new(Number: 110));
        Assert.Equal(ControlApplyStatuses.Applied, result.Status);
        var write = Assert.Single(fixture.Writer.Written);
        Assert.Equal("gpu-A", write.Target.Id);
        Assert.Equal("gpu.power-limit", write.Setting.CapabilityId);
        Assert.Equal("W", write.Setting.Unit);
        Assert.Equal(110, write.Setting.Number);
        Assert.Equal(0xA1UL, write.Target.AdapterKey);
        Assert.Equal("failed", (await fixture.Core.WriteAsync(reference, new(Number: double.NaN))).Status);
        Assert.Equal("failed", (await fixture.Core.WriteAsync(reference, new(Integer: "110"))).Status);
        Assert.Single(fixture.Writer.Written);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedOrAmbiguousDeviceIdentityCannotRedirectAWrite(bool ambiguous)
    {
        var field = MetricFieldRegistry.Definitions.Single(item => item.Code == MetricFieldCode.GpuPowerLimit);
        using var fixture = new Fixture(Bind(field, "gpu-A", "nvidia.nvml", 100, adapterKey: 0xA1));
        fixture.Controls.Objects = [Gpu("gpu-A", 0xA1, 0)];
        await fixture.Core.DiscoverAsync();
        var reference = Assert.Single(fixture.Core.Catalog.Fields).Field;
        fixture.Controls.Objects = ambiguous ? [Gpu("gpu-A", 0xA1, 0), Gpu("duplicate", 0xA1, 1)]
            : [Gpu("gpu-A", 0xB2, 0)];
        Assert.Equal(ControlApplyStatuses.Unsupported, (await fixture.Core.WriteAsync(reference, new(Number: 110))).Status);
        Assert.Empty(fixture.Writer.Written);
    }

    [Fact]
    public async Task MeasuredValuesRemainReadOnlyAndWritesKeepOriginalPermissionsAndRangeChecks()
    {
        using var memory = new Fixture(MetricDeviceFieldTests.CreateSnapshot(SamplingObservationStatus.Current));
        var readOnly = Assert.Single(memory.Core.Catalog.Fields).Field;
        Assert.Equal("unsupported", (await memory.Core.WriteAsync(readOnly, new(Number: 0))).Status);
        Assert.Equal(0, memory.Controls.ReadCount);
        var field = MetricFieldRegistry.Definitions.Single(item => item.Code == MetricFieldCode.GpuPowerLimit);
        using var power = new Fixture(Bind(field, "gpu-A", "nvidia.nvml", 100, adapterKey: 0xA1));
        power.Controls.Objects = [Gpu("gpu-A", 0xA1, 0, ControlAccessLevels.Root)];
        await power.Core.DiscoverAsync();
        var reference = Assert.Single(power.Core.Catalog.Fields).Field;
        Assert.Equal(ControlApplyStatuses.Unsupported, (await power.Core.WriteAsync(reference, new(Number: 110))).Status);
        power.Controls.Objects = [Gpu("gpu-A", 0xA1, 0)];
        Assert.Equal(ControlApplyStatuses.Failed, (await power.Core.WriteAsync(reference, new(Number: 999))).Status);
        Assert.Empty(power.Writer.Written);
    }

    [Fact]
    public async Task CapabilityHttpGetUsesTheSameDirectoryWithoutCapturingOrProbing()
    {
        using var fixture = new Fixture(MetricDeviceFieldTests.CreateSnapshot(SamplingObservationStatus.Current));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.Core);
        var state = new DashboardMonitoringCatalogState();
        state.PublishCatalogProbe(fixture.Source.Current);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton<IMetricSampler>(fixture.Source);
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapMetricFieldEndpoints",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses)) };
        var response = await client.GetAsync("/api/metrics/fields/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var capability = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("memory_all_physical_available_bytes", capability.GetProperty("field").GetProperty("fieldName").GetString());
        Assert.True(capability.GetProperty("read").GetProperty("supported").GetBoolean());
        Assert.False(capability.GetProperty("write").GetProperty("supported").GetBoolean());
        Assert.Empty(fixture.Source.Captured);
        Assert.Equal(0, fixture.Controls.ReadCount);
        await app.StopAsync();
    }

    private static HardwareMetricSnapshot Bind(MetricFieldDefinition field, string device, string source,
        double? number = null, string? integer = null, bool? boolean = null, byte[]? bytes = null, ulong? adapterKey = null)
    {
        var basis = MetricDeviceFieldTests.CreateSnapshot(SamplingObservationStatus.Current);
        var address = $"{device}/{field.Name}";
        var value = Assert.Single(basis.Items).Value with { Id = address, NumericValue = number, SourceId = source,
            IntegerValue = integer, BooleanValue = boolean, BytesValue = bytes };
        return basis with
        {
            Items = new Dictionary<string, MetricValue> { [address] = value },
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { [address] = new(device, field, address, source) { AdapterKey = adapterKey } },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            { [address] = Assert.Single(basis.Datasets).Value with { DatasetId = address } }
        };
    }

    private static ControlObject Gpu(string id, ulong key, int displayIndex, string access = ControlAccessLevels.Normal)
        => new(id, ControlObjectKinds.Gpu, "GPU", new(ControlOperatingSystems.Windows, ControlVendors.Nvidia),
            [new("gpu.power-limit", "Power limit", ControlValueKinds.Number, true, Range: new(50, 200, 1, "W", 100),
                RequiredAccessLevel: access)], AdapterIndex: displayIndex) { AdapterKey = key };

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient client = new();
        internal Source Source { get; }
        internal Catalog Controls { get; } = new();
        internal Writer Writer { get; } = new();
        internal ReadWriteCore Core { get; }
        internal IReadWriteBackend CoreBackend { get; }
        internal Fixture(HardwareMetricSnapshot snapshot)
        {
            Source = new(snapshot);
            var state = new DashboardMonitoringCatalogState();
            state.PublishCatalogProbe(snapshot);
            CoreBackend = new HostReadWriteBackend(Source, Source, state, Controls,
                new ControlWriteLayer(Controls, [Writer], new Access()));
            Core = new(CoreBackend, new DependencyActions(client, []));
        }
        public void Dispose() => client.Dispose();
    }

    private sealed class Source(HardwareMetricSnapshot snapshot) : IMetricSampler, IMetricSnapshotObservationSource
    {
        internal HardwareMetricSnapshot Current { get; set; } = snapshot;
        internal List<MetricSampleRequest> Captured { get; } = [];
        internal MetricSampleRequest? LastCachedRequest { get; private set; }
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken token) => throw new InvalidOperationException("Direct APIs must not enter subscription reads.");
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken token) => throw new InvalidOperationException("Direct APIs must not enter subscription reads.");
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval) => throw new InvalidOperationException("Direct APIs must not subscribe.");
        public HardwareMetricSnapshot ReadLatest(MetricSampleRequest request) { LastCachedRequest = request; return Current; }
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Captured.Add(request); return Task.FromResult(Current); }
    }

    private sealed class Catalog : IControlObjectCatalog
    {
        internal IReadOnlyList<ControlObject> Objects { get; set; } = [];
        internal int ReadCount { get; private set; }
        public ControlObjectCatalog ReadObjects() { ReadCount++; return new(Objects, DateTimeOffset.UnixEpoch); }
    }
    private sealed class Writer : IControlWriter
    {
        internal List<(ControlObject Target, ControlSetting Setting)> Written { get; } = [];
        public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability) => ControlWriteAvailability.Yes(capability.Range);
        public Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability, ControlSetting setting, CancellationToken token)
        { Written.Add((target, setting)); return Task.FromResult(new ControlApplyOutcome(target.Id, capability.Id, ControlApplyStatuses.Applied, null)); }
    }
    private sealed class Access : IControlAccessLevel
    {
        public string Current => ControlAccessLevels.Normal;
        public Task SetAsync(string level, CancellationToken token) => throw new NotSupportedException();
    }
}
