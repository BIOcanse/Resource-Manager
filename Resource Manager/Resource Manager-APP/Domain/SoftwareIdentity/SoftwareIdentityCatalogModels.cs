namespace ResourceManager.App.Domain.SoftwareIdentity;

public sealed record SoftwareIdentityCatalogDocument(
    string Version,
    IReadOnlyList<SoftwareIdentityCatalogEntry> Entries);

public sealed record SoftwareIdentityCatalogEntry(
    string Id,
    string DisplayName,
    string Kind,
    string Source,
    IReadOnlyList<int>? SteamAppIds = null,
    IReadOnlyList<string>? PackageIdentifiers = null,
    IReadOnlyList<string>? InstalledNames = null,
    IReadOnlyList<string>? ExecutableNames = null,
    IReadOnlyList<string>? ProductNames = null);

public enum PortableSoftwareIdentityConfidence : byte
{
    Candidate = 0,
    Confirmed = 1
}

public sealed record PortableSoftwareIdentityMatch(
    SoftwareIdentityCatalogEntry Entry,
    PortableSoftwareIdentityConfidence Confidence,
    IReadOnlyList<string> Evidence);
