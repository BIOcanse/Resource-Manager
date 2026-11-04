using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>Corrupt user curves fail visibly and are never replaced by an empty catalog.</summary>
public sealed partial class JsonGpuVfCurvePresetStore : IGpuVfCurvePresetStore, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;
    private GpuVfCurveCatalog? cached;

    public JsonGpuVfCurvePresetStore(IHostEnvironment environment, StartupCapabilitySet? capabilities = null)
        => path = ControlDataPaths.FilePath(environment.ContentRootPath, "gpu-vf-curves.json", capabilities);

    public async Task<GpuVfCurveCatalog> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cached is not null) return cached;
            if (!File.Exists(path)) return cached = GpuVfCurveCatalog.Empty;
            var catalog = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                StoreJsonContext.Default.GpuVfCurveCatalog) ?? throw new InvalidDataException("The GPU V/F curve catalog is empty.");
            if (catalog.Presets is null || catalog.Presets.Any(p => string.IsNullOrWhiteSpace(p.Id)
                || p.Binding is null || p.Points is null) || catalog.Presets.Select(p => p.Id).Distinct().Count() != catalog.Presets.Count)
                throw new InvalidDataException("The GPU V/F curve catalog is invalid.");
            return cached = Freeze(catalog);
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(GpuVfCurveCatalog catalog, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var frozen = Freeze(catalog);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(frozen,
                StoreJsonContext.Default.GpuVfCurveCatalog), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            cached = frozen;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }

    private static GpuVfCurveCatalog Freeze(GpuVfCurveCatalog catalog)
        => new(System.Collections.Immutable.ImmutableArray.CreateRange(catalog.Presets.Select(p => p with
            { Points = System.Collections.Immutable.ImmutableArray.CreateRange(p.Points) })));

    public void Dispose() => gate.Dispose();

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(GpuVfCurveCatalog))]
    private partial class StoreJsonContext : JsonSerializerContext;
}
