using ResourceManager.App.Application.Monitoring;

namespace ResourceManager.App.Endpoints.Transport;

public sealed record GpuTelemetryWireSnapshot(
    int Version,
    DateTimeOffset CapturedAt,
    IReadOnlyList<GpuTelemetryAdapterSnapshot> Adapters)
{
    public const int CurrentVersion = 2;

    public static GpuTelemetryWireSnapshot From(
        GpuTelemetryWorkerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new GpuTelemetryWireSnapshot(
            CurrentVersion,
            snapshot.CapturedAt,
            snapshot.Adapters);
    }
}
