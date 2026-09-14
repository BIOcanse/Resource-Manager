namespace ResourceManager.App.Application.Monitoring;

public interface IGpuTelemetryWorkerClient
{
    GpuTelemetryWorkerSnapshot GetLatestSnapshot();
}
