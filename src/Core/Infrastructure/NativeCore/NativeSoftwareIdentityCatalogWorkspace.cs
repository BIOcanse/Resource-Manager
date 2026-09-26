using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeSoftwareIdentityCatalogWorkspace : IDisposable
{
    public NativeSoftwareIdentityCatalogWorkspace(
        CompiledHostManagerSoftwareIdentityCatalogPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException("Software identity catalog plan is not published.");
        }

        var configuration = CreateConfiguration(plan);
        Session = new NativeSoftwareIdentityCatalogSession(in configuration);
    }

    public NativeSoftwareIdentityCatalogSession Session { get; }

    public void Dispose() => Session.Dispose();

    internal static unsafe NativeSoftwareIdentityCatalogConfiguration CreateConfiguration(
        CompiledHostManagerSoftwareIdentityCatalogPlan plan)
    {
        var capacity = plan.Recreate.Capacity;
        var hot = plan.HotPublish;
        var configuration = new NativeSoftwareIdentityCatalogConfiguration
        {
            AbiVersion = plan.Build.AbiVersion,
            StructSize = checked((uint)Unsafe.SizeOf<NativeSoftwareIdentityCatalogConfiguration>()),
            Generation = plan.ConfigurationGeneration,
            MaximumEntryCount = checked((uint)capacity.MaximumEntryCount),
            MaximumAliasCount = checked((uint)capacity.MaximumAliasCount),
            MaximumRootCount = checked((uint)capacity.MaximumRootCount),
            MaximumCatalogKeyByteCount = checked((uint)capacity.MaximumCatalogKeyByteCount),
            MaximumQueryFactCount = checked((uint)capacity.MaximumQueryFactCount),
            MaximumQuerySignalCount = checked((uint)capacity.MaximumQuerySignalCount),
            MaximumQueryKeyByteCount = checked((uint)capacity.MaximumQueryKeyByteCount),
            EntryIndexCapacity = checked((uint)capacity.EntryIndexCapacity),
            AliasIndexCapacity = checked((uint)capacity.AliasIndexCapacity),
            IdentityIndexCapacity = checked((uint)capacity.IdentityIndexCapacity),
            RootIndexCapacity = checked((uint)capacity.RootIndexCapacity),
            RequiredProhibitedAliasCount = checked((uint)plan.Recreate.ProhibitedExecutableAliases.Length),
            RequiredLauncherTokenCount = checked((uint)plan.Recreate.LauncherTokens.Length),
            RequiredManagedChildRuleCount = checked((uint)plan.Recreate.ManagedChildSegments.Length),
            MinimumContainsKeyLength = checked((uint)hot.MinimumContainsKeyLength),
            ReservedCapacity = 0,
            ResidentByteBudget = checked((ulong)hot.ResidentByteBudget),
            ExactTextScore = hot.ExactTextScore,
            ContainsTextScore = hot.ContainsTextScore,
            IdentityMinimumScore = hot.IdentityMinimumScore,
            StrongEvidenceMinimumScore = hot.StrongEvidenceMinimumScore,
            RootHitBonus = hot.RootHitBonus,
            QueryLauncherMatchBonus = hot.QueryLauncherMatchBonus,
            QueryLauncherNonmatchPenalty = hot.QueryLauncherNonmatchPenalty,
            RootLauncherMatchBonus = hot.RootLauncherMatchBonus,
            RootLauncherNonmatchPenalty = hot.RootLauncherNonmatchPenalty,
            RootRejectScore = hot.RootRejectScore,
            StrongSignalMask = hot.StrongSignalMask,
            RootSignalMask = hot.RootSignalMask,
            Flags = hot.LauncherNonEntryRequiresRoot
                ? (ulong)NativeSoftwareIdentityCatalogConfigurationFlags.LauncherNonEntryRequiresRoot
                : 0
        };
        for (var index = 0; index < NativeSoftwareIdentityCatalogAbi.SignalCount; index++)
        {
            configuration.SignalWeights[index] = hot.SignalWeights[index];
            configuration.RootSignalWeights[index] = hot.RootSignalWeights[index];
        }
        return configuration;
    }
}
