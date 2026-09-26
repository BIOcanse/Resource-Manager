using ResourceManager.App.Domain.Migration;

namespace ResourceManager.App.Application.Migration;

public interface ISoftwareDataMigrationManager
{
    MigrationRoots GetRoots();

    Task<IReadOnlyList<SoftwareDataMigrationRecord>> GetRecordsAsync(CancellationToken cancellationToken);

    SoftwareDataMigrationPlan Preview(SoftwareDataMigrationRequest request);

    Task<SoftwareDataMigrationResult> ExecuteAsync(
        SoftwareDataMigrationRequest request,
        CancellationToken cancellationToken);

    Task<SoftwareDataRestoreResult> RestoreAsync(
        SoftwareDataRestoreRequest request,
        CancellationToken cancellationToken);
}
