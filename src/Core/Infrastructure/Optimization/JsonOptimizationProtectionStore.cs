using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.Persistence;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class JsonOptimizationProtectionStore(IHostEnvironment environment) : IOptimizationProtectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonOptimizationProtectionStoreJsonContext(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }).Options;

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
                var image = await File.ReadAllBytesAsync(protectionPath, cancellationToken);
                if (!IsCurrentProtectionImage(image))
                    return await MigrateDisplayTextAsync(image, cancellationToken);
                var document = JsonSerializer.Deserialize<OptimizationProtectionDocument>(image, JsonOptions)
                    ?? throw new InvalidDataException("The optimization protection document is null.");
                if (document.Version != OptimizationProtectionDocument.CurrentVersion)
                    throw new InvalidDataException("Unsupported optimization protection format.");
                return Normalize(document.Targets);
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
            await SaveCoreAsync(targets, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SaveCoreAsync(IReadOnlyList<ProtectedOptimizationTarget> targets, CancellationToken cancellationToken)
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
                    new OptimizationProtectionDocument(OptimizationProtectionDocument.CurrentVersion, Normalize(targets)),
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

    private static bool IsCurrentProtectionImage(byte[] image)
    {
        try
        {
            using var json = JsonDocument.Parse(image);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var number)
                && number == OptimizationProtectionDocument.CurrentVersion;
        }
        catch (JsonException) { return false; }
    }

    private async Task<IReadOnlyList<ProtectedOptimizationTarget>> MigrateDisplayTextAsync(byte[] image, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProtectedOptimizationTarget> targets;
        try
        {
            var array = System.Text.Json.Nodes.JsonNode.Parse(image)!.AsArray();
            foreach (var node in array)
            {
                var item = node!.AsObject();
                item.Remove("displayName");
                item.Remove("protectedReason");
                item["protectionSource"] = item["createdFromReportId"]?.GetValue<string>() == "software-detail" ? "software_detail" : "report";
            }
            targets = Normalize(array.Deserialize<List<ProtectedOptimizationTarget>>(JsonOptions)!.Select(item => item with
            {
                TargetType = StoredOptimizationWireValues.ReadReportTargetType(item.TargetType),
                SoftwareKind = item.SoftwareKind is null ? null : SoftwareKinds.ReadStored(item.SoftwareKind),
                State = StoredOptimizationWireValues.ReadProtectionState(item.State)
            }));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or NullReferenceException)
        {
            System.Diagnostics.Trace.TraceWarning("Optimization protection migration starts empty: {0}", error.Message);
            targets = [];
        }
        var backup = protectionPath + ".display-text.byte-exact." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant() + ".backup";
        if (!File.Exists(backup))
        {
            await using var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(image, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        var preserved = await File.ReadAllBytesAsync(backup, cancellationToken);
        if (!image.AsSpan().SequenceEqual(preserved))
            throw new IOException("Optimization protection migration backup differs from its source.");
        var current = await File.ReadAllBytesAsync(protectionPath, cancellationToken);
        if (!image.AsSpan().SequenceEqual(current))
            throw new IOException("Optimization protection changed during migration.");
        await SaveCoreAsync(targets, cancellationToken);
        return targets;
    }

    private sealed record OptimizationProtectionDocument(int Version, IReadOnlyList<ProtectedOptimizationTarget> Targets)
    {
        public const int CurrentVersion = 1;
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
