using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 调节权限档位的存放处，就是一个小文件，里面写着档位名。
///
/// **任何读不出来的情况都回到最低那一档。** 文件没有、坏了、内容不认识、权限没了 ——
/// 结果都是"普通"。旧安全档记录也归入普通档，Root 必须明确选择。
/// 不会出现文件损坏反而把危险项放开的情况。
/// </summary>
public sealed class JsonControlAccessLevel : IControlAccessLevel
{
    private readonly object gate = new();
    private readonly string markerPath;
    private readonly ILogger<JsonControlAccessLevel>? logger;
    private bool loaded;
    private string current = ControlAccessLevels.Normal;

    public JsonControlAccessLevel(
        IHostEnvironment environment,
        ILogger<JsonControlAccessLevel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        markerPath = Path.Combine(
            environment.ContentRootPath,
            "UserData",
            "Control",
            "access-level.txt");
        this.logger = logger;
    }

    public string Current
    {
        get
        {
            lock (gate)
            {
                if (!loaded)
                {
                    current = Read();
                    loaded = true;
                }
                return current;
            }
        }
    }

    public Task SetAsync(string level, CancellationToken cancellationToken)
    {
        var normalized = Normalize(level);
        lock (gate)
        {
            Write(normalized);
            current = normalized;
            loaded = true;
        }
        return Task.CompletedTask;
    }

    /// <summary>认识的就用，不认识的一律当成最低那一档。</summary>
    private static string Normalize(string? level)
        => ControlAccessLevels.All.FirstOrDefault(
            known => string.Equals(known, level?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? ControlAccessLevels.Normal;

    private string Read()
    {
        try
        {
            return File.Exists(markerPath)
                ? Normalize(File.ReadAllText(markerPath))
                : ControlAccessLevels.Normal;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "读不到调节权限档位，按最低那一档处理。");
            return ControlAccessLevels.Normal;
        }
    }

    private void Write(string level)
    {
        try
        {
            var directory = Path.GetDirectoryName(markerPath)
                ?? throw new InvalidOperationException("调节权限档位的存放路径没有父目录。");
            Directory.CreateDirectory(directory);
            if (string.Equals(level, ControlAccessLevels.Normal, StringComparison.Ordinal))
            {
                // 回到默认就是把记录删掉 —— 没有记录本来就等于默认那一档。
                if (File.Exists(markerPath))
                {
                    File.Delete(markerPath);
                }
                return;
            }
            File.WriteAllText(markerPath, level);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "存不下调节权限档位。");
            throw;
        }
    }
}
