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
/// 订阅持有需求租约时，采样在后台按固定间隔做，新结果先完整读出来再原子替换。
/// 没有订阅就停止采样并保留当前值。读取端只拿当前值，
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
    /// 需求出现后先等一会儿。开机那阵子驱动、辅助进程、监控快照都还没就绪，
    /// 这时候去读只会得到一堆"读不到"，然后把它当成当前值挂出去。
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(12);

    private readonly IReadOnlyList<IControlWriter> writers = writers.ToArray();
    private readonly TimeProvider clock = TimeProvider.System;
    private readonly object subscriptionGate = new();
    private readonly SemaphoreSlim demandChanged = new(0, 1);
    private CancellationTokenSource? demand;
    private int leases;
    private bool stopped;
    private int disposed;
    private volatile ControlActualState current = ControlActualState.Empty;

    internal ControlActualStateReader(IControlObjectCatalog catalog, IEnumerable<IControlWriter> writers,
        TimeProvider clock) : this(catalog, writers)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ControlActualState Current => current;

    public IDisposable AcquireSubscription()
    {
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (leases++ == 0)
            {
                demand = new CancellationTokenSource();
                if (demandChanged.CurrentCount == 0) demandChanged.Release();
            }
            return new SubscriptionLease(this);
        }
    }

    private void ReleaseSubscription()
    {
        lock (subscriptionGate)
        {
            if (--leases == 0) CancelDemand();
        }
    }

    // Called under subscriptionGate. The worker takes the token while holding the same gate.
    private void CancelDemand()
    {
        demand?.Cancel();
        demand?.Dispose();
        demand = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await demandChanged.WaitAsync(stoppingToken).ConfigureAwait(false);
                CancellationToken demandToken;
                lock (subscriptionGate)
                {
                    if (demand is null) continue;
                    demandToken = demand.Token;
                }
                using var sampling = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, demandToken);
                try
                {
                    await Task.Delay(StartupDelay, clock, sampling.Token).ConfigureAwait(false);
                    using var timer = new PeriodicTimer(Interval, clock);
                    do
                    {
                        sampling.Token.ThrowIfCancellationRequested();
                        var next = await ReadOnceAsync(sampling.Token).ConfigureAwait(false);
                        lock (subscriptionGate)
                        {
                            // An obsolete read must not replace the value after the last lease exits.
                            if (!sampling.IsCancellationRequested) current = next;
                        }
                    }
                    while (await timer.WaitForNextTickAsync(sampling.Token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常收摊。
        }
        finally
        {
            lock (subscriptionGate)
            {
                stopped = true;
                CancelDemand();
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (subscriptionGate)
        {
            stopped = true;
            CancelDemand();
        }
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally
        {
            demandChanged.Dispose();
            base.Dispose();
        }
    }

    private sealed class SubscriptionLease(ControlActualStateReader owner) : IDisposable
    {
        private ControlActualStateReader? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseSubscription();
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // 采样失败不该让这条后台路断掉 —— 下一轮还有机会。
            // 这一轮读到多少算多少，不拿旧值假装是新的。
            logger?.LogWarning(error, "读实际状态时出错，这一轮只取到 {Count} 项。", values.Count);
        }
        return new ControlActualState(values, clock.GetUtcNow());
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
