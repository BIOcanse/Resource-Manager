namespace ResourceManager.App.Domain.Updates;

public sealed record ProductVersionOption(
    string Choice,
    string Version,
    string Series,
    string Channel,
    DateTimeOffset? PublishedAt,
    string AssetName,
    string DownloadUrl,
    string ChecksumUrl,
    bool Selectable,
    string? UnavailableReason);

public sealed record ProductVersionCatalog(
    string ItemId,
    string Role,
    string? InstalledVersion,
    string Status,
    bool Complete,
    DateTimeOffset CheckedAt,
    IReadOnlyList<ProductVersionOption> Options,
    string? Error);
