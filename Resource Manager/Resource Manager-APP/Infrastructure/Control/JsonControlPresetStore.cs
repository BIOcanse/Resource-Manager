using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 配置存成一个 JSON 文件，和期望状态同一套做法：写临时文件再原子替换。
///
/// 读坏了就当没有配置 —— 但**不动期望状态**。配置是用户攒下来的方案，
/// 丢了很可惜；可它丢了不该连带把机器推回默认值，那两件事本来就没有关系。
/// </summary>
public sealed class JsonControlPresetStore : IControlPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string presetPath;
    private readonly ILogger<JsonControlPresetStore>? logger;
    private ControlPresetCatalog? cached;

    public JsonControlPresetStore(
        IHostEnvironment environment,
        ILogger<JsonControlPresetStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        presetPath = Path.Combine(
            environment.ContentRootPath,
            "UserData",
            "Control",
            "presets.json");
        this.logger = logger;
    }

    public async Task<ControlPresetCatalog> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return cached ??= ReadFromDisk();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(ControlPresetCatalog catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(presetPath)
                ?? throw new InvalidOperationException("控制配置的存放路径没有父目录。");
            Directory.CreateDirectory(directory);

            var temporaryPath = $"{presetPath}.{Environment.ProcessId}.tmp";
            var json = JsonSerializer.Serialize(catalog, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, presetPath, overwrite: true);
            cached = catalog;
        }
        finally
        {
            gate.Release();
        }
    }

    private ControlPresetCatalog ReadFromDisk()
    {
        if (!File.Exists(presetPath))
        {
            return ControlPresetCatalog.Empty;
        }
        try
        {
            var json = File.ReadAllText(presetPath);
            return JsonSerializer.Deserialize<ControlPresetCatalog>(json, JsonOptions)
                ?? ControlPresetCatalog.Empty;
        }
        catch (Exception error) when (error is JsonException or IOException
            or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "控制配置读取失败，按没有配置处理。");
            return ControlPresetCatalog.Empty;
        }
    }
}
