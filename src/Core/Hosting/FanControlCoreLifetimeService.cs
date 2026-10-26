using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Hosting;

/// <summary>
/// 风扇探测和恢复由本服务持有，但监控启动不能等它；退出时才按主机期限等待收尾。
/// </summary>
internal sealed class FanControlCoreLifetimeService(
    FanControlCoreClient core,
    ILogger<FanControlCoreLifetimeService>? logger = null) : IHostedService
{
    internal Task StartupTask { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        core.StartSession();
        // 风扇驱动可能很慢。只启动这一份任务，其余监控服务不依赖它的探测结果。
        StartupTask = Task.Run(() =>
        {
            try { core.Describe(); }
            catch (Exception error)
            {
                // 请求边界已处理的错误不会抛到这里；这里只记录启动任务自身漏出的异常。
                logger?.LogWarning(error, "Fan Control Core startup discovery/recovery failed.");
            }
        });
        return Task.CompletedTask;
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
