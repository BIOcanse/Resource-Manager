namespace ResourceManager.App.Domain.Migration;

public sealed record MigrationRoots(
    string UserDataRoot,
    string MiscRoot,
    string DependenciesRoot,
    string ManagedSoftwareRoot);

public sealed record SoftwareDataMigrationRequest(
    string SoftwareName,
    IReadOnlyList<string> SourcePaths,
    string TargetCategory,
    string MigrationKind = "Data",
    bool ConfirmExecution = false,
    bool AllowMediumRisk = false);

public sealed record SoftwareDataMigrationPlan(
    string SoftwareName,
    string TargetCategory,
    string MigrationKind,
    string TargetRoot,
    bool CanExecute,
    string Summary,
    IReadOnlyList<SoftwareDataMigrationItem> Items);

public sealed record SoftwareDataMigrationItem(
    string Id,
    string SourcePath,
    string DestinationPath,
    string BackupPath,
    bool Exists,
    bool IsDirectory,
    long? SizeBytes,
    string Risk,
    string Classification,
    bool CanExecute,
    string Message);

public sealed record SoftwareDataMigrationResult(
    SoftwareDataMigrationPlan Plan,
    IReadOnlyList<SoftwareDataMigrationActionResult> Results);

public sealed record SoftwareDataMigrationActionResult(
    string SourcePath,
    string DestinationPath,
    string BackupPath,
    string State,
    string Message);

public sealed record SoftwareDataMigrationRecord(
    string Id,
    string SoftwareName,
    string TargetCategory,
    string MigrationKind,
    string SourcePath,
    string DestinationPath,
    string BackupPath,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RestoredAt);

public sealed record SoftwareDataRestoreRequest(
    string Id,
    bool ConfirmExecution = false);

public sealed record SoftwareDataRestoreResult(
    SoftwareDataMigrationRecord Record,
    string State,
    string Message);
