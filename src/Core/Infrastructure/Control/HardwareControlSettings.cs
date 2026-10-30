using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// Build-time choices of the hardware helpers and control writers (freedom-point table hardware_bridge).
/// The helper clients, the command queue, the fan curve feed and the EC channel are constructed before the
/// first runtime plan publication, so they read <see cref="ProductDefaults"/>; the plan compiler consumes
/// the same declarations through <see cref="Compile"/>.
/// </summary>
internal static class HardwareControlSettings
{
    private const string Consumer = "HardwareControlSettings.Compile";

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static CompiledHardwareControlSettings ProductDefaults { get; } = Compile(EmbeddedFreedomPointDefaults.Begin());

    internal static CompiledHardwareControlSettings Compile(FreedomPointCompilation freedom)
    {
        ArgumentNullException.ThrowIfNull(freedom);
        var requestTimeout = freedom.Consume<int>(BackendFreedomPointPaths.HardwareHelperRequestTimeout, Consumer);
        if (requestTimeout <= 0)
            throw new InvalidDataException("The hardware helper request timeout must be a positive integer.");
        return new(TimeSpan.FromMilliseconds(requestTimeout),
            freedom.Consume<HardwareHelperProcessTiming>(BackendFreedomPointPaths.HardwareHelperProcessTiming, Consumer).Validate(),
            freedom.Consume<FanCurveFeedTiming>(BackendFreedomPointPaths.FanCurveFeed, Consumer).Validate(),
            freedom.Consume<UniwillEcChannelOptions>(BackendFreedomPointPaths.UniwillEcChannel, Consumer).Validate());
    }
}
