using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task D3d12RaytracingHttpGetsArePassiveAndReturnTheExplicitOriginalCurrent()
    {
        var genericReader = new Reader(() => throw new InvalidOperationException("Passive D3D HTTP must not enumerate topology."));
        using var fixture = new Fixture(genericReader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        var adapter = new WindowsD3dAdapterIdentity(0x0000_0001_0000_002A, 0x10DE, 0x2684);
        var scope = new WindowsD3dCapabilityScope(WindowsD3dCapabilityFields.RaytracingTier,
            WindowsD3dApiVersion.Direct3D12, [adapter]);
        var recorded = new D3dRaytracingHttpReader();
        var access = fixture.Provider.CreateD3dCapabilityInformation(scope, recorded);
        const string name = "gpu_all_d3d12_raytracing_tier";
        var identity = $"windows-d3d-adapter|d3d12|luid:{adapter.Luid:X16}|pci:{adapter.PciVendorId:X4}:{adapter.PciDeviceId:X4}";
        var field = new FieldReference(identity, name);

        // Before any explicit capability discovery, both actual endpoints remain passive.
        using var initialFields = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.DoesNotContain(initialFields.RootElement.GetProperty("devices").EnumerateArray(),
            device => device.GetProperty("deviceIdentity").GetString() == identity);
        using (var state = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/state")))
            AssertInformationFieldsInStateMatchFieldsEndpoint(state.RootElement, initialFields.RootElement);
        Assert.Equal(0, genericReader.ReadCount);
        Assert.Equal(0, recorded.ReadCount);

        foreach (uint? tier in new uint?[] { 0, uint.MaxValue, null, 12 })
        {
            recorded.Tier = tier;
            var beforeExplicitRead = recorded.ReadCount;
            if (beforeExplicitRead == 0)
                Assert.Equal(field, Assert.Single((await access.Backend.DiscoverAsync(default)).Fields).Field);
            else
                _ = await access.Backend.CollectAsync([field], default);
            Assert.Equal(beforeExplicitRead + 1, recorded.ReadCount);
            Assert.Equal(WindowsD3dCapabilityFields.RaytracingTier, recorded.LastFields);
            Assert.Equal(0, genericReader.ReadCount);

            var originalState = fixture.Provider.ReadState();
            var originalInformation = originalState.InformationFields;
            var current = originalInformation.FindField(field);
            Assert.NotNull(current);
            Assert.Same(DeviceInformationFieldRegistry.GpuAllD3d12RaytracingTier, current.Definition);
            var readsBeforeGets = recorded.ReadCount;
            using var fieldsResponse = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var wireField = Field(fieldsResponse.RootElement, identity, name);
            var definition = wireField.GetProperty("definition");
            Assert.Equal(name, definition.GetProperty("name").GetString());
            Assert.Equal("gpu_adapter", definition.GetProperty("resource").GetString());
            Assert.Equal("all", definition.GetProperty("vendor").GetString());
            Assert.Equal("unsigned-integer", definition.GetProperty("valueKind").GetString());
            Assert.Equal("enum", definition.GetProperty("unit").GetString());
            Assert.Equal("windows.d3d12.capability-query", definition.GetProperty("sourceId").GetString());
            var reading = access.Backend.ReadCached(field);
            if (tier is { } value)
            {
                var expected = value.ToString(CultureInfo.InvariantCulture);
                Assert.Equal(JsonValueKind.String, wireField.GetProperty("value").ValueKind);
                Assert.Equal(expected, wireField.GetProperty("value").GetString());
                Assert.Equal(expected, current.Value?.GetString());
                Assert.Equal(FieldObservationStatus.Current, reading.Status);
                Assert.Equal(MetricInteger.Parse(expected), reading.Value?.Integer);
                Assert.NotNull(current.ObservedAt);
                Assert.Equal(current.ObservedAt, wireField.GetProperty("observedAt").GetDateTimeOffset());
                Assert.Equal(current.ObservedAt, reading.ObservedAt);
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, wireField.GetProperty("value").ValueKind);
                Assert.Null(current.Value);
                Assert.Null(current.ObservedAt);
                Assert.True(!wireField.TryGetProperty("observedAt", out var observedAt)
                    || observedAt.ValueKind == JsonValueKind.Null);
                Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
                Assert.Null(reading.Value);
                Assert.Null(reading.ObservedAt);
            }
            using var stateResponse = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/state"));
            AssertInformationFieldsInStateMatchFieldsEndpoint(stateResponse.RootElement, fieldsResponse.RootElement);
            Assert.Same(originalState, fixture.Provider.ReadState());
            Assert.Same(originalInformation, fixture.Provider.ReadState().InformationFields);
            Assert.Equal(readsBeforeGets, recorded.ReadCount);
            Assert.Equal(0, genericReader.ReadCount);
        }
        await app.StopAsync();
    }

    private sealed class D3dRaytracingHttpReader : IWindowsD3dCapabilityReader
    {
        private int readCount;
        public int ReadCount => Volatile.Read(ref readCount);
        public uint? Tier { get; set; }
        public WindowsD3dCapabilityFields LastFields { get; private set; }
        public WindowsD3dCapabilityReadResult Read(WindowsD3dCapabilityScope scope,
            WindowsD3dCapabilityFields selectedFields, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref readCount);
            LastFields = selectedFields;
            return new([new(scope.Adapters.Single(), scope.ApiVersion, null, []) { RaytracingTier = Tier }]);
        }
    }
}
