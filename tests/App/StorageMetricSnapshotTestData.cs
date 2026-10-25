using System.Buffers.Binary;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

// Shared recorded storage input and the real Native committed-snapshot factory.
internal static class StorageMetricSnapshotTestData
{
    internal const string Identity = "windows-storage|unique|DISK-A";
    internal const ulong LargeCount = (1UL << 63) | 3;
    internal static HardwareMetricSnapshot CommittedSnapshot()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"storage-fields-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [new(7, Identity, 1) { SupportsNvmeHealth = true }], [], workspace.CreateCatalogHandleMap());
        var now = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, now);
        var bindings = catalog.MetricBindings.Values.Where(b => b.DeviceIdentity == Identity).ToArray();
        var plan = workspace.PlanCollection(bindings.Select(b => b.MetricHandle).ToArray(), Modes(catalog), false, now);
        var zone = Zone([Counter()]);
        foreach (var source in plan.Sources)
        {
            var sensors = catalog.SourceIds[source.SourceHandle] == NativeMetricSnapshotSourceCatalog.WindowsNvmeHealth
                ? zone.ReadNvmeSensorsCore(new(new Dictionary<int, string> { [7] = Identity })) : zone.ReadStorageSensors();
            workspace.CompleteSource(plan, WindowsHardwareMetricSampler.CreateStorageCompletion(plan, source,
                new(source.SourceHandle, 1, 1), now, sensors), now);
        }
        return NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
    }

    internal static WindowsStorageSensorsMonitoringZone Zone(IReadOnlyList<Dictionary<string, object?>> counters)
        => new((_, query) => new(query.Contains("MSFT_PhysicalDisk") ? [Physical()]
            : query.Contains("MSFT_Disk") ? [OsDisk()] : counters, null),
            (_, _, _, _) => WindowsNvmeHealthReader.ParseHealthResponse(Response()));
    internal static NativeMetricSnapshotSourceRuntimeMode[] Modes(NativeMetricSnapshotCatalogProjection catalog)
        => catalog.Sources.Select(s => new NativeMetricSnapshotSourceRuntimeMode(s.SourceHandle, 1,
            NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
    internal static Dictionary<string, object?> Physical(int id = 7, string uid = "DISK-A", string serial = "SN-A") => new()
    { ["DeviceId"] = id.ToString(), ["UniqueId"] = uid, ["UniqueIdFormat"] = 8, ["SerialNumber"] = serial, ["BusType"] = 17, ["Size"] = 1234UL, ["HealthStatus"] = 2UL };
    internal static Dictionary<string, object?> OsDisk(int number = 23, string uid = "DISK-A", string serial = "SN-A") => new()
    { ["Number"] = number, ["UniqueId"] = uid, ["SerialNumber"] = serial, ["BusType"] = 17, ["Size"] = 1234UL };
    internal static Dictionary<string, object?> Counter() => new()
    {
        ["DeviceId"] = "7", ["Temperature"] = 35, ["TemperatureMax"] = 90, ["Wear"] = 150,
        ["ReadErrorsTotal"] = LargeCount, ["ReadErrorsCorrected"] = 2UL, ["ReadErrorsUncorrected"] = 1UL,
        ["WriteErrorsTotal"] = 3UL, ["WriteErrorsCorrected"] = 0UL, ["WriteErrorsUncorrected"] = 3UL,
        ["PowerOnHours"] = 45, ["StartStopCycleCount"] = 4, ["StartStopCycleCountMax"] = 20,
        ["LoadUnloadCycleCount"] = 5, ["LoadUnloadCycleCountMax"] = 30,
        ["ReadLatencyMax"] = 300, ["WriteLatencyMax"] = 250, ["FlushLatencyMax"] = 200
    };
    internal static byte[] Response()
    {
        var response = WindowsNvmeHealthReader.CreateHealthQuery();
        Write(response, 0, 48); Write(response, 4, 48);
        var log = response.AsSpan(48);
        log[0] = 0xA5; log[3] = 90; log[4] = 10; log[5] = 255;
        BinaryPrimitives.WriteUInt16LittleEndian(log[1..], 310);
        Write(response, 48 + 192, 42); Write(response, 48 + 196, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(log[214..], 270);
        log.Slice(32, 160).Fill(255); // All ten counters exercise the full unsigned128 maximum.
        return response;
    }
    internal static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    internal static void Write(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
