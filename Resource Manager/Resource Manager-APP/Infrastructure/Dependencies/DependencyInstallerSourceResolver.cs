using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Infrastructure.Dependencies;

/// <summary>
/// 安装器来源解析：直链照用；GitHub 来源提供「已验证版本」和「最新版本」两个选择。
/// 已验证版本的地址是确定性拼出来的，不调用 API，所以断网或被速率限制时仍可安装。
/// </summary>
public sealed class DependencyInstallerSourceResolver : IDependencyInstallerSourceResolver
{
    private const string GitHubApiRoot = "https://api.github.com";
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);

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
            return new DependencyVersionOptions(definition.Id, kind, []);
        }

        if (kind == DependencyInstallerSourceKinds.Manual || definition.ReleaseSource is null)
        {
            return new DependencyVersionOptions(definition.Id, kind, []);
        }

        var source = definition.ReleaseSource;
        var verified = new DependencyVersionOption(
            DependencyVersionChoices.Verified,
            Available: true,
            Version: source.VerifiedTag,
            AssetName: source.VerifiedAssetName,
            UnavailableReason: null);

        DependencyVersionOption latest;
        try
        {
            var resolved = await ResolveLatestAsync(source, cancellationToken);
            latest = new DependencyVersionOption(
                DependencyVersionChoices.Latest,
                Available: true,
                resolved.Version,
                resolved.AssetName,
                UnavailableReason: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "无法解析 {Owner}/{Repository} 的最新发布，版本对话框只提供已验证版本。",
                source.Owner,
                source.Repository);
            latest = new DependencyVersionOption(
                DependencyVersionChoices.Latest,
                Available: false,
                Version: null,
                AssetName: null,
                UnavailableReason: DescribeFailure(exception));
        }

        return new DependencyVersionOptions(definition.Id, kind, [verified, latest]);
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
            return await ResolveLatestAsync(source, cancellationToken);
        }

        if (!string.Equals(choice, DependencyVersionChoices.Verified, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"未知的版本选择：{choice}");
        }

        return ResolveVerified(source);
    }

    /// <summary>已验证版本：地址由 owner/repo/tag/asset 确定性拼出，不联网。</summary>
    private static ResolvedInstallerSource ResolveVerified(GitHubReleaseSource source)
    {
        var url =
            $"https://github.com/{Uri.EscapeDataString(source.Owner)}/{Uri.EscapeDataString(source.Repository)}" +
            $"/releases/download/{Uri.EscapeDataString(source.VerifiedTag)}/{Uri.EscapeDataString(source.VerifiedAssetName)}";
        return new ResolvedInstallerSource(url, source.VerifiedTag, source.VerifiedAssetName);
    }

    private async Task<ResolvedInstallerSource> ResolveLatestAsync(
        GitHubReleaseSource source,
        CancellationToken cancellationToken)
    {
        var requestUri =
            $"{GitHubApiRoot}/repos/{Uri.EscapeDataString(source.Owner)}/{Uri.EscapeDataString(source.Repository)}/releases/latest";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("ResourceManager-DependencyResolver");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("GitHub 接口暂时限制了请求频率，稍后再试或改用已验证版本。");
        }

        response.EnsureSuccessStatusCode();

        await using var payload = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(payload, cancellationToken: timeout.Token);
        var root = document.RootElement;

        var version = root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
            ? tag.GetString()
            : null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("最新发布里没有可用的安装器资产。");
        }

        var candidates = new List<(string Name, string Url)>();
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                && urlElement.ValueKind == JsonValueKind.String
                    ? urlElement.GetString()
                    : null;

            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
            {
                candidates.Add((name, url));
            }
        }

        // 按目录里声明的模式顺序取，模式顺序就是偏好顺序；不依赖上游返回资产的排列。
        foreach (var pattern in source.AssetPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (!MatchesPattern(candidate.Name, pattern))
                {
                    continue;
                }

                EnsureAllowedAssetUrl(candidate.Url);
                return new ResolvedInstallerSource(candidate.Url, version, candidate.Name);
            }
        }

        throw new InvalidOperationException("最新发布里没有与该依赖匹配的安装器资产。");
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
