using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// Fan Control Core（Apache-2.0）通过固定版本的 SDK 在后端内运行。
/// 用户决定：「去尝试并入后端，用net的C#部分肯定是高层逻辑，底层应该不变，或许可能并入。」
/// 「这个作为优先级比较低的任务，让子代理并行做就行」
/// 托管异常在这里变成失败请求；同一进程内不能隔离原生内存破坏或驱动故障，等待超时也不能中止原生调用。
/// 正常退出交还临时接管；崩溃后先按持久记录恢复，再允许新写入。持久固件曲线保留。
/// 独立标准输入输出宿主仍供其他调用方使用。
/// </summary>
public sealed partial class FanControlCoreClient(
    IHostEnvironment environment,
    ILogger<FanControlCoreClient>? logger = null) : IDisposable, IAsyncDisposable, IControlDetectionCache
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim temperatureGate = new(1, 1);
    private readonly object sessionSync = new();
    private TaskCompletionSource drained = CompletedSignal();
    private volatile bool sessionStopped = true;
    private IFanControlCoreSession? session;
    private Func<string, string?, IFanControlCoreSession>? sessionFactory;
    private int activeCalls;
    private string? commandOperation;
    private bool temperatureInFlight;
    private IFanControlCoreSession? closingSession;
    internal string InFlightOperation
    {
        get
        {
            lock (sessionSync)
                return string.Join("; ", new[] {
                    commandOperation, temperatureInFlight ? "temperature notification" : null,
                    (session ?? closingSession)?.InFlightOperation,
                    closingSession is not null ? "shutdown restoration/disposal" : null
                }.Where(value => value is not null)) is { Length: > 0 } detail ? detail : "shutdown drain";
        }
    }
    private long nextRequestId;
    private readonly HashSet<string> reportedDiagnostics = [];
    private void ReportDiagnostic(string detail)
    {
        lock (reportedDiagnostics)
            if (!reportedDiagnostics.Add(detail)) return;
        logger?.LogWarning("Fan Control Core: {Detail}", detail);
    }
    private volatile bool disposed;
    private bool refreshDetection;

    internal FanControlCoreClient(IHostEnvironment environment,
        Func<string, string?, IFanControlCoreSession> sessionFactory, ILogger<FanControlCoreClient>? logger = null)
        : this(environment, logger) => this.sessionFactory = sessionFactory;

    /// <summary>
    /// 这台机器上有哪几个风扇、各自叫什么。
    ///
    /// **这是"有什么"，不是"现在多少"。** 普通读取只返回已探测能力；
    /// DiscoverAsync 显式探测，ResetDetection 仅使清单失效。
    ///
    /// 缓存是必须的，不是优化：目录每列一次可控对象都要问它，
    /// 每次重探底层通道都可能耗时数秒。
    /// </summary>
    public IReadOnlyList<FanCoreFan> Describe() => Volatile.Read(ref describedFans) ?? [];

    /// <summary>Invalidate the finite inventory result; ordinary reads stay passive.</summary>
    public void ResetDetection()
    {
        lock (describeGate)
        {
            describedFans = null;
            refreshDetection = true;
        }
    }

    /// <summary>Finite discovery holds the session resource without starting a business block.</summary>
    public async Task<IReadOnlyList<FanCoreFan>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref describedFans) is { } cached) return cached;
        var discovery = DiscoverOwnedAsync(cancellationToken);
        try { return await discovery.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException error)
        {
            logger?.LogWarning(error, "Fan Control Core inventory discovery timed out; its resource remains owned until completion.");
            return [];
        }
    }

    private async Task<IReadOnlyList<FanCoreFan>> DiscoverOwnedAsync(CancellationToken cancellationToken)
    {
        await discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref describedFans) is { } cached) return cached;
            var held = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Run(() => ExecuteRequest(held, "describe",
                        refreshDetection ? new Dictionary<string, object?> { ["refresh"] = true } : null)).ConfigureAwait(false);
                    return Describe();
                }
                finally { gate.Release(); }
            }
            finally { await ReleaseSessionAsync().ConfigureAwait(false); }
        }
        finally { discoveryGate.Release(); }
    }

    private void ObserveDescription(JsonElement response)
    {
        if (!response.TryGetProperty("fans", out var fans) || fans.ValueKind != JsonValueKind.Array) return;
        ObserveOpen(response);
        var backend = Text(response, "backend");
        if (Text(response, "reason") is { } detail) ReportDiagnostic(detail);
        foreach (var fan in fans.EnumerateArray())
            if (Text(fan, "firmwareCurveRequires") is { } prerequisite) ReportDiagnostic(prerequisite);
        if (response.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.ValueKind == JsonValueKind.Array)
            foreach (var entry in diagnostics.EnumerateArray())
                if (entry.GetString() is { } text) ReportDiagnostic(text);
        lock (describeGate)
        {
            unavailableReason = Flag(response, "available") ? null : "fan-channel-missing";
            Volatile.Write(ref describedFans, ParseFans(fans, backend));
            refreshDetection = false;
        }
    }
    internal static IReadOnlyList<FanCoreFan> ParseFans(JsonElement fans, string? backend)
    {
        var list = new List<FanCoreFan>();
        foreach (var fan in fans.EnumerateArray())
        {
            if (!fan.TryGetProperty("index", out var index)
                || index.ValueKind != JsonValueKind.Number)
            {
                continue;
            }
            list.Add(new FanCoreFan(
                index.GetInt32(),
                null,
                backend,
                Flag(fan, "writable"),
                Flag(fan, "supportsDuty"),
                Text(fan, "role") ?? ControlFanRoles.Unknown,
                Text(fan, "firmwareCurve"),
                Flag(fan, "supportsSoftwareCurve"),
                Flag(fan, "softwareTakeoverCoversAllFans"),
                Integer(fan, "curvePoints"),
                Integer(fan, "minimumOnPercent") ?? 0,
                Flag(fan, "requiresPeriodicReassert"),
                FirmwareCurveRequires: Text(fan, "firmwareCurveRequires") is null ? null : "fan-firmware-prerequisite",
                SupportsFirmwareCurveReset: Flag(fan, "supportsFirmwareCurveReset")));
        }
        return list;
    }

    private static string? Text(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Integer(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static bool Flag(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// 读取核心的曲线回执，无法确认完整读值时返回 null。
    /// 每次显式编辑都重新读取；OEM 工具或性能档位可能已经改变固件表。
    /// </summary>
    public async Task<IReadOnlyList<ControlCurvePoint>?> ReadCurveAsync(
        int fanIndex,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                "curve",
                new Dictionary<string, object?> { ["fan"] = fanIndex },
                cancellationToken)
            .ConfigureAwait(false);
        if (response is not { } result) return null;
        var points = ParseCurveReceipt(result);
        if (points is null) return null;
        return points;
    }

    // 读写回执共用同一条边界：只有完整且成功的曲线回读才算确认值，不能把部分数据当成成功。
    internal static IReadOnlyList<ControlCurvePoint>? ParseCurveReceipt(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
            || !response.TryGetProperty("curve", out var curve) || curve.ValueKind != JsonValueKind.Array
            || curve.GetArrayLength() == 0) return null;

        var points = new List<ControlCurvePoint>();
        foreach (var point in curve.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Object
                || !point.TryGetProperty("temperatureCelsius", out var celsius) || celsius.ValueKind != JsonValueKind.Number
                || !celsius.TryGetDouble(out var temperature) || !double.IsFinite(temperature)
                || !point.TryGetProperty("percent", out var percent) || percent.ValueKind != JsonValueKind.Number
                || !percent.TryGetDouble(out var duty) || !double.IsFinite(duty) || duty < 0 || duty > 100) return null;
            points.Add(new(temperature, duty));
        }
        return points;
    }

    /// <summary>The current session has software plans or pending hand-back.</summary>
    public bool NeedsTemperatures() => SoftwareCurveRunNeeded;

    /// <summary>
    /// 这条通道用不上时，核心给的那句具体原因。用得上就是 null。
    ///
    /// 和风扇清单同一次 describe 的结果 —— 清单是空的时候，
    /// **为什么空**就在这里。
    /// </summary>
    public string? UnavailableReason => IsAvailable ? unavailableReason : openFailureReason;

    private volatile string? unavailableReason;

    /// <summary>
    /// 把这一刻的温度推给核心。
    ///
    /// **推不动就丢掉这一拍，不排队。** 推的是"现在多少"；通道正忙的时候
    /// 排一堆旧温度进去，等轮到它们时每一条都已经过期了 ——
    /// 核心那边宁可短暂收不到（它有自己的时效兜底），也不该按着过期的数调速。
    /// </summary>
    public async Task<bool> PushTemperaturesAsync(
        IReadOnlyDictionary<string, FanTemperatureSample> samples,
        CancellationToken cancellationToken,
        bool skipIfBusy = true)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var arguments = new Dictionary<string, object?>
        {
            ["samples"] = samples.ToDictionary(pair => pair.Key, pair => pair.Value.Celsius),
            ["observedAtUtcTicks"] = samples.ToDictionary(pair => pair.Key, pair => pair.Value.ObservedAtUtcTicks)
        };
        if (!skipIfBusy)
        {
            var response = await SendAsync("temp", arguments, cancellationToken).ConfigureAwait(false);
            return response is { } result && Flag(result, "ok");
        }
        // 温度只更新托管存储，仍像独立宿主一样与串行硬件命令分开，慢硬件不能挡住新温度。
        if (!await temperatureGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        IFanControlCoreSession? current;
        lock (sessionSync)
        {
            current = disposed || sessionStopped ? null : session;
            if (current is not null)
            {
                if (activeCalls++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                temperatureInFlight = true;
            }
        }
        if (current is null) { temperatureGate.Release(); return false; }
        var pending = Task.Run(() =>
        {
            try
            {
                arguments["op"] = "temp";
                arguments["notification"] = true;
                var request = JsonSerializer.SerializeToElement(arguments);
                var response = Handle(current, request, "temp");
                return response is { } result && Flag(result, "ok");
            }
            finally
            {
                lock (sessionSync) temperatureInFlight = false;
                EndCall(); temperatureGate.Release();
            }
        });
        try { return await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
    }

    private readonly object describeGate = new();
    private IReadOnlyList<FanCoreFan>? describedFans;

}

internal interface IFanControlCoreSession : IDisposable
{
    JsonElement Handle(JsonElement request);
    bool SoftwareCurveRunNeeded { get; }
    event Action<bool>? SoftwareCurveRunNeededChanged;
    Task RunSoftwareCurveAsync(CancellationToken stop);
    void SetDiagnosticSink(Action<string> sink) { }
    string? InFlightOperation => null;
}

/// <summary>
/// 风扇控制核心报上来的一个风扇：它是谁、归哪条通道。
///
/// **只有"有什么"，没有"现在多少"。** 转速和占空比属于读取那条路，
/// 混进来会让调用方以为问一次清单就顺带拿到了当前值。
/// </summary>
public sealed record FanCoreFan(
    int Index,
    string? Name,
    /// <summary>哪条通道报的，例如 uniwill-wmi。界面上作为补充说明显示。</summary>
    string? Detail,
    /// <summary>能不能写。只读的风扇照样列出来，用户要看得见它存在。</summary>
    bool Writable,
    /// <summary>能不能给任意占空比。给不了的机器上只有全速和自动两档。</summary>
    bool SupportsDuty,
    /// <summary>
    /// 这个风扇吹的是什么，取值见 <see cref="ControlFanRoles"/>。
    ///
    /// **由核心回答，我们不按下标猜。** 只有通道知道自己这台机器上 0 号是 CPU
    /// 还是显卡；按下标猜的话，每个用到它的地方都得各猜一遍，而且迟早猜错。
    /// </summary>
    string Role,
    /// <summary>
    /// 固件曲线是哪一种形态（见 <see cref="ControlFanFirmwareCurveKinds"/>），
    /// 没有固件曲线就是 null。
    /// </summary>
    string? FirmwareCurve,
    /// <summary>能不能由本程序接管跑曲线。前提是这条通道能设固定转速。</summary>
    bool SupportsSoftwareCurve,
    /// <summary>
    /// 软件接管这把风扇，会不会把这条通道上**所有**风扇一起接过来。
    ///
    /// 有的平台上"交给软件管"是整机一个开关，不是每把风扇一个 ——
    /// 那时没挂曲线的风扇会停在当时那个转速上不再自动调。
    /// **要在用户选之前就把这件事说清楚。**
    /// </summary>
    bool SoftwareTakeoverCoversAllFans,
    /// <summary>
    /// 固件曲线能写几个点。软件曲线和没有曲线的通道为 null。
    ///
    /// **界面上给几个可拖的点由它决定**，不写死成十个 ——
    /// ASUS 是 8 点、Gigabyte 15 点、Uniwill 16 区、Legion 10 档，各家不一样。
    /// </summary>
    int? CurvePoints,
    /// <summary>
    /// 风扇转得起来的最低占空比。0 表示没有这个死区。
    ///
    /// 这是**硬件死区**，不是我们的策略：低于它风扇只会停转。
    /// 不报出来的话，用户拖到 5% 会看到风扇停，而界面写着"已应用 5%"。
    /// </summary>
    int MinimumOnPercent,
    /// <summary>固件会周期性夺回控制权，软件曲线必须按期重下发才维持得住。</summary>
    bool RequiresPeriodicReassert,
    /// <summary>
    /// 固件曲线要满足什么条件才会被固件执行。没有前置条件就是 null。
    ///
    /// **和"写不写得进去"是两回事**：表能写进去、回读也一致，固件却可能压根
    /// 不拿它去跑风扇（联想只在自定义电源模式下执行）。满足不了就把这一项锁住、
    /// 把这句话原样给用户看，**不替他去改那个前置**。
    /// </summary>
    string? FirmwareCurveRequires = null,
    /// <summary>模块实现显式原表恢复。缺少此声明的旧表控制模块不能安全调整或撤销。</summary>
    bool SupportsFirmwareCurveReset = false);
