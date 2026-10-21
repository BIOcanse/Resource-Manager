using ResourceManager.App.Application.Indexing;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.ReadWrite.Windows.Files.Indexing;

namespace ResourceManager.App.Infrastructure.Indexing;

// Product database, root/refresh decisions and logging remain Host; one SDK algorithm/pool is borrowed.
public sealed class SqliteSoftwareFileIndex(ResourceManagerDatabase database,
    INativeFileQueryLeaseSource fileQuery, ILogger<SqliteSoftwareFileIndex> logger)
    : SqliteIndexedFileOwner(IndexedFileDatabase.Borrow(database.DatabasePath, database.OpenConnectionAsync), fileQuery,
        (error, softwareId, rootPath) => logger.LogWarning(error,
            "Software file index refresh failed for {SoftwareId} root {RootPath}; retaining the last complete snapshot.", softwareId, rootPath)),
        ISoftwareFileIndex, ISoftwareFileIndexRefresher { }
