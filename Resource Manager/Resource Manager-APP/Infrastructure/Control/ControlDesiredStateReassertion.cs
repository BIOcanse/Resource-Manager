using ResourceManager.App.Application.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 把已经存着的期望状态反复重新施加。
///
/// 这就是「用户设定一次就一直维持」的兑现方式。需要它是因为设定会被外力抹掉：
/// 重启、显卡驱动重置、笔记本睡醒、设备拔了再插、别的软件改了同一块卡。
/// 所以不能只在用户点确定那一下写一次。
///
/// 只在**有设定**的时候才动手（见 <see cref="IControlPlane.ReassertAsync"/>）——
/// 用户没设过就完全不碰这台机器，不去把它"重置"成我们以为的默认值。
/// </summary>
public sealed class ControlDesiredStateReassertion(
    IControlPlane plane,
    ILogger<ControlDesiredStateReassertion>? logger = null) : BackgroundService
{
    /// <summary>启动后先等一会儿：让设备目录先认全，免得对着空目录施加。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);

    /// <summary>之后的重新施加间隔。设定被外力抹掉时，最多这么久就会被重新按回去。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReassertOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 关机，正常路径。
        }
    }

    private async Task ReassertOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await plane.ReassertAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // 一次没成功不能让这条循环死掉 —— 下一轮还要接着维持。
            logger?.LogWarning(error, "重新施加控制设定失败，下一轮再试。");
        }
    }
}
