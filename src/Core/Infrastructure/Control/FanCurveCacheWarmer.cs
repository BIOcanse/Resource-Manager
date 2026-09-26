using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 开机之后把每把风扇现在跑的曲线先读进缓存。
///
/// **不读的话，用户第一次勾"转速曲线"要干等十几秒**（本机实测一次 11~18 秒：
/// 固件那三张表几十个字节，每个字节还得连读到一致才算数）。等不起的后果不是
/// "慢一点"，而是前端请求先超时、界面退回一条我们编的默认曲线 ——
/// 用户看到的就不是这台机器现在的行为了。
///
/// 所以在后台先读一次。读完之后那个值一直有效，因为**这张表只有被写才会变**；
/// 我们自己写完会把回读结果存回去，用户点"重新检测硬件"会清掉。
///
/// 只读，不写任何东西。读失败就算了 —— 那时用户勾选时会自己再读一次。
/// </summary>
public sealed class FanCurveCacheWarmer(
    FanControlCoreClient core,
    ILogger<FanCurveCacheWarmer>? logger = null) : BackgroundService
{
    /// <summary>
    /// 开机后先等一会儿。
    ///
    /// 这件事一点都不急：它要在用户**翻到控制页并勾选某一项之前**读完就行，
    /// 而开机那阵子机器本来就忙，抢在前面读几十次 EC 没有任何好处。
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(25);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            foreach (var fan in core.Describe())
            {
                if (fan.FirmwareCurve is null)
                {
                    // 没有固件曲线就没有"现在跑的那条"可读。
                    continue;
                }
                stoppingToken.ThrowIfCancellationRequested();
                var curve = await core.ReadCurveAsync(fan.Index, stoppingToken)
                    .ConfigureAwait(false);
                logger?.LogDebug(
                    "预读风扇 {Index} 的固件曲线：{Result}。",
                    fan.Index,
                    curve is null ? "没读到" : $"{curve.Count} 个点");
            }
        }
        catch (OperationCanceledException)
        {
            // 关机，正常路径。
        }
        catch (Exception error)
        {
            // 预读失败不影响任何功能：用户勾选时会自己再读一次，只是那次要等。
            logger?.LogDebug(error, "预读风扇曲线没成功。");
        }
    }
}
