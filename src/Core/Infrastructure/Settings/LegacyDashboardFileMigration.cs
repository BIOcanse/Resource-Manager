using System.Text.Json;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Settings;

// One-time retirement of the old backend-owned file, not a dashboard settings
// store. Remove with the frontend importer when no supported upgrade starts at
// dashboard schema 16 or earlier. The backup must remain until then.
public sealed class LegacyDashboardFileMigration(
    IHostEnvironment environment,
    RuntimePersistenceCapabilityPolicy persistence,
    ILogger<LegacyDashboardFileMigration>? logger = null)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!persistence.MutablePersistenceEnabled) return;
        var config = Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), "Config");
        foreach (var name in new[] { "dashboard-settings.json", "dashboard-settings.last-good.json" })
        {
            var path = Path.Combine(config, name);
            try
            {
                var bytes = await WindowsNativeAtomicFileCommitter.ReadCommittedBytesAsync(path, cancellationToken);
                using var image = JsonDocument.Parse(bytes);
                if (!PresentationPreferenceRemoval.HasDashboardFields(image.RootElement)) continue;
                PresentationPreferenceRemoval.PreserveLegacyImage(path, bytes,
                    PresentationPreferenceRemoval.HasDashboardFields, PresentationPreferenceRemoval.DashboardBackupSuffix);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await stream.WriteAsync("{\"version\":17}\n"u8.ToArray(), cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                        stream.Flush(flushToDisk: true);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    WindowsNativeAtomicFileCommitter.CommitReplace(temporary, path);
                }
                finally { _ = WindowsNativeAtomicFileCommitter.DeleteExact(temporary); }
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable old presentation file cannot prevent backend startup.
                logger?.LogWarning(error, "Legacy dashboard retirement left the original file at {Path}.", path);
            }
        }
    }
}
