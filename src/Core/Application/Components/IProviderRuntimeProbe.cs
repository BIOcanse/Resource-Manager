namespace ResourceManager.App.Application.Components;

public interface IProviderRuntimeProbe
{
    ComponentProviderRuntimeProbeResult Probe(string componentId);
}

public sealed record ComponentProviderRuntimeProbeResult(
    bool RuntimeAvailable,
    bool InitializeExportAvailable,
    string State,
    string Message,
    string? RuntimePath);
