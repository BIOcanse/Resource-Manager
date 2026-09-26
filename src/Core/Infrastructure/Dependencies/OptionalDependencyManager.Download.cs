using System.Diagnostics;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Operations;

using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager
{
    public async Task<OptionalDependencyDownloadResult> DownloadAsync(
        string id,
        bool acknowledgeExternalTerms,
        string? versionChoice,
        CancellationToken cancellationToken,
        IProgress<DependencyDownloadProgress>? progress = null)
    {
        var definition = OptionalDependencyCatalog.Find(id)
            ?? throw new InvalidOperationException($"Unknown dependency: {id}");

        EnsureTerms(definition, acknowledgeExternalTerms);

        if (definition.InstallerSourceKind == DependencyInstallerSourceKinds.Manual)
        {
            throw new InvalidOperationException("这个依赖没有可自动获取的安装器来源，请从来源页下载后放入安装器缓存目录。");
        }

        var installerSource = await installerSourceResolver.ResolveAsync(definition, versionChoice, cancellationToken);

        var paths = GetPaths(definition);
        Directory.CreateDirectory(paths.InstallerDirectory);

        var trackingScope = new FileChangeTrackingScope(
            $"dependency-download:{definition.Id}",
            [paths.InstallerDirectory]);
        var before = await fileChangeTracker.CaptureAsync(trackingScope, cancellationToken);

        var destination = Path.Combine(paths.InstallerDirectory, definition.InstallerFileName);
        var temporary = destination + ".download";

        long bytesWritten = 0;
        try
        {
            using var response = await httpClient.GetAsync(
                installerSource.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            var startedAt = Stopwatch.GetTimestamp();
            progress?.Report(CreateProgress(definition.Id, bytesWritten, totalBytes, startedAt));

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);

            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                bytesWritten += read;
                progress?.Report(CreateProgress(definition.Id, bytesWritten, totalBytes, startedAt));
            }
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }

        File.Move(temporary, destination, overwrite: true);
        var length = new FileInfo(destination).Length;
        progress?.Report(new DependencyDownloadProgress(definition.Id, length, length, 100, null));
        var after = await fileChangeTracker.CaptureAsync(trackingScope, cancellationToken);
        var fileChanges = fileChangeTracker.Compare(before, after);

        return new OptionalDependencyDownloadResult(
            definition.Id,
            "downloaded",
            destination,
            length,
            installerSource.Version is null
                ? BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.InstallerDownloaded)
                : BackendMessage.Create(
                    BackendMessageDomains.Dependency,
                    BackendMessageCodes.Dependency.InstallerDownloadedVersion,
                    installerSource.Version),
            fileChanges);
    }
}
