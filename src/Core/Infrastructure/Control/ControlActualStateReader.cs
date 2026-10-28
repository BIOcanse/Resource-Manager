using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.Subscription;

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
/// 订阅表发布是否在用，父决策节点决定本块启停。运行时按固定间隔采样，
/// 新结果先完整读出来再原子替换；停止后保留当前值。读取端只拿当前值，
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
    private int leases;
    private bool actualControlValuesInUse;
    private bool fanReadingsInUse;
    private int disposed;
    private volatile ControlActualState current = ControlActualState.Empty;
    private readonly SamplingPublicationSignal publications = new();
    private const string ActualDataset = "control.actual";

    internal ControlActualStateReader(IControlObjectCatalog catalog, IEnumerable<IControlWriter> writers,
        TimeProvider clock) : this(catalog, writers)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ControlActualState Current => current;

    internal bool ActualControlValuesInUse => Volatile.Read(ref actualControlValuesInUse);
    internal event Action? SubscriptionsChanged;
    internal bool FanReadingsInUse => Volatile.Read(ref fanReadingsInUse);
    internal event Action? FanReadingsChanged;

    public Task<ControlActualState?> FetchOnceAsync(string holder, CancellationToken cancellationToken)
        => SubscriptionSingleFetch.ReadAsync(SubscribeOnceAsync(holder, cancellationToken),
            static _ => true, cancellationToken);

    private async IAsyncEnumerable<ControlActualState> SubscribeOnceAsync(string holder,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = publications.Subscribe([ActualDataset]);
        using var lease = AcquireSubscription();
        logger?.LogDebug("Control actual-state subscription {Holder} acquired.", holder);
        try
        {
            // No initial cached delivery: this waits for the original worker,
            // including its normal startup delay and shared ten-second cadence.
            await publication.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return Current;
        }
        finally { logger?.LogDebug("Control actual-state subscription {Holder} released.", holder); }
    }

    public IDisposable AcquireSubscription()
    {
        bool changed;
        bool fanChanged;
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            changed = leases++ == 0;
            Volatile.Write(ref actualControlValuesInUse, true);
            fanChanged = RefreshFanReadingsLocked();
        }
        if (changed) SubscriptionsChanged?.Invoke();
        if (fanChanged) FanReadingsChanged?.Invoke();
        return new SubscriptionLease(this);
    }

    private void ReleaseSubscription()
    {
        bool changed;
        bool fanChanged;
        lock (subscriptionGate)
        {
            if (leases == 0) return;
            changed = --leases == 0;
            Volatile.Write(ref actualControlValuesInUse, leases != 0);
            fanChanged = RefreshFanReadingsLocked();
        }
        if (changed) SubscriptionsChanged?.Invoke();
        if (fanChanged) FanReadingsChanged?.Invoke();
    }

    // Called only when the subscription owner changes or refreshes its declarations.
    // State-authority reads load the published Boolean and never call the catalog.
    private bool RefreshFanReadingsLocked()
    {
        var next = leases != 0 && catalog.ActualReadingsUseFanCore;
        if (fanReadingsInUse == next) return false;
        Volatile.Write(ref fanReadingsInUse, next);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, clock, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval, clock);
            do
            {
                stoppingToken.ThrowIfCancellationRequested();
                var next = await ReadOnceAsync(stoppingToken).ConfigureAwait(false);
                lock (subscriptionGate)
                {
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        current = next;
                        publications.Publish([ActualDataset]);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常收摊。
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        finally
        {
            bool fanChanged;
            lock (subscriptionGate)
            {
                leases = 0;
                Volatile.Write(ref actualControlValuesInUse, false);
                fanChanged = RefreshFanReadingsLocked();
            }
            SubscriptionsChanged?.Invoke();
            if (fanChanged) FanReadingsChanged?.Invoke();
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
            var declared = await catalog.ReadObjectsAsync(cancellationToken).ConfigureAwait(false);
            bool fanChanged;
            lock (subscriptionGate) fanChanged = RefreshFanReadingsLocked();
            if (fanChanged) FanReadingsChanged?.Invoke();
            foreach (var controlObject in declared.Objects)
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
