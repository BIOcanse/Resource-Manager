using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 期望状态存成一个 JSON 文件。
///
/// 和面板设置同一套做法：写临时文件再原子替换，读坏了就当没设过 ——
/// **不能因为文件坏了就把用户的机器推回默认值**，但也不能拿半个文件去写硬件。
/// 这两者之间选"当没设过"，因为那是唯一安全的解释。
/// </summary>
public sealed class JsonControlDesiredStateStore : IControlDesiredStateStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string statePath;
    private readonly ILogger<JsonControlDesiredStateStore>? logger;
    private ControlDesiredState? cached;

    public JsonControlDesiredStateStore(
        IHostEnvironment environment,
        ILogger<JsonControlDesiredStateStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        statePath = Path.Combine(
            environment.ContentRootPath,
            "UserData",
            "Control",
            "desired-state.json");
        this.logger = logger;
    }

    public async Task<ControlDesiredState> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cached is not null)
            {
                return cached;
            }
            cached = ReadFromDisk();
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(ControlDesiredState desired, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(statePath)
                ?? throw new InvalidOperationException("控制期望状态的存放路径没有父目录。");
            Directory.CreateDirectory(directory);

            var temporaryPath = $"{statePath}.{Environment.ProcessId}.tmp";
            var json = JsonSerializer.Serialize(desired, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, statePath, overwrite: true);
            cached = desired;
        }
        finally
        {
            gate.Release();
        }
    }

    private ControlDesiredState ReadFromDisk()
    {
        if (!File.Exists(statePath))
        {
            return ControlDesiredState.Empty;
        }
        try
        {
            var json = File.ReadAllText(statePath);
            return JsonSerializer.Deserialize<ControlDesiredState>(json, JsonOptions)
                ?? ControlDesiredState.Empty;
        }
        catch (Exception error) when (error is JsonException or IOException
            or UnauthorizedAccessException)
        {
            // 读不动就当没设过。半份期望状态绝不能拿去写硬件。
            logger?.LogWarning(error, "控制期望状态读取失败，按未设定处理。");
            return ControlDesiredState.Empty;
        }
    }
}
