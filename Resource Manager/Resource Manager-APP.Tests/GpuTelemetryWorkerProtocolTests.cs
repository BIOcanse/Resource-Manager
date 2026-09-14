using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring.GpuTelemetry;

namespace Resource_Manager_APP.Tests;

public sealed class GpuTelemetryWorkerProtocolTests
{
    [Fact]
    public void RequestRoundTrips_WithStableCounterOrder()
    {
        var request = GpuTelemetryWorkerRequest.Create(
            ["gpu.1.tensor.active", "gpu.0.engine.compute", "gpu.1.tensor.active"],
            GpuTelemetryWorkerDetailLevel.AdvancedCounters,
            500);

        var json = GpuTelemetryWorkerProtocol.SerializeRequest(request);
        var roundTrip = GpuTelemetryWorkerProtocol.DeserializeRequest(json);

        Assert.Equal(GpuTelemetryWorkerRequest.CurrentProtocolVersion, roundTrip.ProtocolVersion);
        Assert.Equal(GpuTelemetryWorkerDetailLevel.AdvancedCounters, roundTrip.DetailLevel);
        Assert.Equal(500, roundTrip.TimeoutMilliseconds);
        Assert.Equal(["gpu.0.engine.compute", "gpu.1.tensor.active"], roundTrip.CounterIds);
    }

    [Fact]
    public void SnapshotRoundTrips_WithProviderAndCounterMetadata()
    {
        var snapshot = new GpuTelemetryWorkerSnapshot(
            GpuTelemetryWorkerSnapshot.CurrentProtocolVersion,
            7,
            DateTimeOffset.Parse("2026-07-08T12:00:00Z"),
            42,
            GpuTelemetryWorkerStatus.Online,
            [
                new GpuTelemetryAdapterSnapshot(
                    1,
                    1,
                    "NVIDIA GeForce RTX 5060 Laptop GPU",
                    "nvidia",
                    "blackwell",
                    [
                        new GpuTelemetryCounterSample(
                            "gpu.1.tensor.active",
                            "Tensor active",
                            GpuTelemetryCounterClass.ProfilerMetric,
                            12.5,
                            "%",
                            "nvidia.cupti",
                            true,
                            null)
                    ])
            ],
            [
                new GpuTelemetryProviderState(
                    "nvidia.cupti",
                    GpuTelemetryWorkerStatus.Online,
                    null,
                    true,
                    true,
                    3)
            ],
            null);

        var json = GpuTelemetryWorkerProtocol.SerializeSnapshot(snapshot);
        var roundTrip = GpuTelemetryWorkerProtocol.DeserializeSnapshot(json);

        Assert.Equal(7, roundTrip.Sequence);
        Assert.Single(roundTrip.Adapters);
        Assert.Single(roundTrip.Adapters[0].Counters);
        Assert.True(roundTrip.Adapters[0].Counters[0].IsIntrusive);
        Assert.Equal("nvidia.cupti", roundTrip.Providers[0].ProviderId);
    }
}
