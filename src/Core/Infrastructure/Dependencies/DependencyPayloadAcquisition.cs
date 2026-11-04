using System.IO.Compression;
using System.Security.Cryptography;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Infrastructure.Dependencies;

/// <summary>Explicit installation replaces a release; verification repairs only missing files of a recorded current release.</summary>
public sealed class DependencyPayloadAcquisition(
    HttpClient httpClient,
    IHostEnvironment environment,
    HardwareBridgeClient hardwareBridge,
    ILogger<DependencyPayloadAcquisition>? logger = null) : IDependencyPayloadAcquisition
{
    internal Func<string, Stream>? OpenStagedFile { get; init; }
    internal Func<CancellationToken, ValueTask<IAsyncDisposable>>? SuspendHelper { get; init; }

    public Task<DependencyPayloadResult> EnsureAsync(OptionalDependencyDefinition definition, CancellationToken cancellationToken)
        => AcquireAsync(definition, replaceRelease: false, cancellationToken);

    public Task<DependencyPayloadResult> InstallAsync(OptionalDependencyDefinition definition, CancellationToken cancellationToken)
        => AcquireAsync(definition, replaceRelease: true, cancellationToken);

    private async Task<DependencyPayloadResult> AcquireAsync(OptionalDependencyDefinition definition, bool replaceRelease, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.PayloadSource is not { } source) return new(true, "This component has no separate payload.");
        try
        {
            var directory = Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), "Dependencies", definition.Id);
            DependencyInstallPaths.RejectLinks(directory);
            if (Directory.Exists(directory)) DependencyInstallPaths.ValidateTree(directory);
            var missing = source.FileNames.Where(name => !File.Exists(DependencyInstallPaths.InRoot(directory, name))).ToArray();
            var installed = DependencyReleaseRecordStore.Read(DependencyReleaseRecordStore.InstalledPath(directory), logger);
            var current = DependencyReleaseRecordStore.MatchesPin(installed, definition);
            if (missing.Length == 0 && (current || !replaceRelease)) return new(true, "No missing files require repair.");
            if (!replaceRelease && !current) return new(false, "A confirmed pinned installation is required before same-release repair.");

            var url = $"https://github.com/{source.Owner}/{source.Repository}/releases/download/{source.VerifiedTag}/{source.VerifiedAssetName}";
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, token).ConfigureAwait(false);
            buffer.Position = 0;
            var record = new DependencyReleaseRecord(source.VerifiedTag, source.VerifiedAssetName, Convert.ToHexString(SHA256.HashData(buffer)));
            if (!replaceRelease && !string.Equals(record.AssetSha256, installed!.AssetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The repair archive differs from the recorded installed asset.");
            buffer.Position = 0;
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
            using var transaction = new DependencyPayloadTransaction(directory, replaceRelease, logger);
            transaction.Prepare(archive, source.FileNames, missing, source.IncludeArchiveContents, record, token, OpenStagedFile);
            // A LICENSE-only repair neither writes the executable nor ends its session.
            var writesHelper = definition.Id == "hardware-bridge" && (replaceRelease
                || missing.Any(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
            await using var session = writesHelper
                ? await (SuspendHelper is null ? hardwareBridge.SuspendForInstallationAsync(token) : SuspendHelper(token)).ConfigureAwait(false) : null;
            transaction.Publish(token);
            return new(true, "The requested payload files were published.", Changed: true);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException
            or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            logger?.LogWarning(error, "Component payload acquisition failed for {ComponentId}", definition.Id);
            return new(false, $"Component payload acquisition failed: {error.Message}");
        }
    }
}
