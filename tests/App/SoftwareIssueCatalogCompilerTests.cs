using System.Text.Json;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareIdentity;
using ResourceManager.App.Infrastructure.SoftwareIssues;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareIssueCatalogCompilerTests
{
    private const string ArtifactPath = @"C:\Program Files\Fixture\driver.sys";

    [Fact]
    public void ExactIdentityReadBlocksWriteAndReplacementUntilClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(
            Path.GetTempPath(),
            $"resource-manager-software-issue-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        try
        {
            using (SoftwareArtifactIdentityProbe.OpenForExactIdentityRead(path))
            {
                Assert.Throws<IOException>(() =>
                    File.Open(
                        path,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete)
                        .Dispose());
                Assert.Throws<IOException>(() => File.Delete(path));
            }

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BundledCatalogDefinesExactNotebookFanControlVulnerability()
    {
        var document = ReadBundledDocument<SoftwareIssueCatalogDocument>(
            "SoftwareIssues",
            "software-issues.v1.json");
        var identities = ReadBundledDocument<SoftwareIdentityCatalogDocument>(
            "SoftwareIdentity",
            "software-identities.v1.json");
        var entry = Assert.Single(document.Entries);
        var artifact = Assert.Single(entry.Artifacts);
        var compiler = new SoftwareIssueCatalogCompiler(
            document,
            new StaticArtifactProbe(new SoftwareArtifactIdentity(
                artifact.Path,
                artifact.Length,
                artifact.Sha256,
                artifact.FileVersion)),
            identities.Entries
                .Select(static identity => identity.Id)
                .ToHashSet(StringComparer.Ordinal));

        var issues = await compiler.GetIssuesAsync(
            "app-notebook-fan-control",
            CancellationToken.None);

        Assert.Equal("app-notebook-fan-control", entry.SoftwareIdentityId);
        Assert.Equal(SoftwareIssueKinds.KnownSecurityVulnerability, entry.Kind);
        Assert.Equal(
            @"%ProgramFiles(x86)%\NoteBook FanControl\WinRing0x64.sys",
            artifact.Path);
        Assert.Equal(14544, artifact.Length);
        Assert.Equal(
            "11BD2C9F9E2397C9A16E0990E4ED2CF0679498FE0FD418A3DFDAC60B5C160EE5",
            artifact.Sha256);
        Assert.Equal("1.2.0.5", artifact.FileVersion);
        Assert.Equal(3, entry.References.Count);
        Assert.Single(issues);
    }

    [Fact]
    public async Task ExactArtifactIdentityProducesStaticIssue()
    {
        var probe = new StaticArtifactProbe(new SoftwareArtifactIdentity(
            ArtifactPath,
            14544,
            new string('A', 64),
            "1.2.0.5"));
        var compiler = new SoftwareIssueCatalogCompiler(
            Document(Artifact()),
            probe,
            KnownIdentityIds());

        var issues = await compiler.GetIssuesAsync(
            "app-fixture",
            CancellationToken.None);

        var issue = Assert.Single(issues);
        Assert.Equal(SoftwareIssueKinds.KnownSecurityVulnerability, issue.Kind);
        Assert.Equal(SoftwareIssueSources.StaticCatalog, issue.Source);
        Assert.False(issue.Dynamic);
        Assert.Empty(issue.ReportIds);
        Assert.True(compiler.OwnsArtifactPath("app-fixture", ArtifactPath));
    }

    [Theory]
    [InlineData(14543, null, null)]
    [InlineData(14544, "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", null)]
    [InlineData(14544, null, "2.0.0.0")]
    public async Task ChangedArtifactIdentityDoesNotProduceIssue(
        long length,
        string? sha256,
        string? version)
    {
        var probe = new StaticArtifactProbe(new SoftwareArtifactIdentity(
            ArtifactPath,
            length,
            sha256 ?? new string('A', 64),
            version ?? "1.2.0.5"));
        var compiler = new SoftwareIssueCatalogCompiler(
            Document(Artifact()),
            probe,
            KnownIdentityIds());

        var issues = await compiler.GetIssuesAsync(
            "app-fixture",
            CancellationToken.None);

        Assert.Empty(issues);
    }

    [Fact]
    public void ConstructorRejectsDynamicKindsAndNonHttpsReferences()
    {
        var dynamicEntry = Entry() with
        {
            Kind = SoftwareIssueKinds.AbnormalMemoryUsage
        };
        var invalidReference = Entry() with
        {
            Id = "invalid-reference",
            References = [new SoftwareIssueReference("bad", "http://example.test")]
        };

        Assert.Throws<InvalidDataException>(() =>
            new SoftwareIssueCatalogCompiler(
                Document(Artifact(), dynamicEntry),
                new StaticArtifactProbe(null),
                KnownIdentityIds()));
        Assert.Throws<InvalidDataException>(() =>
            new SoftwareIssueCatalogCompiler(
                Document(Artifact(), invalidReference),
                new StaticArtifactProbe(null),
                KnownIdentityIds()));
    }

    [Fact]
    public void ConstructorRejectsSecurityIssueWithoutExactArtifact()
    {
        var entry = Entry() with { Artifacts = [] };

        Assert.Throws<InvalidDataException>(() =>
            new SoftwareIssueCatalogCompiler(
                new SoftwareIssueCatalogDocument("1.0.0", [entry]),
                new StaticArtifactProbe(null),
                KnownIdentityIds()));
    }

    [Fact]
    public void ConstructorRejectsUnknownSoftwareIdentity()
    {
        Assert.Throws<InvalidDataException>(() =>
            new SoftwareIssueCatalogCompiler(
                Document(Artifact()),
                new StaticArtifactProbe(null),
                new HashSet<string>(StringComparer.Ordinal)));
    }

    private static SoftwareIssueCatalogDocument Document(
        SoftwareIssueArtifactRequirement artifact,
        SoftwareIssueCatalogEntry? replacement = null)
        => new("1.0.0", [replacement ?? Entry() with { Artifacts = [artifact] }]);

    private static SoftwareIssueCatalogEntry Entry()
        => new(
            "fixture-vulnerability",
            "app-fixture",
            SoftwareIssueKinds.KnownSecurityVulnerability,
            SoftwareIssueSeverities.Warning,
            "存在已公开安全漏洞",
            "fixture",
            [Artifact()],
            [new SoftwareIssueReference("NVD", "https://nvd.nist.gov/")]);

    private static SoftwareIssueArtifactRequirement Artifact()
        => new(
            ArtifactPath,
            14544,
            new string('A', 64),
            "1.2.0.5");

    private static IReadOnlySet<string> KnownIdentityIds()
        => new HashSet<string>(StringComparer.Ordinal) { "app-fixture" };

    private static T ReadBundledDocument<T>(string directory, string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure",
            "Resources",
            directory,
            fileName);
        using var stream = File.OpenRead(path);
        return Assert.IsType<T>(
            JsonSerializer.Deserialize<T>(stream, JsonSerializerOptions.Web));
    }

    private sealed class StaticArtifactProbe(SoftwareArtifactIdentity? identity)
        : ISoftwareArtifactIdentityProbe
    {
        public ValueTask<SoftwareArtifactIdentity?> ReadAsync(
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(identity);
        }
    }
}
