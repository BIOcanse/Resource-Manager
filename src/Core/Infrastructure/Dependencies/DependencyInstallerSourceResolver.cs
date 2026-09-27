using System.Text.RegularExpressions;
using System.Text.Json;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.Shared.Packages;
using ResourceManager.App.Infrastructure.Updates;

namespace ResourceManager.App.Infrastructure.Dependencies;

/// <summary>
/// 安装器来源解析：直链照用；GitHub 来源提供已验证、最新、最新稳定及历史版本。
/// 已验证版本的地址是确定性拼出来的，不调用 API，所以断网或被速率限制时仍可安装。
/// </summary>
public sealed class DependencyInstallerSourceResolver : IDependencyInstallerSourceResolver
{
    // 解析出的资产地址只接受这两个前缀；GitHub 的资产下载会重定向到 objects.githubusercontent.com。
    private static readonly string[] AllowedAssetPrefixes =
    [
        "https://github.com/",
        "https://objects.githubusercontent.com/"
    ];

    private readonly HttpClient httpClient;
    private readonly ILogger<DependencyInstallerSourceResolver> logger;

    public DependencyInstallerSourceResolver(
        HttpClient httpClient,
        ILogger<DependencyInstallerSourceResolver> logger)
    {
        this.httpClient = httpClient;
        this.logger = logger;
    }

    public async Task<DependencyVersionOptions> GetVersionOptionsAsync(
        OptionalDependencyDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var kind = definition.InstallerSourceKind;
        if (kind == DependencyInstallerSourceKinds.Direct)
        {
            return new DependencyVersionOptions(definition.Id, kind, [], "unavailable", false);
        }

        if (kind == DependencyInstallerSourceKinds.Manual || definition.ReleaseSource is null)
        {
            return new DependencyVersionOptions(definition.Id, kind, [], "unavailable", false);
        }

        var source = definition.ReleaseSource;
        var verified = new DependencyVersionOption(
            DependencyVersionChoices.Verified,
            Available: true,
            Version: source.VerifiedTag,
            AssetName: source.VerifiedAssetName,
            UnavailableReason: null);

        var options = new List<DependencyVersionOption> { verified };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var listing = await GitHubReleaseReader.ReadAsync(
                httpClient, source.Owner, source.Repository, timeout.Token);
            var releases = listing.Releases
                .Select(release => (Release: release, Parsed: ParseVersion(release.Tag), Asset: PickAsset(source, release.Assets)))
                .Where(item => item.Asset is not null)
                .OrderByDescending(item => item.Parsed, Comparer<ReleaseVersion?>.Create(
                    (left, right) => left is null ? right is null ? 0 : -1 : left.CompareTo(right)))
                .ThenByDescending(item => item.Release.PublishedAt)
                .ToArray();
            var latest = releases.FirstOrDefault();
            var stable = releases.FirstOrDefault(item => !item.Release.Prerelease && item.Parsed?.Stable == true);
            options.Add(ToPinned(DependencyVersionChoices.Latest, latest, "没有可用的发布版本。"));
            options.Add(ToPinned(DependencyVersionChoices.LatestStable, stable, "没有可用的稳定版本。"));
            foreach (var release in releases)
            {
                options.Add(new DependencyVersionOption(
                    DependencyVersionChoices.TagPrefix + release.Release.Tag,
                    Available: release.Parsed is not null,
                    release.Release.Tag,
                    release.Asset!.Name,
                    release.Parsed is null ? "该上游版本号不能可靠排序，无法确认升级方向。" : null,
                    release.Parsed?.Series,
                    release.Release.Prerelease || release.Parsed?.Stable != true ? "preview" : "stable",
                    release.Release.PublishedAt));
            }
            return new DependencyVersionOptions(
                definition.Id, kind, options,
                listing.Complete ? "loaded" : "partial", listing.Complete);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "无法解析 {Owner}/{Repository} 的最新发布，版本对话框只提供已验证版本。",
                source.Owner,
                source.Repository);
            options.Add(new DependencyVersionOption(
                DependencyVersionChoices.Latest,
                Available: false,
                Version: null,
                AssetName: null,
                UnavailableReason: DescribeFailure(exception)));
            options.Add(new DependencyVersionOption(
                DependencyVersionChoices.LatestStable,
                Available: false,
                Version: null,
                AssetName: null,
                UnavailableReason: DescribeFailure(exception)));
            return new DependencyVersionOptions(definition.Id, kind, options, "error", false);
        }
    }

    public async Task<ResolvedInstallerSource> ResolveAsync(
        OptionalDependencyDefinition definition,
        string? versionChoice,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!string.IsNullOrWhiteSpace(definition.DownloadUrl))
        {
            return new ResolvedInstallerSource(definition.DownloadUrl, Version: null, definition.InstallerFileName);
        }

        var source = definition.ReleaseSource
            ?? throw new InvalidOperationException("这个依赖没有可自动获取的安装器来源，请手动放入安装器缓存目录。");

        var choice = string.IsNullOrWhiteSpace(versionChoice)
            ? DependencyVersionChoices.Verified
            : versionChoice.Trim();

        if (string.Equals(choice, DependencyVersionChoices.Latest, StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveCatalogChoiceAsync(source, stableOnly: false, cancellationToken);
        }

        if (string.Equals(choice, DependencyVersionChoices.LatestStable, StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveCatalogChoiceAsync(source, stableOnly: true, cancellationToken);
        }

        if (choice.StartsWith(DependencyVersionChoices.TagPrefix, StringComparison.Ordinal))
        {
            return await ResolveTagAsync(source, choice[DependencyVersionChoices.TagPrefix.Length..], cancellationToken);
        }

        if (!string.Equals(choice, DependencyVersionChoices.Verified, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"未知的版本选择：{choice}");
        }

        return ResolveVerified(source);
    }

    private static ReleaseVersion? ParseVersion(string tag)
        => ReleaseVersion.TryParse(tag, out var parsed) ? parsed : null;

    private static GitHubReleaseAsset? PickAsset(GitHubReleaseSource source, IReadOnlyList<GitHubReleaseAsset> assets)
    {
        foreach (var pattern in source.AssetPatterns.Where(static pattern => !string.IsNullOrWhiteSpace(pattern)))
            foreach (var asset in assets)
                if (MatchesPattern(asset.Name, pattern)) return asset;
        return null;
    }

    private static DependencyVersionOption ToPinned(
        string choice,
        (GitHubPublishedRelease Release, ReleaseVersion? Parsed, GitHubReleaseAsset? Asset) item,
        string unavailableReason)
    {
        if (item.Release is null || item.Asset is null)
            return new DependencyVersionOption(choice, false, null, null, unavailableReason);
        return new DependencyVersionOption(choice, item.Parsed is not null,
            item.Release.Tag, item.Asset.Name,
            item.Parsed is null ? "该上游版本号不能可靠排序。" : null,
            item.Parsed?.Series,
            item.Release.Prerelease || item.Parsed?.Stable != true ? "preview" : "stable",
            item.Release.PublishedAt);
    }

    private async Task<ResolvedInstallerSource> ResolveCatalogChoiceAsync(
        GitHubReleaseSource source, bool stableOnly, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var listing = await GitHubReleaseReader.ReadAsync(
            httpClient, source.Owner, source.Repository, timeout.Token);
        var selected = listing.Releases
            .Select(release => (Release: release, Parsed: ParseVersion(release.Tag), Asset: PickAsset(source, release.Assets)))
            .Where(item => item.Parsed is not null && item.Asset is not null
                && (!stableOnly || (!item.Release.Prerelease && item.Parsed.Stable)))
            .OrderByDescending(item => item.Parsed)
            .FirstOrDefault();
        if (selected.Release is null || selected.Asset is null)
            throw new InvalidOperationException(stableOnly
                ? "没有可用的稳定版本。" : "没有可用的发布版本。");
        EnsureAllowedAssetUrl(selected.Asset.DownloadUrl);
        return new ResolvedInstallerSource(
            selected.Asset.DownloadUrl, selected.Release.Tag, selected.Asset.Name);
    }

    private async Task<ResolvedInstallerSource> ResolveTagAsync(
        GitHubReleaseSource source, string tag, CancellationToken cancellationToken)
    {
        if (tag.Length is < 1 or > 160 || tag.Any(char.IsControl))
            throw new InvalidOperationException("无效的发布标签。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var listing = await GitHubReleaseReader.ReadAsync(
            httpClient, source.Owner, source.Repository, timeout.Token);
        var release = listing.Releases.FirstOrDefault(item => item.Tag == tag)
            ?? throw new InvalidOperationException("所选版本不在已发布目录中。");
        var asset = PickAsset(source, release.Assets)
            ?? throw new InvalidOperationException("所选发布没有匹配的安装器资产。");
        if (ParseVersion(release.Tag) is null)
            throw new InvalidOperationException("所选发布版本无法可靠排序。");
        EnsureAllowedAssetUrl(asset.DownloadUrl);
        return new ResolvedInstallerSource(asset.DownloadUrl, release.Tag, asset.Name);
    }

    /// <summary>已验证版本：地址由 owner/repo/tag/asset 确定性拼出，不联网。</summary>
    private static ResolvedInstallerSource ResolveVerified(GitHubReleaseSource source)
    {
        var url =
            $"https://github.com/{Uri.EscapeDataString(source.Owner)}/{Uri.EscapeDataString(source.Repository)}" +
            $"/releases/download/{Uri.EscapeDataString(source.VerifiedTag)}/{Uri.EscapeDataString(source.VerifiedAssetName)}";
        return new ResolvedInstallerSource(url, source.VerifiedTag, source.VerifiedAssetName);
    }

    /// <summary>资产地址必须落在 GitHub 的发布下载域内，不跟随到白名单外的域。</summary>
    private static void EnsureAllowedAssetUrl(string url)
    {
        foreach (var prefix in AllowedAssetPrefixes)
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidOperationException($"发布资产地址不在允许的下载来源内：{url}");
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(name, expression, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    private static string DescribeFailure(Exception exception) => exception switch
    {
        TaskCanceledException => "连接 GitHub 超时。",
        HttpRequestException => "无法连接 GitHub。",
        JsonException => "GitHub 返回的数据无法解析。",
        _ => exception.Message
    };
}
