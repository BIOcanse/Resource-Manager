namespace ResourceManager.App.Domain.RuntimeSpecialization;

// Backend provider configuration. Observation demand belongs to each active subscription.
public sealed record CompiledMonitoringPlan(
    IReadOnlyList<string> RegisteredSourceZoneIds,
    CompiledFirmwareProviderPlan FirmwareProviders)
{
    public static CompiledMonitoringPlan Default { get; } = new([], CompiledFirmwareProviderPlan.Default);
}