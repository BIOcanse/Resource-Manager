using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace Resource_Manager_APP.Tests;

public sealed class HostNpuInformationFieldsTests
{
    internal const string Identity = @"windows-npu|ACPI\NPU_A";

    [Fact]
    public void CoreDirectoryReaderPassesTheSameNativeFactsAndKeepsUnavailableDistinctFromSuccessfulEmpty()
    {
        var native = new DeviceTopologyNativeSnapshot(new Dictionary<string, DeviceTopologyNativeDevice>(), null);
        var calls = 0;
        Assert.Empty(WindowsDeviceTopologyReader.ReadNpuDeclarations(native, captured =>
        { calls++; Assert.Same(native, captured); return []; })!);
        Assert.Null(WindowsDeviceTopologyReader.ReadNpuDeclarations(native, _ => null));
        Assert.Null(WindowsDeviceTopologyReader.ReadNpuDeclarations(native, _ => throw new InvalidOperationException("DXCore failure")));
        Assert.Null(WindowsDeviceTopologyReader.ReadNpuDeclarations(native with { Error = "partial PnP" }, _ =>
        { calls++; return []; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RealHostBackendsExposeOriginalDeviceCurrentAndSubscriptionsThenAllowDirectCaptureAfterStop()
    {
        var topology = new Topology();
        var journal = new Journal();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var devices = new DeviceTopologySnapshotProvider(topology, journal, new(), sampling.Owner,
            NullLogger<DeviceTopologySnapshotProvider>.Instance, new Acpi());
        await using var cpu = new CpuTopologyProviderTestFixture();
        using var process = ProcessBasicFieldCollectorTests.Fixture(new ProcessBasicFieldCollectorTests.Api());
        var metrics = new HostReadWriteBackend(null!, null!, new DashboardMonitoringCatalogState(), null!, null!);
        var host = new HostFieldBackends(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!),
            devices, cpu.Reader, new HostProcessFieldBackend(process.Sampler));
        Assert.Equal(0, topology.Reads);
        Assert.DoesNotContain(host.Reads.Catalog.Fields, field => field.Field.DeviceIdentity == Identity);
        _ = await devices.CaptureInformationAsync(default);
        var fields = host.Reads.Catalog.Fields.Where(static field => field.Field.DeviceIdentity == Identity)
            .Select(static field => field.Field).ToImmutableArray();
        Assert.Equal(5, fields.Length);
        var name = fields.Single(static field => field.FieldName == "npu_all_name");
        Assert.Equal("Declared NPU", host.Reads.ReadCached(name).Value!.Text);
        Assert.Equal(1, topology.Reads);
        var current = devices.ReadState().InformationFields;
        _ = host.Reads.ReadCached(name);
        Assert.Same(current, devices.ReadState().InformationFields);
        Assert.Equal(1, topology.Reads);
        topology.Rows = [Row() with { NameDeclared = "replacement" }];
        var captured = Assert.Single(await host.Reads.CollectAsync([name], default));
        Assert.Equal("replacement", captured.Value!.Text);
        Assert.Equal(captured, host.Reads.ReadCached(name));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await devices.StartAsync(timeout.Token);
        try
        {
            using var slow = host.Subscriptions.Acquire("npu-slow", fields, TimeSpan.FromMinutes(1));
            await using var stream = host.Subscriptions.SubscribeAsync("npu-fast", [name], TimeSpan.FromMilliseconds(100),
                timeout.Token).GetAsyncEnumerator();
            Assert.True(await stream.MoveNextAsync());
            var reading = Assert.Single(stream.Current);
            Assert.Equal("replacement", reading.Value!.Text);
            var cached = host.Reads.ReadCached(name);
            Assert.Equal(reading with { ObservedAt = cached.ObservedAt }, cached);
            topology.Rows = null;
            do
            {
                Assert.True(await stream.MoveNextAsync());
                reading = Assert.Single(stream.Current);
            } while (reading.Value is not null);
            Assert.All(fields, field => Assert.Null(host.Reads.ReadCached(field).Value));
            Assert.Equal(5, host.Reads.Catalog.Fields.Count(static field => field.Field.DeviceIdentity == Identity));
        }
        finally { await devices.StopAsync(CancellationToken.None); }
        topology.Rows = [Row()];
        Assert.Equal("Declared NPU", Assert.Single(await host.Reads.CollectAsync([name], default)).Value!.Text);
        Assert.True(journal.Saves > 0);
        Assert.Equal(0, cpu.Sampler.CaptureCount);
    }

    internal static DeviceTopologyNpuInformation Row() => new(Identity, @"ACPI\NPU_A", "Declared NPU", "1.2.3",
        "reported location", ["ACPI(_SB_)#NPU(0)"]);
    internal static DeviceTopologySnapshot Snapshot(IReadOnlyList<DeviceTopologyNpuInformation>? rows)
        => new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []) { NpuInformation = rows };

    private sealed class Topology : IDeviceTopologyReader
    {
        private int reads;
        private IReadOnlyList<DeviceTopologyNpuInformation>? rows = [Row()];
        internal int Reads => Volatile.Read(ref reads);
        internal IReadOnlyList<DeviceTopologyNpuInformation>? Rows
        { get => Volatile.Read(ref rows); set => Volatile.Write(ref rows, value); }
        public DeviceTopologySnapshot ReadSnapshot()
        { Interlocked.Increment(ref reads); return Snapshot(Rows); }
    }
    private sealed class Acpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("NPU fields must not query ACPI"); }
    private sealed class Journal : IDeviceTopologySnapshotStore
    {
        private int saves;
        internal int Saves => Volatile.Read(ref saves);
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token)
            => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot value, CancellationToken token)
        { Interlocked.Increment(ref saves); return Task.CompletedTask; }
    }
}

// Reuse the existing endpoint fixture and HTTP setup without duplicating a Host/test server.
public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task NpuCachedHttpKeepsFiveDefinitionsClearsUnavailableThenRemovesSuccessfulEmpty()
    {
        var healthy = HostNpuInformationFieldsTests.Snapshot([HostNpuInformationFieldsTests.Row()]);
        var reader = new Reader(healthy, healthy with { NpuInformation = null }, healthy with { NpuInformation = [] });
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using (var initial = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields")))
            Assert.Empty(initial.RootElement.GetProperty("devices").EnumerateArray());
        Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            await fixture.Provider.SampleNowAsync(default);
            var state = fixture.Provider.ReadState();
            using var message = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var devices = message.RootElement.GetProperty("devices").EnumerateArray();
            if (phase == 2)
                Assert.DoesNotContain(devices, static device => device.GetProperty("deviceIdentity").GetString() == HostNpuInformationFieldsTests.Identity);
            else
            {
                var npu = Assert.Single(devices, static device => device.GetProperty("deviceIdentity").GetString() == HostNpuInformationFieldsTests.Identity);
                var fields = npu.GetProperty("fields").EnumerateArray().ToArray();
                Assert.Equal(5, fields.Length);
                Assert.All(fields, field => Assert.Equal(phase == 0,
                    field.GetProperty("value").ValueKind != JsonValueKind.Null));
                if (phase == 0)
                {
                    Assert.Equal(@"ACPI\NPU_A", Field(message.RootElement, HostNpuInformationFieldsTests.Identity,
                        "npu_all_device_id_declared").GetProperty("value").GetString());
                    Assert.Equal("ACPI(_SB_)#NPU(0)", Field(message.RootElement, HostNpuInformationFieldsTests.Identity,
                        "npu_all_location_paths_declared").GetProperty("value")[0].GetString());
                }
            }
            Assert.Equal(phase + 1, reader.ReadCount);
            Assert.Same(state, fixture.Provider.ReadState());
        }
        await app.StopAsync();
    }
}
