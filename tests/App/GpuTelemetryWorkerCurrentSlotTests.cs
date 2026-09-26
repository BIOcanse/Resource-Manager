using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring.GpuTelemetry;

namespace ResourceManager.App.Tests;

public sealed class GpuTelemetryWorkerCurrentSlotTests
{
    [Fact]
    public void MergeCurrentSnapshot_ReplacesOnlyRequestedCounterSlots()
    {
        var current = Snapshot(
            GpuTelemetryWorkerStatus.Online,
            Counter("gpu.0.compute", 10),
            Counter("gpu.0.memory", 20));
        var update = Snapshot(
            GpuTelemetryWorkerStatus.Online,
            Counter("gpu.0.compute", 30));

        var merged = GpuTelemetryWorkerSupervisor.MergeCurrentSnapshot(
            current,
            update,
            ["gpu.0.compute"]);

        Assert.Equal(30, ReadValue(merged, "gpu.0.compute"));
        Assert.Equal(20, ReadValue(merged, "gpu.0.memory"));
    }

    [Fact]
    public void MergeCurrentSnapshot_PublishesEmptyOnlyForFailedRequestedSlots()
    {
        var current = Snapshot(
            GpuTelemetryWorkerStatus.Online,
            Counter("gpu.0.compute", 10),
            Counter("gpu.0.memory", 20));
        var failed = Snapshot(GpuTelemetryWorkerStatus.Failed);

        var merged = GpuTelemetryWorkerSupervisor.MergeCurrentSnapshot(
            current,
            failed,
            ["gpu.0.memory"]);

        Assert.Equal(10, ReadValue(merged, "gpu.0.compute"));
        Assert.Null(ReadValue(merged, "gpu.0.memory"));
        Assert.Equal(GpuTelemetryWorkerStatus.Failed, merged.Status);
    }

    [Fact]
    public void MergeCurrentSnapshot_OnlineTopologyDropsAdaptersMissingFromTheUpdate()
    {
        var current = SnapshotWithAdapters(
            GpuTelemetryWorkerStatus.Online,
            Adapter(0, 10, Counter("gpu.0.compute", 10)),
            Adapter(1, 20, Counter("gpu.1.compute", 20)));
        var update = SnapshotWithAdapters(
            GpuTelemetryWorkerStatus.Online,
            Adapter(1, 20, Counter("gpu.1.compute", 30)));

        var merged = GpuTelemetryWorkerSupervisor.MergeCurrentSnapshot(
            current,
            update,
            ["gpu.1.compute"]);

        var adapter = Assert.Single(merged.Adapters);
        Assert.Equal(1, adapter.AdapterIndex);
        Assert.Equal(20UL, adapter.AdapterIdentity);
        Assert.Equal(30, ReadValue(merged, "gpu.1.compute"));
    }

    [Fact]
    public void MergeCurrentSnapshot_DoesNotInheritCountersAcrossAdapterIdentityReplacement()
    {
        var current = SnapshotWithAdapters(
            GpuTelemetryWorkerStatus.Online,
            Adapter(
                0,
                10,
                Counter("gpu.0.compute", 10),
                Counter("gpu.0.memory", 20)));
        var update = SnapshotWithAdapters(
            GpuTelemetryWorkerStatus.Online,
            Adapter(0, 99, Counter("gpu.0.compute", 30)));

        var merged = GpuTelemetryWorkerSupervisor.MergeCurrentSnapshot(
            current,
            update,
            ["gpu.0.compute"]);

        var adapter = Assert.Single(merged.Adapters);
        Assert.Equal(99UL, adapter.AdapterIdentity);
        Assert.Equal(30, ReadValue(merged, "gpu.0.compute"));
        Assert.DoesNotContain(
            adapter.Counters,
            static counter => counter.CounterId == "gpu.0.memory");
    }

    [Fact]
    public void ProjectCurrentSnapshot_ReturnsOnlyRequestedCounterSlots()
    {
        var current = SnapshotWithAdapters(
            GpuTelemetryWorkerStatus.Online,
            Adapter(
                0,
                10,
                Counter("gpu.0.compute", 10),
                Counter("gpu.0.memory", 20)),
            Adapter(
                1,
                20,
                Counter("gpu.1.compute", 30)));

        var projected = GpuTelemetryWorkerSupervisor.ProjectCurrentSnapshot(
            current,
            ["gpu.0.compute"]);

        Assert.Equal(2, projected.Adapters.Length);
        var requested = Assert.Single(projected.Adapters[0].Counters);
        Assert.Equal("gpu.0.compute", requested.CounterId);
        Assert.Empty(projected.Adapters[1].Counters);
    }

    private static GpuTelemetryWorkerSnapshot Snapshot(
        GpuTelemetryWorkerStatus status,
        params GpuTelemetryCounterSample[] counters)
        => SnapshotWithAdapters(
            status,
            counters.Length == 0 ? [] : [Adapter(0, 1, counters)]);

    private static GpuTelemetryWorkerSnapshot SnapshotWithAdapters(
        GpuTelemetryWorkerStatus status,
        params GpuTelemetryAdapterSnapshot[] adapters)
        => new(
            GpuTelemetryWorkerSnapshot.CurrentProtocolVersion,
            1,
            DateTimeOffset.UtcNow,
            0,
            status,
            adapters,
            [],
            null);

    private static GpuTelemetryAdapterSnapshot Adapter(
        int index,
        ulong identity,
        params GpuTelemetryCounterSample[] counters)
        => new(index, identity, $"GPU {index}", "10DE", "test", counters);

    private static GpuTelemetryCounterSample Counter(string id, double? value)
        => new(
            id,
            id,
            GpuTelemetryCounterClass.HardwareCounter,
            value,
            "%",
            "test",
            false,
            null);

    private static double? ReadValue(
        GpuTelemetryWorkerSnapshot snapshot,
        string counterId)
        => Assert.Single(snapshot.Adapters)
            .Counters
            .Single(counter => string.Equals(
                counter.CounterId,
                counterId,
                StringComparison.OrdinalIgnoreCase))
            .Value;
}
