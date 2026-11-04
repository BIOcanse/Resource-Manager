using System.Collections.Frozen;

namespace ResourceManager.App.Domain.ExternalInvocation;

public sealed class ExternalInvocationRuntimePlan
{
    private ExternalInvocationRuntimePlan(
        bool enabled,
        FrozenSet<string> disabledModules,
        FrozenSet<string> disabledOperations)
    {
        Enabled = enabled;
        DisabledModules = disabledModules;
        DisabledOperations = disabledOperations;
    }

    public bool Enabled { get; }

    public IReadOnlySet<string> DisabledModules { get; }

    public IReadOnlySet<string> DisabledOperations { get; }

    public static ExternalInvocationRuntimePlan Default { get; } = Compile(enabled: true);

    public static ExternalInvocationRuntimePlan Compile(
        bool enabled,
        IEnumerable<string>? disabledModules = null,
        IEnumerable<string>? disabledOperations = null)
    {
        return new ExternalInvocationRuntimePlan(
            enabled,
            Normalize(disabledModules),
            Normalize(disabledOperations));
    }

    public bool IsModuleEnabled(ExternalInvocationModuleDescriptor descriptor)
        => Enabled
            && descriptor.EnabledByDefault
            && !DisabledModules.Contains(descriptor.ModuleId);

    public bool IsOperationEnabled(ExternalInvocationOperationDescriptor descriptor)
        => descriptor.EnabledByDefault
            && !DisabledOperations.Contains(descriptor.OperationId);

    private static FrozenSet<string> Normalize(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToFrozenSet(StringComparer.Ordinal);
    }
}
