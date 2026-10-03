using System.Text.Json;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Overlay;

public sealed class JsonPerformanceOverlaySettingsStore(IHostEnvironment environment)
    : IPerformanceOverlaySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storagePath = Path.Combine(
        PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData", "SoftwareProfiles", "performance-overlay.local.json");

    public async Task<PerformanceOverlaySettingsDocument> GetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await LoadCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<PerformanceOverlaySettings> GetSoftwareAsync(
        string softwareId, CancellationToken cancellationToken)
    {
        var id = PerformanceOverlaySettings.Normalize(null, softwareId).SoftwareId;
        var document = await GetAsync(cancellationToken);
        return document.Software.FirstOrDefault(item =>
            item.SoftwareId.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? PerformanceOverlaySettings.Default(id);
    }

    public async Task<PerformanceOverlaySettings> SaveSoftwareAsync(
        PerformanceOverlaySettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = PerformanceOverlaySettings.Normalize(settings, settings.SoftwareId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            var next = new PerformanceOverlaySettingsDocument(1, document.Software
                .Where(item => !item.SoftwareId.Equals(normalized.SoftwareId, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderBy(static item => item.SoftwareId, StringComparer.OrdinalIgnoreCase)
                .ToArray());
            await SaveCoreAsync(next, cancellationToken);
            return normalized;
        }
        finally { gate.Release(); }
    }

    private async Task<PerformanceOverlaySettingsDocument> LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Normalize(await JsonSerializer.DeserializeAsync<PerformanceOverlaySettingsDocument>(
                stream, JsonOptions, cancellationToken));
        }
        catch (FileNotFoundException) { return PerformanceOverlaySettingsDocument.Empty; }
        catch (DirectoryNotFoundException) { return PerformanceOverlaySettingsDocument.Empty; }
    }

    private async Task SaveCoreAsync(
        PerformanceOverlaySettingsDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        var temporaryPath = $"{storagePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, Normalize(document), JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        NativeCore.WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, storagePath);
    }

    private static PerformanceOverlaySettingsDocument Normalize(PerformanceOverlaySettingsDocument? document)
    {
        var software = (document?.Software ?? [])
            .Where(static item => item is not null && !string.IsNullOrWhiteSpace(item.SoftwareId))
            .Select(static item => PerformanceOverlaySettings.Normalize(item, item.SoftwareId))
            .GroupBy(static item => item.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Last())
            .OrderBy(static item => item.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new PerformanceOverlaySettingsDocument(1, software);
    }
}
