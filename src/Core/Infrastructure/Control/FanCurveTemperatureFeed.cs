using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

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
///   0.1 秒推送让核心及时看到最新读数，再按曲线目标和加速度调速。
///   核心不对温度做滤波；放大推送间隔只会增加读数等待，拖慢对温度上冲的响应。</item>
/// <item><b>用不上就一次都不推。</b> 只有成功应用的软件曲线持有温度订阅，
///   最后一条撤销或核心退出时立即释放；支持软件曲线不等于正在执行。</item>
/// <item><b>推送停了要能被发现。</b> 推的是"现在多少"，核心那边带时效：
///   连续几秒收不到就把风扇交还固件。所以这里**不补推历史、不排队**，
///   赶不上的那一拍直接丢掉 —— 迟到的温度比没有温度更糟。</item>
/// </list>
/// </summary>
public sealed class FanCurveTemperatureFeed(
    IMetricSnapshotObservationSource metrics,
    FanControlCoreClient core,
    DashboardMonitoringCatalogState catalog,
    ILogger<FanCurveTemperatureFeed>? logger = null) : BackgroundService
{
    /// <summary>
    /// 订阅与推送的间隔。**这个必须小**，理由见类上那段。
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private const string SubscriptionId = "control.fan-curve.temperature";
    internal static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(3);
    private readonly object subscriptionGate = new();
    private readonly SemaphoreSlim activated = new(0, 1);
    private IDisposable? lease;
    private MetricSampleRequest? request;
    private bool finished;

    private void UpdateSubscription()
    {
        try { UpdateSubscriptionCore(); }
        catch (Exception error)
        {
            lock (subscriptionGate) request = null;
            logger?.LogWarning(error, "Fan temperature subscription callback failed; curves will release on stale input.");
        }
    }

    private void UpdateSubscriptionCore()
    {
        lock (subscriptionGate)
        {
            lease?.Dispose();
            lease = null;
            request = null;
            if (finished || !core.NeedsTemperatures()) return;
            request = TemperatureRequest(catalog.Current);
            lease = metrics.AcquireSubscription(SubscriptionId, request, Interval);
            if (activated.CurrentCount == 0) activated.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            core.TemperatureDemandChanged += UpdateSubscription;
            catalog.Changed += UpdateSubscription;
            UpdateSubscription();
            while (!stoppingToken.IsCancellationRequested)
            {
                await activated.WaitAsync(stoppingToken).ConfigureAwait(false);
                using var timer = new PeriodicTimer(Interval);
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    MetricSampleRequest? currentRequest;
                    lock (subscriptionGate) currentRequest = request;
                    if (currentRequest is null) break;
                    try
                    {
                        // ReadLatest only reads; sampling belongs to the lease.
                        if (metrics.ReadLatest(currentRequest) is { } snapshot
                            && Collect(snapshot, DateTimeOffset.UtcNow) is { Count: > 0 } samples)
                        {
                            await core.PushTemperaturesAsync(samples, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger?.LogDebug(error, "这一拍温度没推过去。");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 关机，正常路径。
        }
        catch (Exception error)
        {
            logger?.LogError(error, "Fan temperature feed stopped after a failure; curves will release on stale input.");
        }
        finally
        {
            core.TemperatureDemandChanged -= UpdateSubscription;
            catalog.Changed -= UpdateSubscription;
            lock (subscriptionGate)
            {
                finished = true;
                try { lease?.Dispose(); }
                catch (Exception error) { logger?.LogWarning(error, "Fan temperature subscription release failed."); }
                lease = null;
                request = null;
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
    internal static MetricSampleRequest TemperatureRequest(HardwareMetricSnapshot? snapshot)
    {
        return MetricSampleRequest.ForIds(new[] { "cpu.temperature" }.Concat(
            snapshot?.Items.Keys.Where(id => id.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase)
                && id.EndsWith(".temperature", StringComparison.OrdinalIgnoreCase)) ?? []));
    }

    internal static Dictionary<string, FanTemperatureSample> Collect(HardwareMetricSnapshot snapshot, DateTimeOffset now)
    {
        var samples = new Dictionary<string, FanTemperatureSample>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in TemperatureRequest(snapshot).Ids)
        {
            if (!snapshot.TryGetCurrentDataset(id, out var observation)
                || observation.ObservedAtUtcTicks <= 0 || observation.ObservedAtUtcTicks > now.UtcTicks
                || now.UtcTicks - observation.ObservedAtUtcTicks > MaximumSampleAge.Ticks
                || !snapshot.Items.TryGetValue(id, out var item)
                || item.NumericValue is not { } celsius || !double.IsFinite(celsius)) continue;
            if (id.Equals("cpu.temperature", StringComparison.OrdinalIgnoreCase))
                samples["cpu"] = new(celsius, observation.ObservedAtUtcTicks);
            else
            {
                var parts = id.Split('.');
                if (parts.Length == 3 && int.TryParse(parts[1], out var index) && index >= 0)
                    samples[$"gpu{index}"] = new(celsius, observation.ObservedAtUtcTicks);
            }
        }
        return samples;
    }
}

public sealed record FanTemperatureSample(double Celsius, long ObservedAtUtcTicks);
