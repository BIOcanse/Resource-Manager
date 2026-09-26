using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class JsonOptimizationProtectionStore(IHostEnvironment environment) : IOptimizationProtectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string protectionPath = Path.Combine(
        PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "Config",
        "optimization-protection.json");

    public async Task<IReadOnlyList<ProtectedOptimizationTarget>> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await using var stream = new FileStream(
                    protectionPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                var targets = await JsonSerializer.DeserializeAsync<List<ProtectedOptimizationTarget>>(
                    stream,
                    JsonOptions,
                    cancellationToken);
                return Normalize(targets
                    ?? throw new InvalidDataException(
                        "The optimization protection document is null."));
            }
            catch (FileNotFoundException)
            {
                return [];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "The optimization protection document is invalid.",
                    exception);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<ProtectedOptimizationTarget> targets,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(protectionPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = protectionPath + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        Normalize(targets),
                        JsonOptions,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, protectionPath, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (FileNotFoundException)
                {
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static IReadOnlyList<ProtectedOptimizationTarget> Normalize(
        IEnumerable<ProtectedOptimizationTarget> targets)
    {
        return targets
            .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(static item => item with
            {
                ProtectionLevel = OptimizationProtectionLevels.Normalize(item.ProtectionLevel)
            })
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderBy(static item => item.ProtectedAt).First())
            .OrderBy(static item => item.ProtectedAt)
            .ToArray();
    }
}
