namespace ResourceManager.App.Domain.SoftwareMetadata;

public sealed record SoftwareMetadataCatalogDocument(
    string Version,
    IReadOnlyList<SoftwareMetadataCatalogSource> Sources,
    IReadOnlyList<SoftwareMetadataCatalogEntry> Entries);

public sealed record SoftwareMetadataCatalogSource(
    string Id,
    string Provider,
    string Revision,
    string License,
    string? Url = null);

public sealed record SoftwareMetadataExternalIds(
    IReadOnlyList<string>? PackageIdentifiers = null,
    IReadOnlyList<long>? SteamAppIds = null,
    IReadOnlyList<string>? WikidataIds = null);

public sealed record SoftwareMetadataLocalization(
    string Summary,
    string? Description,
    string SourceRef);

public sealed record SoftwareMetadataCatalogEntry(
    string SoftwareIdentityId,
    SoftwareMetadataExternalIds? ExternalIds,
    string? Publisher,
    string? HomepageUrl,
    string? SupportUrl,
    string? LicenseName,
    string? LicenseUrl,
    IReadOnlyList<string>? Tags,
    IReadOnlyDictionary<string, SoftwareMetadataLocalization> Localizations,
    IReadOnlyList<string> SourceRefs);

public sealed record ResolvedSoftwareMetadata(
    string SoftwareIdentityId,
    string RequestedLanguage,
    string ResolvedLanguage,
    string Summary,
    string? Description,
    string? Publisher,
    string? HomepageUrl,
    string? SupportUrl,
    string? LicenseName,
    string? LicenseUrl,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> SourceRefs);

public sealed record SoftwareMetadataLookupResult(
    string CatalogVersion,
    bool Found,
    ResolvedSoftwareMetadata? Metadata);
