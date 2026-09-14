namespace ResourceManager.App.Infrastructure.Persistence;

public enum ResourceManagerDatabaseInitializationDisposition : byte
{
    CreatedNew = 1,
    OpenedExisting = 2,
    RecoveredAfterCorruption = 3
}

public sealed record ResourceManagerDatabaseInitializationState(
    ResourceManagerDatabaseInitializationDisposition Disposition,
    string DatabasePath,
    DateTimeOffset CompletedAt,
    string? QuarantineDatabasePath = null,
    string? RecoveryReason = null);
