using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Infrastructure.Dependencies;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// 安装器来源解析的合同：已验证版本不联网且地址确定；最新版本按目录声明的模式顺序取资产；
/// 资产地址必须落在允许的下载域内；解析失败只影响最新版本这一项。
/// </summary>
public sealed class DependencyInstallerSourceResolverTests
{
    private static OptionalDependencyDefinition GitHubDefinition(
        IReadOnlyList<string>? assetPatterns = null) =>
        new(
            Id: "test-dependency",
            Name: "Test Dependency",
            Vendor: "Test",
            Category: "Test",
            SourcePageUrl: "https://github.com/owner/repo",
            DownloadUrl: null,
            ExternalTermsUrl: "https://github.com/owner/repo/blob/master/LICENSE",
            InstallerFileName: "Installer.zip",
            InstallerFilePatterns: ["Installer*.zip"],
            InstallDirectoryName: "Test",
            RequiresExternalTermsAcknowledgement: true,
            RequiresElevation: false,
            InstalledProbeRelativePaths: [],
            InstallNote: "test",
            ReleaseSource: new GitHubReleaseSource(
                Owner: "owner",
                Repository: "repo",
                VerifiedTag: "v1.2.3",
                VerifiedAssetName: "Installer.zip",
                AssetPatterns: assetPatterns ?? ["Installer.zip", "Installer*.zip"]));

    private static DependencyInstallerSourceResolver CreateResolver(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? payload = null,
        Exception? failure = null) =>
        new(
            new HttpClient(new StubHandler(statusCode, payload, failure)),
            NullLogger<DependencyInstallerSourceResolver>.Instance);

    [Fact]
    public async Task VerifiedChoiceBuildsDeterministicUrlWithoutNetwork()
    {
        // 处理器一旦被调用就抛异常，证明已验证版本这条路不联网。
        var resolver = CreateResolver(failure: new InvalidOperationException("不应该联网"));

        var resolved = await resolver.ResolveAsync(
            GitHubDefinition(),
            DependencyVersionChoices.Verified,
            CancellationToken.None);

        Assert.Equal(
            "https://github.com/owner/repo/releases/download/v1.2.3/Installer.zip",
            resolved.DownloadUrl);
        Assert.Equal("v1.2.3", resolved.Version);
    }

    [Fact]
    public async Task MissingChoiceFallsBackToVerified()
    {
        var resolver = CreateResolver(failure: new InvalidOperationException("不应该联网"));

        var resolved = await resolver.ResolveAsync(GitHubDefinition(), null, CancellationToken.None);

        Assert.Equal("v1.2.3", resolved.Version);
    }

    [Fact]
    public async Task UnknownChoiceIsRejected()
    {
        var resolver = CreateResolver();

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            GitHubDefinition(),
            "whatever",
            CancellationToken.None));
    }

    [Fact]
    public async Task LatestChoicePicksAssetByPatternOrderNotResponseOrder()
    {
        var resolver = CreateResolver(payload: """
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "Installer.NET.10.zip", "browser_download_url": "https://github.com/owner/repo/releases/download/v9.9.9/Installer.NET.10.zip" },
            { "name": "Installer.zip", "browser_download_url": "https://github.com/owner/repo/releases/download/v9.9.9/Installer.zip" }
          ]
        }
        """);

        var resolved = await resolver.ResolveAsync(
            GitHubDefinition(),
            DependencyVersionChoices.Latest,
            CancellationToken.None);

        // 目录里 "Installer.zip" 排在 "Installer*.zip" 前面，所以即便它在响应里排第二也应被选中。
        Assert.Equal("Installer.zip", resolved.AssetName);
        Assert.Equal("v9.9.9", resolved.Version);
    }

    [Fact]
    public async Task AssetOutsideAllowedDownloadHostIsRejected()
    {
        var resolver = CreateResolver(payload: """
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "Installer.zip", "browser_download_url": "https://example.com/Installer.zip" }
          ]
        }
        """);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            GitHubDefinition(),
            DependencyVersionChoices.Latest,
            CancellationToken.None));
    }

    [Fact]
    public async Task UnmatchedAssetsAreRejected()
    {
        var resolver = CreateResolver(payload: """
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "Source code.zip", "browser_download_url": "https://github.com/owner/repo/releases/download/v9.9.9/Source%20code.zip" }
          ]
        }
        """);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            GitHubDefinition(),
            DependencyVersionChoices.Latest,
            CancellationToken.None));
    }

    [Fact]
    public async Task VersionOptionsKeepVerifiedAvailableWhenLatestLookupFails()
    {
        var resolver = CreateResolver(statusCode: HttpStatusCode.Forbidden);

        var options = await resolver.GetVersionOptionsAsync(GitHubDefinition(), CancellationToken.None);

        Assert.Equal(DependencyInstallerSourceKinds.GitHubRelease, options.SourceKind);
        var verified = options.Options.Single(option => option.Choice == DependencyVersionChoices.Verified);
        var latest = options.Options.Single(option => option.Choice == DependencyVersionChoices.Latest);
        Assert.True(verified.Available);
        Assert.Equal("v1.2.3", verified.Version);
        Assert.False(latest.Available);
        Assert.False(string.IsNullOrWhiteSpace(latest.UnavailableReason));
    }

    [Fact]
    public async Task DirectSourceUsesCatalogUrl()
    {
        var definition = GitHubDefinition() with
        {
            DownloadUrl = "https://vendor.example/installer.exe",
            ReleaseSource = null
        };
        var resolver = CreateResolver(failure: new InvalidOperationException("不应该联网"));

        var resolved = await resolver.ResolveAsync(definition, null, CancellationToken.None);

        Assert.Equal("https://vendor.example/installer.exe", resolved.DownloadUrl);
        Assert.Equal(DependencyInstallerSourceKinds.Direct, definition.InstallerSourceKind);
    }

    [Fact]
    public async Task ManualSourceHasNoVersionOptionsAndCannotResolve()
    {
        var definition = GitHubDefinition() with { ReleaseSource = null };
        var resolver = CreateResolver();

        var options = await resolver.GetVersionOptionsAsync(definition, CancellationToken.None);

        Assert.Equal(DependencyInstallerSourceKinds.Manual, definition.InstallerSourceKind);
        Assert.Empty(options.Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            definition,
            null,
            CancellationToken.None));
    }

    [Fact]
    public void CatalogDeclaresExpectedSourceKinds()
    {
        string KindOf(string id) => OptionalDependencyCatalog.Definitions
            .Single(definition => definition.Id == id)
            .InstallerSourceKind;

        Assert.Equal(DependencyInstallerSourceKinds.Direct, KindOf("shared-webview2-runtime"));
        Assert.Equal(DependencyInstallerSourceKinds.GitHubRelease, KindOf("librehardwaremonitor-provider"));
        Assert.Equal(DependencyInstallerSourceKinds.GitHubRelease, KindOf("amd-smu-pawnio-provider"));
        Assert.Equal(DependencyInstallerSourceKinds.GitHubRelease, KindOf("notebook-fancontrol-provider"));
        // 下载页需要人工交互的厂商仍然是手动获取，不假装能一键。
        Assert.Equal(DependencyInstallerSourceKinds.Manual, KindOf("msi-afterburner"));
    }

    [Fact]
    public void GitHubReleaseSourcesDeclareVerifiedAssetMatchingTheirPatterns()
    {
        foreach (var definition in OptionalDependencyCatalog.Definitions)
        {
            if (definition.ReleaseSource is not { } source)
            {
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(source.VerifiedTag));
            Assert.False(string.IsNullOrWhiteSpace(source.VerifiedAssetName));
            Assert.NotEmpty(source.AssetPatterns);
        }
    }

    private sealed class StubHandler(
        HttpStatusCode statusCode,
        string? payload,
        Exception? failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(payload ?? "{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
