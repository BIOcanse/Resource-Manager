using ResourceManager.App.Application.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 超频免责声明的同意状态，存成一个小文件。
///
/// 读不到就当**没同意** —— 这一条只能往安全的方向猜：文件坏了、被删了、
/// 权限没了，都不该被解读成"用户同意接受部件寿命缩短"。
/// </summary>
public sealed class JsonControlOverclockConsent : IControlOverclockConsent
{
    private const string AcceptedMarker = "accepted";

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string consentPath;
    private readonly ILogger<JsonControlOverclockConsent>? logger;

    public JsonControlOverclockConsent(
        IHostEnvironment environment,
        ILogger<JsonControlOverclockConsent>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        consentPath = Path.Combine(
            environment.ContentRootPath,
            "UserData",
            "Control",
            "overclock-consent.txt");
        this.logger = logger;
    }

    public async Task<bool> IsAcceptedAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return File.Exists(consentPath)
                && string.Equals(
                    (await File.ReadAllTextAsync(consentPath, cancellationToken)
                        .ConfigureAwait(false)).Trim(),
                    AcceptedMarker,
                    StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 读不到就当没同意。往安全的方向猜。
            logger?.LogWarning(error, "读不到超频同意状态，按未同意处理。");
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetAcceptedAsync(bool accepted, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(consentPath)
                ?? throw new InvalidOperationException("超频同意状态的存放路径没有父目录。");
            Directory.CreateDirectory(directory);
            if (accepted)
            {
                await File.WriteAllTextAsync(consentPath, AcceptedMarker, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (File.Exists(consentPath))
            {
                // 撤回就是把记录删掉，而不是写一个"拒绝" —— 没有记录本来就等于没同意。
                File.Delete(consentPath);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "存不下超频同意状态。");
        }
        finally
        {
            gate.Release();
        }
    }
}
