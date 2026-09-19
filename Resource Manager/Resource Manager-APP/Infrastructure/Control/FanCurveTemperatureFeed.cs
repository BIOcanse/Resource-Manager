using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 给软件风扇曲线喂温度。
///
/// **风扇控制核心自己没有传感器**，它只会读写风扇 —— 这是故意的：
/// 一套完整的温度采集本程序早就有了，不该在那个核心里再长一套。
/// 所以温度由这里订阅、按期推过去。
///
/// 三件事值得单独说：
///
/// <list type="bullet">
/// <item><b>间隔 0.1 秒，而且必须这么小。</b> 温度能在一秒内冲上去十几度；
///   一秒一次的话，每一次调速看到的都是已经发生过的事。核心那边的滤波窗口
///   是按**样本数**算的（13 个），间隔放大多少倍，滞后就放大多少倍 ——
///   0.1 秒下那个窗口是 1.3 秒，一秒下就成了 13 秒，风扇会明显迟钝。</item>
/// <item><b>用不上就一次都不推。</b> 走固件曲线的机器（曲线交给固件自己查表）
///   根本不需要我们参与，核心会在 describe 里说 <c>needsTemperatures=false</c>，
///   那时这条订阅连起都不起 —— 不做无用功。</item>
/// <item><b>推送停了要能被发现。</b> 推的是"现在多少"，核心那边带时效：
///   连续几秒收不到就把风扇交还固件。所以这里**不补推历史、不排队**，
///   赶不上的那一拍直接丢掉 —— 迟到的温度比没有温度更糟。</item>
/// </list>
/// </summary>
public sealed class FanCurveTemperatureFeed(
    IMetricSnapshotObservationSource metrics,
    FanControlCoreClient core,
    ILogger<FanCurveTemperatureFeed>? logger = null) : BackgroundService
{
    /// <summary>
    /// 订阅与推送的间隔。**这个必须小**，理由见类上那段。
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 启动后先等一会儿再问核心要不要温度。
    ///
    /// 问它就要起一次子进程握手，而开机那阵子本来就忙；
    /// 而且这台机器需不需要推温度，晚几秒知道没有任何损失。
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    /// <summary>不需要推的时候，隔多久再问一次。</summary>
    private static readonly TimeSpan IdleRecheck = TimeSpan.FromMinutes(5);

    private const string SubscriptionId = "control.fan-curve.temperature";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            // while，不递归。
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!core.NeedsTemperatures())
                {
                    /*
                     * 这台机器的曲线归固件执行，不需要我们喂温度。
                     *
                     * 隔一阵再问一次而不是就此收工：用户可能装上组件、
                     * 或者把某把风扇改成软件接管，那时就需要了。
                     */
                    await Task.Delay(IdleRecheck, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                await FeedAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 关机，正常路径。
        }
    }

    private async Task FeedAsync(CancellationToken stoppingToken)
    {
        /*
         * 只订这几项。
         *
         * **不要图省事订 All**：那会让整条采集链路按 0.1 秒去跑所有指标
         * （进程列表、磁盘、网络……），为了两个温度读数把整机开销抬上去。
         * 订阅的频率是按逻辑项算的，订得越窄，代价越小。
         */
        var request = MetricSampleRequest.ForIds(["cpu.temperature", "gpu.temperature"]);
        using var lease = metrics.AcquireSubscription(SubscriptionId, request, Interval);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!core.NeedsTemperatures())
            {
                // 情况变了（比如用户把曲线改回固件执行），退出去重新判断。
                return;
            }
            try
            {
                // 读当前值，**不触发采样也不等待采样** —— 采样是订阅那条独立的路。
                if (metrics.ReadLatest(request) is not { } snapshot)
                {
                    continue;
                }
                if (Collect(snapshot) is { Count: > 0 } samples)
                {
                    await core.PushTemperaturesAsync(samples, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                /*
                 * 一拍没推成不该把这条喂料停掉 —— 停了核心那边会因为收不到温度
                 * 把风扇交还固件，而问题可能只是这一次没赶上。
                 * 真的一直推不动，核心自己的时效机制会兜底。
                 */
                logger?.LogDebug(error, "这一拍温度没推过去。");
            }
        }
    }

    /// <summary>
    /// 把快照里的温度整理成核心认的那几个名字。
    ///
    /// 名字的约定很简单：<c>cpu</c>、<c>gpu0</c>、<c>gpu1</c>……
    /// 核心那边按前缀决定哪把风扇看哪一路（显卡风扇只看 gpu 开头的）。
    ///
    /// **读不到就不放进去**，不要填 0 —— 那会让曲线以为现在很凉。
    /// </summary>
    private static Dictionary<string, double> Collect(HardwareMetricSnapshot snapshot)
    {
        var samples = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.Cpu.Sensors.TemperatureCelsius is { } cpu && double.IsFinite(cpu))
        {
            samples["cpu"] = cpu;
        }
        foreach (var gpu in snapshot.Gpus)
        {
            if (gpu.Sensors.TemperatureCelsius is { } celsius && double.IsFinite(celsius))
            {
                samples[$"gpu{gpu.Index}"] = celsius;
            }
        }
        return samples;
    }
}
