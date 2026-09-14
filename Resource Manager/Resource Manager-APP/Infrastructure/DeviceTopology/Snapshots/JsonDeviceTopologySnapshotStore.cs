using System.Text.Json;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed class JsonDeviceTopologySnapshotStore : IDeviceTopologySnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger<JsonDeviceTopologySnapshotStore> logger;
    private readonly string snapshotPath;
    private readonly string machineIdentity = DeviceTopologyCacheIdentity.ReadCurrent();

    public JsonDeviceTopologySnapshotStore(
        IHostEnvironment environment,
        ILogger<JsonDeviceTopologySnapshotStore> logger)
    {
        this.logger = logger;
        snapshotPath = Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "UserData",
            "Cache",
            "DeviceTopology",
            "snapshot.v3.json");
    }

    public async Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(snapshotPath))
            {
                return null;
            }

            await using var stream = File.OpenRead(snapshotPath);
            var document = await JsonSerializer.DeserializeAsync<DeviceTopologyPersistedSnapshot>(
                stream,
                JsonOptions,
                cancellationToken);
            if (document is null
                || !string.Equals(document.SchemaVersion, DeviceTopologySnapshotState.CurrentSchemaVersion, StringComparison.Ordinal)
                || !string.Equals(document.MachineIdentity, machineIdentity, StringComparison.Ordinal)
                || document.Snapshot.System is null
                || document.Snapshot.Ports is null
                || string.IsNullOrWhiteSpace(document.SemanticHash))
            {
                return null;
            }

            return document;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load the persisted device topology snapshot.");
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        DeviceTopologyPersistedSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        var temporaryPath = snapshotPath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(snapshotPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = snapshot with { MachineIdentity = machineIdentity };
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, snapshotPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }

            gate.Release();
        }
    }
}
