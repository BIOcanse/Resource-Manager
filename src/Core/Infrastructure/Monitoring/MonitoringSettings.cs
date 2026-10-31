using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.Monitoring;

/// <summary>
/// Build-time monitoring choices (freedom-point table monitoring). The performance overlay channel and
/// targeted recording read <see cref="ProductDefaults"/>; the VidMm, GPU allocation and Present
/// session owners read their buffer pools from the published plan; the plan compiler
/// consumes the same declarations through <see cref="Compile"/>.
/// </summary>
internal static class MonitoringSettings
{
    private const string Consumer = "MonitoringSettings.Compile";

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static CompiledMonitoringSettings ProductDefaults { get; } = Compile(EmbeddedFreedomPointDefaults.Begin());

    internal static CompiledMonitoringSettings Compile(FreedomPointCompilation freedom)
    {
        ArgumentNullException.ThrowIfNull(freedom);
        var overlayFrameWindow = freedom.Consume<int>(BackendFreedomPointPaths.OverlayFrameWindow, Consumer);
        if (overlayFrameWindow <= 0)
            throw new InvalidDataException("The performance overlay frame window must be a positive integer.");
        return new(TimeSpan.FromMilliseconds(overlayFrameWindow),
            freedom.Consume<TargetedRecordingOptions>(BackendFreedomPointPaths.TargetedRecording, Consumer).Validate(),
            SessionBuffers(freedom, BackendFreedomPointPaths.VidMmSessionBuffers),
            SessionBuffers(freedom, BackendFreedomPointPaths.GpuAllocationSessionBuffers),
            SessionBuffers(freedom, BackendFreedomPointPaths.PresentSessionBuffers),
            freedom.Consume<PerformanceOverlayOptions>(BackendFreedomPointPaths.PerformanceOverlay, Consumer).ToDefaults(),
            freedom.Consume<FramePipelineOptions>(BackendFreedomPointPaths.FramePipeline, Consumer).Validate());
    }

    private static EtwSessionBuffers SessionBuffers(FreedomPointCompilation freedom, string path)
        => freedom.Consume<EtwSessionBufferPoolOptions>(path, Consumer).ToSessionBuffers();
}
