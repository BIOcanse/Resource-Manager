namespace ResourceManager.App.Hosting.StartupCapabilities;

[Flags]
public enum StartupCapability : uint
{
    None = 0,
    MutablePersistence = 1 << 0,
    LegacyPersistenceImport = 1 << 1,
    GpuLaunchInterceptionReconciliation = 1 << 2,
    RuntimeEffectOwners = 1 << 3,
    PublicServiceCoordination = 1 << 4,
    OptimizationRuntime = 1 << 5,
    SharedResourceOwnership = 1 << 6
}

public sealed record StartupCapabilitySet(
    string ProfileId,
    StartupCapability Allowed)
{
    public const string NormalReadOnlyProfileId = "normal-read-only";
    public const string FullProfileId = "full";

    public static StartupCapabilitySet NormalReadOnly { get; } = new(
        NormalReadOnlyProfileId,
        StartupCapability.None);

    public static StartupCapabilitySet Full { get; } = new(
        FullProfileId,
        StartupCapability.MutablePersistence
        | StartupCapability.LegacyPersistenceImport
        | StartupCapability.GpuLaunchInterceptionReconciliation
        | StartupCapability.RuntimeEffectOwners
        | StartupCapability.PublicServiceCoordination
        | StartupCapability.OptimizationRuntime
        | StartupCapability.SharedResourceOwnership);

    public bool Allows(StartupCapability capability)
        => capability != StartupCapability.None
            && (Allowed & capability) == capability;
}
