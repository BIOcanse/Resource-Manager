using ResourceManager.App.Application.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 首次须知看过没有，存成一个小文件。
///
/// 读不到就当**没看过**：多给人看一次须知没有坏处，漏掉那一次才有。
/// </summary>
public sealed class JsonControlNoticeAcknowledgement : IControlNoticeAcknowledgement
{
    private const string SeenMarker = "acknowledged";

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string markerPath;
    private readonly ILogger<JsonControlNoticeAcknowledgement>? logger;

    public JsonControlNoticeAcknowledgement(
        IHostEnvironment environment,
        ILogger<JsonControlNoticeAcknowledgement>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        markerPath = Path.Combine(
            environment.ContentRootPath,
            "UserData",
            "Control",
            "notice-acknowledged.txt");
        this.logger = logger;
    }

    public async Task<bool> IsAcknowledgedAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return File.Exists(markerPath)
                && string.Equals(
                    (await File.ReadAllTextAsync(markerPath, cancellationToken)
                        .ConfigureAwait(false)).Trim(),
                    SeenMarker,
                    StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "读不到控制页须知状态，按没看过处理。");
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AcknowledgeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(markerPath)
                ?? throw new InvalidOperationException("控制页须知状态的存放路径没有父目录。");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(markerPath, SeenMarker, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "存不下控制页须知状态。");
        }
        finally
        {
            gate.Release();
        }
    }
}
