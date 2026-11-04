using FreedomPoint;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

/// <summary>Pure host adaptation. Current state and publication stay with the
/// existing coordinator/provider; this class owns no applied configuration.</summary>
internal static class FreedomPointPlanApplication
{
    internal static CompiledRuntimePlan Prepare(CompiledRuntimePlan current, CompiledRuntimePlan candidate)
    {
        var before = current.HostManager.FreedomPoints.SdkFacts;
        if (before is null) return candidate;
        var tree = candidate.HostManager.FreedomPoints;
        // Plans without this SDK's declarations are owned by their original
        // path (including unpublished/default plans); do not synthesize facts.
        if (tree.SdkContractFingerprint.Length == 0) return candidate;
        var registry = tree.SdkRegistry ?? throw new FreedomPointException("candidate_registry_missing");
        var facts = tree.SdkFacts ?? throw new FreedomPointException("candidate_facts_missing");
        return candidate with { FreedomPointPreview = new ConfigurationChange(registry, before, facts) };
    }

    internal static void ValidatePublication(CompiledRuntimePlan current, CompiledRuntimePlan candidate)
    {
        if (candidate.FreedomPointPreview is not { } preview) return;
        var tree = candidate.HostManager.FreedomPoints;
        preview.ValidateBase(tree.SdkRegistry ?? throw new FreedomPointException("candidate_registry_missing"),
            current.HostManager.FreedomPoints.SdkFacts ?? throw new FreedomPointException("current_facts_missing"),
            tree.SdkFacts ?? throw new FreedomPointException("candidate_facts_missing"));
    }
}
