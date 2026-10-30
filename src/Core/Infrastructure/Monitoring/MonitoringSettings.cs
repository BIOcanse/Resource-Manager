using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.Monitoring;

/// <summary>
/// Build-time monitoring choices (freedom-point table monitoring). The service identity resolver, the
/// performance overlay channel and targeted recording read <see cref="ProductDefaults"/>; the plan compiler
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
        var serviceIdentityCache = freedom.Consume<int>(BackendFreedomPointPaths.ServiceIdentityCacheLifetime, Consumer);
        if (serviceIdentityCache < 0)
            throw new InvalidDataException("The service identity cache lifetime must be nonnegative.");
        var overlayFrameWindow = freedom.Consume<int>(BackendFreedomPointPaths.OverlayFrameWindow, Consumer);
        if (overlayFrameWindow <= 0)
            throw new InvalidDataException("The performance overlay frame window must be a positive integer.");
        return new(TimeSpan.FromMilliseconds(serviceIdentityCache), TimeSpan.FromMilliseconds(overlayFrameWindow),
            freedom.Consume<TargetedRecordingOptions>(BackendFreedomPointPaths.TargetedRecording, Consumer).Validate());
    }
}
