using System.IO.Compression;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Dependencies;

/// <summary>
/// 取组件安装器之外还需要的运行时文件。
///
/// 只在文件**确实缺**的时候下载：这些文件不会自己变，重复下载没有意义，
/// 而且组件页每刷新一次就联网一次是不能接受的。
///
/// 下载地址按已验证标签确定性拼出来，和已验证安装器同一口径 ——
/// 不调 GitHub API，所以断网或被限流时不会把整条路带塌，只是这次取不到。
/// </summary>
public sealed class DependencyPayloadAcquisition(
    HttpClient httpClient,
    IHostEnvironment environment,
    ILogger<DependencyPayloadAcquisition>? logger = null) : IDependencyPayloadAcquisition
{
    /// <summary>
    /// 确保这个组件的运行时文件都在。已经在就什么都不做。
    /// </summary>
    public async Task<DependencyPayloadResult> EnsureAsync(
        OptionalDependencyDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.PayloadSource is not { } source)
        {
            return new DependencyPayloadResult(true, "这个组件不需要额外的运行时文件。");
        }

        var directory = ResolvePayloadDirectory(definition.Id);
        var missing = source.FileNames
            .Where(name => !File.Exists(Path.Combine(directory, name)))
            .ToArray();
        if (missing.Length == 0)
        {
            return new DependencyPayloadResult(true, "运行时文件已就位。");
        }

        try
        {
            Directory.CreateDirectory(directory);
            var extracted = await DownloadAndExtractAsync(
                source,
                directory,
                missing,
                cancellationToken).ConfigureAwait(false);
            var stillMissing = source.FileNames
                .Where(name => !File.Exists(Path.Combine(directory, name)))
                .ToArray();
            return stillMissing.Length == 0
                ? new DependencyPayloadResult(true, $"已取回 {string.Join("、", extracted)}。")
                // 归档里没有我们要的文件：上游改了资产内容，如实说，别假装成功。
                : new DependencyPayloadResult(false, $"归档里没有找到：{string.Join("、", stillMissing)}。");
        }
        catch (Exception error) when (error is HttpRequestException
            or IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or TaskCanceledException)
        {
            logger?.LogWarning(error, "取 {Component} 的运行时文件失败。", definition.Id);
            return new DependencyPayloadResult(false, $"取运行时文件失败：{error.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> DownloadAndExtractAsync(
        DependencyPayloadSource source,
        string directory,
        IReadOnlyList<string> wanted,
        CancellationToken cancellationToken)
    {
        var url = $"https://github.com/{source.Owner}/{source.Repository}/releases/download/"
            + $"{source.VerifiedTag}/{source.VerifiedAssetName}";

        using var response = await httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // 归档不大（几十 KB），直接进内存，省掉一个要清理的临时文件。
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        var extracted = new List<string>();
        foreach (var name in wanted)
        {
            // 只按文件名取，且只写到目标目录下 —— 不跟随归档里的路径，
            // 免得一个构造过的归档把文件写到目录外面去。
            var entry = archive.Entries.FirstOrDefault(candidate =>
                string.Equals(
                    Path.GetFileName(candidate.FullName),
                    name,
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                continue;
            }
            var destination = Path.Combine(directory, name);
            entry.ExtractToFile(destination, overwrite: true);
            extracted.Add(name);
        }
        return extracted;
    }

    private string ResolvePayloadDirectory(string componentId)
        => Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "Dependencies",
            componentId);
}
