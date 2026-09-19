using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 实际状态：这台机器现在实际是什么样。
///
/// **它不是期望状态的回声。** 固件会按温度自己调度，用户也可能用别的软件改过 ——
/// 两者对不上是常态，而且正是用户需要看见的信息，所以两份都留着，不互相覆盖。
///
/// 这一份和统一写入层是**分开**的两条路：写走写入层，读走这里再进统一订阅源。
/// 分开之后，两边各按各的节奏跑，不会因为共用一个对象而互相等锁。
///
/// 采样在后台按固定间隔做，新结果先完整读出来再原子替换。读取端只拿当前值，
/// 不触发采样也不等待 —— 订阅者再多，硬件那一侧也只有这一条采样路。
/// </summary>
public sealed class ControlActualStateReader(
    IControlObjectCatalog catalog,
    IEnumerable<IControlWriter> writers,
    ILogger<ControlActualStateReader>? logger = null)
    : BackgroundService, IControlActualStateOwner
{
    /// <summary>
    /// 采样间隔。
    ///
    /// 控制面这些量变化不快（功耗上限、频率偏移、电压档位都是用户设了才变），
    /// 但每次采样都要走 SMU 邮箱或驱动调用，压得太密只会和写入抢同一条通道。
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 启动后先等一会儿。开机那阵子驱动、辅助进程、监控快照都还没就绪，
    /// 这时候去读只会得到一堆"读不到"，然后把它当成当前值挂出去。
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(12);

    private readonly IReadOnlyList<IControlWriter> writers = writers.ToArray();
    private volatile ControlActualState current = ControlActualState.Empty;

    public ControlActualState Current => current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                // 先完整读出来，再原子替换 —— 半份结果不该被谁看见。
                current = await ReadOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常收摊。
        }
    }

    private async Task<ControlActualState> ReadOnceAsync(CancellationToken cancellationToken)
    {
        var values = new List<ControlActualValue>();
        try
        {
            foreach (var controlObject in catalog.ReadObjects().Objects)
            {
                foreach (var capability in controlObject.Capabilities)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ReadOne(controlObject, capability, cancellationToken) is { } task
                        && await task.ConfigureAwait(false) is { } value)
                    {
                        values.Add(value);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // 采样失败不该让这条后台路断掉 —— 下一轮还有机会。
            // 这一轮读到多少算多少，不拿旧值假装是新的。
            logger?.LogWarning(error, "读实际状态时出错，这一轮只取到 {Count} 项。", values.Count);
        }
        return new ControlActualState(values, DateTimeOffset.UtcNow);
    }

    private Task<ControlActualValue?>? ReadOne(
        ControlObject controlObject,
        ControlCapability capability,
        CancellationToken cancellationToken)
    {
        foreach (var writer in writers)
        {
            var availability = writer.Probe(controlObject, capability);
            if (!availability.IsMine)
            {
                continue;
            }
            /*
             * **判据是"读不读得到"，不是"能不能写"。**
             *
             * 先前这里写的是"调不了的项也不去读，读不到是必然的" —— 那个前提是错的：
             * 显卡的温度墙、降速阈值、保护关机阈值在消费级卡上全都读得到
             * （89 / 102 / 105），只是驱动不接受改。那几个数对用户有用，
             * 界面会把它们单独列成只读项。
             *
             * 仍然不去读"既写不了也读不到"的那些 —— 那才是徒增一次硬件调用。
             */
            return availability.IsReadable
                ? writer.ReadAsync(controlObject, capability, cancellationToken)
                : null;
        }
        return null;
    }
}
