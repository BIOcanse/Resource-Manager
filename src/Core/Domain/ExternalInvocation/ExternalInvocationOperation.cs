namespace ResourceManager.App.Domain.ExternalInvocation;

public sealed record ExternalInvocationModuleDescriptor(
    string ModuleId,
    string Version,
    string Description,
    bool EnabledByDefault = true);

public sealed record ExternalInvocationOperationDescriptor(
    string OperationId,
    string ModuleId,
    string Version,
    ExternalInvocationAccessClass AccessClass,
    string ContractInterfaceName,
    ExternalInvocationOperationCategory Category,
    ExternalInvocationRiskLevel RiskLevel,
    string Description,
    string RequestSchemaId,
    string ResponseSchemaId,
    bool EnabledByDefault = true);

public sealed record ExternalInvocationModuleAvailability(
    bool Available,
    string? Reason)
{
    public static ExternalInvocationModuleAvailability Ready { get; } = new(true, null);

    public static ExternalInvocationModuleAvailability Unavailable(string reason)
        => new(false, reason);
}

public sealed record ExternalInvocationCatalogOperation(
    ExternalInvocationOperationDescriptor Operation,
    bool Enabled,
    bool Available,
    string? UnavailableReason);

public sealed record ExternalInvocationCatalogModule(
    ExternalInvocationModuleDescriptor Module,
    bool Enabled,
    ExternalInvocationModuleAvailability Availability,
    IReadOnlyList<ExternalInvocationCatalogOperation> Operations);

public sealed record ExternalInvocationCatalogSnapshot(
    string SchemaVersion,
    IReadOnlyList<ExternalInvocationCatalogModule> Modules,
    DateTimeOffset CapturedAt);
