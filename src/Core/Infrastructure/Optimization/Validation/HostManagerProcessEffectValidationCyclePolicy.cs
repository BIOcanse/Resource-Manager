using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static class HostManagerProcessEffectValidationCyclePolicy
{
    private const ulong BaseScopedEffectMask =
        (1UL << ((int)HostManagerCycleEffectKind.NativeTransactionRecovery - 1)) |
        (1UL << ((int)HostManagerCycleEffectKind.ExitedOwnershipReconciliation - 1)) |
        (1UL << ((int)HostManagerCycleEffectKind.NativeActionTransaction - 1)) |
        (1UL << ((int)HostManagerCycleEffectKind.NativeWorkspaceLifecycle - 1));

    internal static ulong CreateAllowedEffectMask(
        in HostManagerProcessEffectValidationCycleSnapshot scope)
    {
        if (scope.IsProductionUnscoped)
        {
            return ulong.MaxValue;
        }

        var mask = BaseScopedEffectMask;
        if (scope.State == HostManagerProcessEffectValidationScopeState.Active
            && scope.AutomaticMemoryCleanupAllowed)
        {
            mask |= 1UL << ((int)HostManagerCycleEffectKind.AutomaticMemoryCleanup - 1);
        }
        if (scope.State == HostManagerProcessEffectValidationScopeState.Active
            && scope.NonAdaptedMemoryTransactionAllowed)
        {
            mask |= 1UL << ((int)HostManagerCycleEffectKind.NonAdaptedMemoryTransaction - 1);
        }
        return mask;
    }

    internal static bool AllowsCycleEffect(
        in HostManagerProcessEffectValidationCycleSnapshot scope,
        HostManagerCycleEffectKind kind)
    {
        HostManagerCycleEffectPermit.RequireKnown(kind);
        return (CreateAllowedEffectMask(scope) & (1UL << ((int)kind - 1))) != 0;
    }

    internal static bool AllowsNativeAction(
        in HostManagerProcessEffectValidationCycleSnapshot scope,
        NativeSmartCoordinatorActionScope actionScope)
        => scope.IsProductionUnscoped
            || actionScope == NativeSmartCoordinatorActionScope.ProcessPolicy;
}
