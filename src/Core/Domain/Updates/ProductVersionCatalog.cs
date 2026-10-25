using System.Text.Json.Serialization;

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
    string Eligibility);

public static class ProductVersionEligibility
{
    public const string Eligible = "eligible";
    public const string InstalledVersionUnknown = "installedVersionUnknown";
    public const string NotNewer = "notNewer";
}

public sealed record ProductVersionCatalog(
    string ItemId,
    string Role,
    string? InstalledVersion,
    string Status,
    bool Complete,
    DateTimeOffset CheckedAt,
    IReadOnlyList<ProductVersionOption> Options,
    [property: JsonIgnore] string? Error)
{
    public string? ErrorCode => Error is null ? null : "update.catalogUnavailable";
}
