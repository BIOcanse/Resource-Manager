using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private async Task<HostManagerRollbackStateDocument> RestorePlacementStateCoreAsync(
        HostManagerCycleEffectPermit permit,
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.LegacyPlacementRestore);
        var source = await LoadRollbackStateAsync(cancellationToken);
        if (!GpuActionFacts.HasPlacementEffects(source.AppliedPlacements)) return source;
        var remainingPlacements = RestoreAppliedPlacementsThroughNative(
            permit,
            source.AppliedPlacements,
            cancellationToken);
        var attemptedAt = DateTimeOffset.UtcNow;
        var remainingEffects = GpuActionFacts.PlacementEffects(remainingPlacements).Sum(static item => item.Records.Count);
        var restoredCount = GpuActionFacts.PlacementEffects(source.AppliedPlacements).Sum(static item => item.Records.Count) - remainingEffects;
        var retainedActions = remainingPlacements.Sum(static item => item.Records.Count) - remainingEffects;
        var message = $"Host Manager restored {restoredCount} placement effects; {remainingEffects} effects remain pending; "
            + $"{retainedActions} window action facts remain recorded.";
        var checkpoint = source with
        {
            LastRestoreAt = attemptedAt,
            Message = message,
            AppliedPlacements = remainingPlacements
        };
        await SaveRollbackStateAsync(checkpoint, CancellationToken.None);
        return checkpoint;
    }
}
