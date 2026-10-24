using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record RuntimePolicySaveResult<T>(
    T Policy,
    GpuLaunchInterceptionStatus? StartupInterception,
    string RuntimeApplicationDisposition,
    string StartupInterceptionDisposition,
    long? RuntimePlanVersion,
    ulong? RuntimePublicationSequence,
    int RuntimeDeliveryFailureCount,
    string? FailureCode);

