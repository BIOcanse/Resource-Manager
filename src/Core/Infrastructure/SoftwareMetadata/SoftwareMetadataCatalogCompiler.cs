using System.Globalization;
using System.Text.RegularExpressions;
using ResourceManager.App.Application.SoftwareMetadata;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Domain.SoftwareMetadata;

namespace ResourceManager.App.Infrastructure.SoftwareMetadata;

public sealed partial class SoftwareMetadataCatalogCompiler : ISoftwareMetadataCatalog
{
    public const string EnglishLanguage = "en-US";
    private const int MaximumSummaryLength = 320;
    private const int MaximumDescriptionLength = 4_000;
    private const int MaximumTagCount = 24;
    private const int MaximumTagLength = 64;
    private readonly IReadOnlyDictionary<string, CompiledEntry> entries;

    public SoftwareMetadataCatalogCompiler(
        SoftwareMetadataCatalogDocument document,
        IReadOnlyList<SoftwareIdentityCatalogEntry> identityEntries)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(identityEntries);
        Version = ValidateVersion(document.Version);

        var identities = identityEntries.ToDictionary(
            static entry => RequireIdentifier(entry.Id, "Software identity ID"),
            StringComparer.Ordinal);
        var sources = CompileSources(document.Sources ?? []);
        entries = CompileEntries(document.Entries ?? [], identities, sources);
    }

    public string Version { get; }

    public bool Contains(string softwareIdentityId)
    {
        return !string.IsNullOrWhiteSpace(softwareIdentityId)
            && entries.ContainsKey(softwareIdentityId.Trim());
    }

    public ResolvedSoftwareMetadata? Resolve(string softwareIdentityId, string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(softwareIdentityId);
        var requestedLanguage = NormalizeLanguage(language);
        if (!entries.TryGetValue(softwareIdentityId.Trim(), out var entry))
        {
            return null;
        }

        var resolvedLanguage = entry.Localizations.ContainsKey(requestedLanguage)
            ? requestedLanguage
            : EnglishLanguage;
        var localization = entry.Localizations[resolvedLanguage];
        return new ResolvedSoftwareMetadata(
            entry.Entry.SoftwareIdentityId,
            requestedLanguage,
            resolvedLanguage,
            localization.Summary,
            localization.Description,
            entry.Entry.Publisher,
            entry.Entry.HomepageUrl,
            entry.Entry.SupportUrl,
            entry.Entry.LicenseName,
            entry.Entry.LicenseUrl,
            entry.Tags,
            entry.SourceRefs);
    }

    private static IReadOnlyDictionary<string, SoftwareMetadataCatalogSource> CompileSources(
        IReadOnlyList<SoftwareMetadataCatalogSource> sourceEntries)
    {
        if (sourceEntries.Count == 0)
        {
            throw new InvalidDataException("Software metadata catalog must declare at least one source.");
        }

        var result = new Dictionary<string, SoftwareMetadataCatalogSource>(StringComparer.Ordinal);
        foreach (var source in sourceEntries)
        {
            var id = RequireIdentifier(source.Id, "Software metadata source ID");
            if (!result.TryAdd(id, source with
                {
                    Id = id,
                    Provider = RequireText(source.Provider, "Software metadata source provider", 160),
                    Revision = RequireText(source.Revision, "Software metadata source revision", 160),
                    License = RequireText(source.License, "Software metadata source license", 160),
                    Url = NormalizeOptionalUrl(source.Url, "Software metadata source URL")
                }))
            {
                throw new InvalidDataException($"Duplicate software metadata source ID '{id}'.");
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, CompiledEntry> CompileEntries(
        IReadOnlyList<SoftwareMetadataCatalogEntry> sourceEntries,
        IReadOnlyDictionary<string, SoftwareIdentityCatalogEntry> identities,
        IReadOnlyDictionary<string, SoftwareMetadataCatalogSource> sources)
    {
        if (sourceEntries.Count == 0)
        {
            throw new InvalidDataException("Software metadata catalog must contain at least one entry.");
        }

        var result = new Dictionary<string, CompiledEntry>(StringComparer.Ordinal);
        foreach (var source in sourceEntries)
        {
            var id = RequireIdentifier(source.SoftwareIdentityId, "Software metadata identity ID");
            if (!identities.TryGetValue(id, out var identity))
            {
                throw new InvalidDataException(
                    $"Software metadata entry '{id}' has no matching bundled software identity.");
            }

            ValidateExternalIds(id, source.ExternalIds, identity);
            var sourceRefs = NormalizeSourceRefs(id, source.SourceRefs ?? [], sources);
            var localizations = CompileLocalizations(
                id,
                source.Localizations
                    ?? new Dictionary<string, SoftwareMetadataLocalization>(StringComparer.Ordinal),
                sources);
            var tags = NormalizeTags(id, source.Tags ?? []);
            var entry = source with
            {
                SoftwareIdentityId = id,
                Publisher = NormalizeOptionalText(source.Publisher, 160, $"Publisher for '{id}'"),
                HomepageUrl = NormalizeOptionalUrl(source.HomepageUrl, $"Homepage URL for '{id}'"),
                SupportUrl = NormalizeOptionalUrl(source.SupportUrl, $"Support URL for '{id}'"),
                LicenseName = NormalizeOptionalText(source.LicenseName, 160, $"License name for '{id}'"),
                LicenseUrl = NormalizeOptionalUrl(source.LicenseUrl, $"License URL for '{id}'"),
                Tags = tags,
                Localizations = localizations,
                SourceRefs = sourceRefs
            };

            if (!result.TryAdd(id, new CompiledEntry(entry, localizations, tags, sourceRefs)))
            {
                throw new InvalidDataException($"Duplicate software metadata identity ID '{id}'.");
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, SoftwareMetadataLocalization> CompileLocalizations(
        string identityId,
        IReadOnlyDictionary<string, SoftwareMetadataLocalization> sourceLocalizations,
        IReadOnlyDictionary<string, SoftwareMetadataCatalogSource> sources)
    {
        var result = new Dictionary<string, SoftwareMetadataLocalization>(StringComparer.Ordinal);
        foreach (var pair in sourceLocalizations)
        {
            var language = NormalizeLanguage(pair.Key);
            var sourceRef = RequireIdentifier(pair.Value.SourceRef, $"Localization source for '{identityId}'");
            if (!sources.ContainsKey(sourceRef))
            {
                throw new InvalidDataException(
                    $"Software metadata entry '{identityId}' references unknown localization source '{sourceRef}'.");
            }

            var localization = pair.Value with
            {
                Summary = RequireText(
                    pair.Value.Summary,
                    $"Summary for '{identityId}'/{language}",
                    MaximumSummaryLength),
                Description = NormalizeOptionalText(
                    pair.Value.Description,
                    MaximumDescriptionLength,
                    $"Description for '{identityId}'/{language}"),
                SourceRef = sourceRef
            };
            if (!result.TryAdd(language, localization))
            {
                throw new InvalidDataException(
                    $"Software metadata entry '{identityId}' has duplicate language '{language}'.");
            }
        }

        if (!result.ContainsKey(EnglishLanguage))
        {
            throw new InvalidDataException(
                $"Software metadata entry '{identityId}' must contain '{EnglishLanguage}'.");
        }
        return result;
    }

    private static IReadOnlyList<string> NormalizeSourceRefs(
        string identityId,
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, SoftwareMetadataCatalogSource> sources)
    {
        var result = new List<string>(sourceRefs.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in sourceRefs)
        {
            var sourceRef = RequireIdentifier(value, $"Source reference for '{identityId}'");
            if (!sources.ContainsKey(sourceRef))
            {
                throw new InvalidDataException(
                    $"Software metadata entry '{identityId}' references unknown source '{sourceRef}'.");
            }
            if (seen.Add(sourceRef))
            {
                result.Add(sourceRef);
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException(
                $"Software metadata entry '{identityId}' must reference at least one source.");
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> NormalizeTags(
        string identityId,
        IReadOnlyList<string> sourceTags)
    {
        if (sourceTags.Count > MaximumTagCount)
        {
            throw new InvalidDataException(
                $"Software metadata entry '{identityId}' exceeds {MaximumTagCount} tags.");
        }

        var result = new List<string>(sourceTags.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in sourceTags)
        {
            var tag = RequireText(value, $"Tag for '{identityId}'", MaximumTagLength);
            if (!seen.Add(tag))
            {
                throw new InvalidDataException(
                    $"Software metadata entry '{identityId}' contains duplicate tag '{tag}'.");
            }
            result.Add(tag);
        }
        return result.AsReadOnly();
    }

    private static void ValidateExternalIds(
        string identityId,
        SoftwareMetadataExternalIds? externalIds,
        SoftwareIdentityCatalogEntry identity)
    {
        if (externalIds is null)
        {
            return;
        }

        var knownPackages = new HashSet<string>(
            identity.PackageIdentifiers ?? [],
            StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in externalIds.PackageIdentifiers ?? [])
        {
            var value = RequireText(packageId, $"Package identifier for '{identityId}'", 256);
            if (!knownPackages.Contains(value))
            {
                throw new InvalidDataException(
                    $"Software metadata entry '{identityId}' declares package ID '{value}' outside its identity entry.");
            }
        }

        var knownSteamIds = new HashSet<long>((identity.SteamAppIds ?? []).Select(static value => (long)value));
        foreach (var steamAppId in externalIds.SteamAppIds ?? [])
        {
            if (steamAppId <= 0 || !knownSteamIds.Contains(steamAppId))
            {
                throw new InvalidDataException(
                    $"Software metadata entry '{identityId}' declares invalid Steam AppID '{steamAppId}'.");
            }
        }

        foreach (var wikidataId in externalIds.WikidataIds ?? [])
        {
            var value = RequireText(wikidataId, $"Wikidata ID for '{identityId}'", 32);
            if (!WikidataIdPattern().IsMatch(value))
            {
                throw new InvalidDataException(
                    $"Software metadata entry '{identityId}' declares invalid Wikidata ID '{value}'.");
            }
        }
    }

    private static string NormalizeLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        try
        {
            return CultureInfo.GetCultureInfo(language.Trim()).Name;
        }
        catch (CultureNotFoundException ex)
        {
            throw new ArgumentException($"Unsupported language tag '{language}'.", nameof(language), ex);
        }
    }

    private static string ValidateVersion(string? version)
    {
        var value = RequireText(version, "Software metadata catalog version", 32);
        if (!System.Version.TryParse(value, out var parsed)
            || parsed.Major < 1
            || parsed.Build < 0
            || parsed.Revision >= 0)
        {
            throw new InvalidDataException(
                "Software metadata catalog version must use the '1.0.0' format.");
        }
        return value;
    }

    private static string RequireIdentifier(string? value, string field)
    {
        var result = RequireText(value, field, 128);
        if (!IdentifierPattern().IsMatch(result))
        {
            throw new InvalidDataException($"{field} '{result}' is not a valid stable identifier.");
        }
        return result;
    }

    private static string RequireText(string? value, string field, int maximumLength)
    {
        var result = value?.Trim();
        if (string.IsNullOrWhiteSpace(result) || result.Length > maximumLength)
        {
            throw new InvalidDataException(
                $"{field} must contain 1..{maximumLength} characters.");
        }
        return result;
    }

    private static string? NormalizeOptionalText(string? value, int maximumLength, string field)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : RequireText(value, field, maximumLength);
    }

    private static string? NormalizeOptionalUrl(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var result = value.Trim();
        if (result.Length > 2_048
            || !Uri.TryCreate(result, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidDataException($"{field} must be an absolute HTTP(S) URL.");
        }
        return result;
    }

    private sealed record CompiledEntry(
        SoftwareMetadataCatalogEntry Entry,
        IReadOnlyDictionary<string, SoftwareMetadataLocalization> Localizations,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> SourceRefs);

    [GeneratedRegex("^[a-z][a-z0-9-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^Q[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex WikidataIdPattern();
}
