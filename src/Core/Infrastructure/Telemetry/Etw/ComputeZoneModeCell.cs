using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>A compute zone's "unfrozen" item. Its owner claims it at construction, when the zone's mode is Normal.</summary>
internal static class ComputeZoneModeCell
{
    internal static StateCell<bool> ClaimUnfrozen(StateAuthority authority, int uid)
    {
        var cell = authority.ClaimCell<bool>(uid);
        _ = cell.PublishAsync(true, new(BusinessBlockRequestKind.Configuration, "compute-zone", "initial", 0));
        return cell;
    }
}
