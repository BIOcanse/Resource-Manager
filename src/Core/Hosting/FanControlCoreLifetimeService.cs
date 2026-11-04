using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Hosting;

/// <summary>
/// 运行期间持有风扇会话，首次取得时完成探测和恢复；退出时按主机期限等待收尾。
/// </summary>
internal sealed class FanControlCoreLifetimeService(
    FanControlCoreClient core,
    ILogger<FanControlCoreLifetimeService>? logger = null) : IHostedService
{
    internal Task StartupTask { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartupTask = core.StartSession();
        return StartupTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 本轮会话在工作线程上收尾；共享客户端只在 DI 最终销毁时永久关闭。
        var disposal = core.StopSessionAsync().AsTask();
        try { await Task.WhenAll(StartupTask, disposal).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var operation = StartupTask.IsCompleted
                ? core.InFlightOperation
                : $"startup describe/recovery; {core.InFlightOperation}";
            logger?.LogWarning("Fan Control Core shutdown deadline expired; still in flight: {Operation}. Durable takeover recovery remains for next startup.",
                operation);
        }
    }
}
