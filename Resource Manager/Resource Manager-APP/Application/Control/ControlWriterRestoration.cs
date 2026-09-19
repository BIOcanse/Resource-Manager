using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Application.Control;

internal static class ControlWriterRestoration
{
    internal static async Task<bool> RestoreDefaultAsync(
        IControlWriter writer, ControlObject target, ControlCapability capability, CancellationToken cancellationToken)
    {
        if (capability.Range?.DefaultValue is not { } value) return false;
        var result = await writer.WriteAsync(target, capability,
            new ControlSetting(capability.Id, Number: value, Unit: capability.Range.Unit), cancellationToken)
            .ConfigureAwait(false);
        return result.Status == ControlApplyStatuses.Applied;
    }
}
