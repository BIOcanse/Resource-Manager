namespace ResourceManager.App.Domain.Indexing;

public sealed record SoftwareFileIndexRoot(
    string Path,
    string Kind);

public sealed record SoftwareFileIndexRequest(
    string SoftwareId,
    string SoftwareName,
    IReadOnlyList<SoftwareFileIndexRoot> Roots);

public sealed record SoftwareFileIndexRootSnapshot(
    string Path,
    string Kind,
    long TotalBytes,
    long FileCount,
    DateTimeOffset? LastIndexedAt);

public sealed record SoftwareFileIndexSnapshot(
    string SoftwareId,
    string SoftwareName,
    IReadOnlyList<SoftwareFileIndexRootSnapshot> Roots,
    long TotalBytes,
    long FileCount,
    DateTimeOffset? LastIndexedAt);

public sealed record SoftwareFileSearchResult(
    long EntryId,
    string SoftwareId,
    string SoftwareName,
    string RootPath,
    string RelativePath,
    string FullPath,
    string FileName,
    string Extension,
    long SizeBytes,
    DateTimeOffset LastWriteAt);

public sealed record SoftwareFileIndexStatistics(
    long SoftwareCount,
    long RootCount,
    long FileCount,
    long TotalBytes,
    DateTimeOffset? OldestIndexedAt,
    DateTimeOffset? LatestIndexedAt);
