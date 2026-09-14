using System.Text.Json;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Domain.SoftwareMetadata;
using ResourceManager.App.Infrastructure.SoftwareMetadata;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareMetadataCatalogCompilerTests
{
    [Fact]
    public void BundledCatalog_CompilesWithEnglishAndChineseForEveryEntry()
    {
        var metadataDocument = ReadBundledDocument<SoftwareMetadataCatalogDocument>(
            "SoftwareMetadata",
            "software-metadata.v1.json");
        var identityDocument = ReadBundledDocument<SoftwareIdentityCatalogDocument>(
            "SoftwareIdentity",
            "software-identities.v1.json");

        var catalog = new SoftwareMetadataCatalogCompiler(
            metadataDocument,
            identityDocument.Entries);

        Assert.Equal(72, metadataDocument.Entries.Count);
        Assert.Equal(
            [
                "resource-manager-curation",
                "software-identity-catalog",
                "wikidata",
                "winget-community"
            ],
            metadataDocument.Sources
                .Select(static source => source.Id)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray());
        Assert.All(metadataDocument.Entries, entry =>
        {
            Assert.True(entry.Localizations.ContainsKey("en-US"));
            Assert.True(entry.Localizations.ContainsKey("zh-CN"));
            Assert.True(catalog.Contains(entry.SoftwareIdentityId));
        });
    }

    [Fact]
    public void Resolve_UsesExactCurrentLanguageThenEnglishOnly()
    {
        var catalog = CreateCatalog(
            new Dictionary<string, SoftwareMetadataLocalization>(StringComparer.Ordinal)
            {
                ["en-US"] = new("English summary.", null, "curated"),
                ["zh-CN"] = new("中文说明。", null, "curated")
            });

        var chinese = Assert.IsType<ResolvedSoftwareMetadata>(
            catalog.Resolve("app-test", "zh-CN"));
        var fallback = Assert.IsType<ResolvedSoftwareMetadata>(
            catalog.Resolve("app-test", "fr-FR"));

        Assert.Equal("zh-CN", chinese.RequestedLanguage);
        Assert.Equal("zh-CN", chinese.ResolvedLanguage);
        Assert.Equal("中文说明。", chinese.Summary);
        Assert.Equal("fr-FR", fallback.RequestedLanguage);
        Assert.Equal("en-US", fallback.ResolvedLanguage);
        Assert.Equal("English summary.", fallback.Summary);
    }

    [Fact]
    public void Constructor_RejectsEntryWithoutEnglishFallback()
    {
        Assert.Throws<InvalidDataException>(() => CreateCatalog(
            new Dictionary<string, SoftwareMetadataLocalization>(StringComparer.Ordinal)
            {
                ["zh-CN"] = new("中文说明。", null, "curated")
            }));
    }

    [Fact]
    public void Constructor_RejectsExternalIdentifierOutsideIdentityCatalog()
    {
        var document = CreateDocument(
            new Dictionary<string, SoftwareMetadataLocalization>(StringComparer.Ordinal)
            {
                ["en-US"] = new("English summary.", null, "curated")
            },
            new SoftwareMetadataExternalIds(PackageIdentifiers: ["Vendor.Other"]));

        Assert.Throws<InvalidDataException>(() => new SoftwareMetadataCatalogCompiler(
            document,
            [CreateIdentity()]));
    }

    [Fact]
    public void Resolve_ReturnsNullForKnownIdentityWithoutMetadata()
    {
        var catalog = CreateCatalog(
            new Dictionary<string, SoftwareMetadataLocalization>(StringComparer.Ordinal)
            {
                ["en-US"] = new("English summary.", null, "curated")
            });

        Assert.False(catalog.Contains("app-unknown"));
        Assert.Null(catalog.Resolve("app-unknown", "zh-CN"));
    }

    private static SoftwareMetadataCatalogCompiler CreateCatalog(
        IReadOnlyDictionary<string, SoftwareMetadataLocalization> localizations)
    {
        return new SoftwareMetadataCatalogCompiler(
            CreateDocument(localizations),
            [CreateIdentity()]);
    }

    private static SoftwareMetadataCatalogDocument CreateDocument(
        IReadOnlyDictionary<string, SoftwareMetadataLocalization> localizations,
        SoftwareMetadataExternalIds? externalIds = null)
    {
        return new SoftwareMetadataCatalogDocument(
            "1.0.0",
            [new SoftwareMetadataCatalogSource(
                "curated",
                "Resource Manager tests",
                "1",
                "Test data")],
            [new SoftwareMetadataCatalogEntry(
                "app-test",
                externalIds ?? new SoftwareMetadataExternalIds(
                    PackageIdentifiers: ["Vendor.Test"]),
                "Test Publisher",
                "https://example.test/",
                null,
                null,
                null,
                ["test"],
                localizations,
                ["curated"])]);
    }

    private static SoftwareIdentityCatalogEntry CreateIdentity()
    {
        return new SoftwareIdentityCatalogEntry(
            "app-test",
            "Test App",
            "Other",
            "test",
            PackageIdentifiers: ["Vendor.Test"]);
    }

    private static T ReadBundledDocument<T>(string directory, string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure",
            "Resources",
            directory,
            fileName);
        using var stream = File.OpenRead(path);
        return Assert.IsType<T>(JsonSerializer.Deserialize<T>(stream, JsonSerializerOptions.Web));
    }
}
