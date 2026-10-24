using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// 风扇控制核心的客户端。
///
/// 那个核心单独一个仓库（Apache-2.0，和本程序同一许可证），因为它要能被别人直接用 ——
/// 笔记本风扇控制这件事不该每个软件重造一遍。它按**控制通道**分类
/// （hwmon / WMI-ACPI / HID / Raw EC / SMM）而不按品牌，要维护的资产是机型 profile。
///
/// 通道和硬件写入辅助进程是同一套：标准输入输出上一行一条 JSON，随主程序启停。
/// 用独立进程而不是引一个库，是因为**碰风扇的代码崩了不该拖累主程序**，
/// 临时接管在退出时交还固件，持久固件曲线保留。
/// </summary>
public sealed partial class FanControlCoreClient(
    IHostEnvironment environment,
    ILogger<FanControlCoreClient>? logger = null) : IDisposable, IControlDetectionCache
{
    private const string ComponentId = "fan-control-core";
    private const string ExecutableName = "FanControlCore.exe";

    /// <summary>
    /// 一条请求等多久。读一次要走十几次 EC 往返（每个字节还要读三遍取一致），
    /// 所以比一般的 IPC 宽松，但不能无限等 —— 卡住的话整个控制页都会跟着卡。
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private Process? process;
    private ControlHelperErrorOutput? errorOutput;
    private long nextRequestId;
    private volatile bool disposed;
    private Func<ProcessStartInfo, Process?>? startFactory;
    internal FanControlCoreClient(IHostEnvironment environment, Func<ProcessStartInfo, Process?> startFactory)
        : this(environment, (ILogger<FanControlCoreClient>?)null) => this.startFactory = startFactory;
    private volatile bool stopping;
    private volatile bool temperatureNotifications;
    private bool refreshDetection;

    public bool IsInstalled => ResolveExecutablePath() is not null;

    /// <summary>
    /// 这台机器上有哪几个风扇、各自叫什么。
    ///
    /// **这是"有什么"，不是"现在多少"。** 普通 describe 返回已探测能力；
    /// 显式 ResetDetection 后才重新探测，其他读取复用当前清单。
    ///
    /// 缓存是必须的，不是优化：目录每列一次可控对象都要问它，
    /// 而每次起一个子进程握一次手，列一次对象就要好几秒。
    /// </summary>
    public IReadOnlyList<FanCoreFan> Describe()
    {
        lock (describeGate)
        {
            if (describedFans is { } cached)
            {
                return cached;
            }
            describedFans = ReadDescription();
            return describedFans;
        }
    }

    /// <summary>
    /// 把缓存的风扇清单丢掉，下次问的时候重新探一遍。
    ///
    /// 给"检测平台"用：用户装上组件、换了机器状态之后，
    /// 他要的是重新问一遍硬件，而不是看我们开机时记下的那份答案。
    /// </summary>
    public void ResetDetection()
    {
        lock (describeGate)
        {
            describedFans = null;
            refreshDetection = true;
        }
    }

    private IReadOnlyList<FanCoreFan> ReadDescription()
    {
        if (!IsInstalled)
        {
            return [];
        }

        JsonElement? response;
        try
        {
            response = SendAsync("describe", refreshDetection ? new Dictionary<string, object?> { ["refresh"] = true } : null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return [];
        }

        if (response is not { } result
            || !result.TryGetProperty("fans", out var fans)
            || fans.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var backend = result.TryGetProperty("backend", out var name)
            && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        temperatureNotifications = Flag(result, "temperatureNotifications");
        /*
         * 通道用不上时那句**具体**原因。
         *
         * "这台机器上没有 Uniwill 的 ACPI WMI 接口" 和 "没有管理员权限" 对用户
         * 是完全不同的两件事，笼统说一句"没有可用的风扇控制通道"，
         * 前者只能去换机器，后者其实重启一下就好了。
         */
        unavailableReason = Flag(result, "available") ? null : Text(result, "reason");

        refreshDetection = false;
        return ParseFans(fans, backend);
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
            var label = fan.TryGetProperty("name", out var fanName)
                && fanName.ValueKind == JsonValueKind.String
                    ? fanName.GetString()
                    : null;
            list.Add(new FanCoreFan(
                index.GetInt32(),
                string.IsNullOrWhiteSpace(label) ? $"风扇 {index.GetInt32()}" : label!,
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
                FirmwareCurveRequires: Text(fan, "firmwareCurveRequires"),
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

    // Read and write receipts share one wire boundary. Never publish a partial
    // curve or turn an unsuccessful response into a confirmed value.
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

    /// <summary>
    /// 只有本客户端已成功应用、尚未撤销的软件曲线需要温度；能力探测不是需求。
    /// </summary>
    public bool NeedsTemperatures()
    {
        lock (softwareCurveGate) return softwareCurves.Count > 0;
    }

    private readonly object softwareCurveGate = new();
    private readonly HashSet<int> softwareCurves = [];
    internal event Action? TemperatureDemandChanged;

    private void UpdateSoftwareCurve(int fanIndex, bool active)
    {
        bool changed;
        lock (softwareCurveGate)
        {
            var needed = softwareCurves.Count > 0;
            if (active && !disposed && !stopping) softwareCurves.Add(fanIndex);
            else softwareCurves.Remove(fanIndex);
            changed = needed != (softwareCurves.Count > 0);
        }
        if (changed) TemperatureDemandChanged?.Invoke();
    }

    private void ClearSoftwareCurves(Process? owner = null)
    {
        bool changed;
        lock (softwareCurveGate)
        {
            if (owner is not null && !ReferenceEquals(process, owner)) return;
            changed = softwareCurves.Count > 0;
            softwareCurves.Clear();
        }
        if (changed) TemperatureDemandChanged?.Invoke();
    }

    private void ObserveSoftwareCurveReceipt(string operation, IReadOnlyDictionary<string, object?>? arguments, JsonElement response)
    {
        if (arguments is null || !arguments.TryGetValue("fan", out var value) || value is not int fanIndex
            || !Flag(response, "ok")) return;
        if (operation == "setcurve" && ParseCurveReceipt(response) is not null
            && Text(response, "execution") is ControlFanCurveExecutions.Software or ControlFanCurveExecutions.Firmware)
        {
            UpdateSoftwareCurve(fanIndex, Text(response, "execution") == ControlFanCurveExecutions.Software);
        }
        else if (operation is "set" or "boost"
            || operation == "auto" && response.TryGetProperty("fans", out var fans) && fans.ValueKind == JsonValueKind.Array
                && fans.EnumerateArray().Any(fan => Integer(fan, "index") == fanIndex && Flag(fan, "automaticControl")))
        {
            UpdateSoftwareCurve(fanIndex, false);
        }
        else if (operation == "curve" && Text(response, "execution") != ControlFanCurveExecutions.Software)
        {
            UpdateSoftwareCurve(fanIndex, false);
        }
    }

    /// <summary>
    /// 这条通道用不上时，核心给的那句具体原因。用得上就是 null。
    ///
    /// 和风扇清单同一次 describe 的结果 —— 清单是空的时候，
    /// **为什么空**就在这里。
    /// </summary>
    public string? UnavailableReason
    {
        get
        {
            lock (describeGate)
            {
                describedFans ??= ReadDescription();
            }
            return unavailableReason;
        }
    }

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
        if (temperatureNotifications && skipIfBusy)
        {
            if (!await inputGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
            try
            {
                if (stopping || process is not { HasExited: false } running) return false;
                arguments["op"] = "temp";
                arguments["notification"] = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                await running.StandardInput.WriteLineAsync(JsonSerializer.Serialize(arguments).AsMemory(), timeout.Token).ConfigureAwait(false);
                await running.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
            {
                logger?.LogDebug(error, "温度通知未送达。");
                return false;
            }
            finally
            {
                inputGate.Release();
                if (disposed || stopping) Stop();
            }
        }
        // Older installed cores still use acknowledged, serialized temperature requests.
        var response = await SendAsync(
                "temp",
                arguments,
                cancellationToken,
                skipIfBusy: skipIfBusy)
            .ConfigureAwait(false);
        return response is { } result && Flag(result, "ok");
    }

    private readonly object describeGate = new();
    private IReadOnlyList<FanCoreFan>? describedFans;

    public async Task<JsonElement?> SendAsync(
        string operation,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken,
        /*
         * 通道正忙就直接放弃这一次，不排队等。
         *
         * 只有"现在多少"这类推送该这么做：排队等于把一串已经过期的值
         * 慢慢灌进去。真正的请求（读曲线、写设定）不能丢，所以默认是等。
         */
        bool skipIfBusy = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (skipIfBusy)
        {
            if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }
        else
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        var commandStarted = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var request = new Dictionary<string, object?>(
                arguments ?? new Dictionary<string, object?>())
            {
                ["id"] = Interlocked.Increment(ref nextRequestId),
                ["op"] = operation
            };

            // Once a hardware operation has started, a closed page must not interrupt its write or restoration.
            using var timeout = new CancellationTokenSource(RequestTimeout);
            Process? running;
            await inputGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (disposed) return null;
                running = EnsureStarted();
                if (running is null || disposed) return null; // Startup may finish after Dispose.
                commandStarted = true;
                await running.StandardInput
                    .WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token)
                    .ConfigureAwait(false);
                await running.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                inputGate.Release();
                if (disposed || stopping) Stop();
            }

            var line = await running.StandardOutput.ReadLineAsync(timeout.Token)
                .ConfigureAwait(false);
            if (line is null)
            {
                if (errorOutput is { } diagnostics)
                {
                    try { await diagnostics.Completion.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                    unavailableReason = diagnostics.FailureReason;
                    logger?.LogWarning("Fan helper closed output: {Detail}", diagnostics.Detail);
                }
                Stop();
                return null;
            }
            using var document = JsonDocument.Parse(line);
            ObserveSoftwareCurveReceipt(operation, arguments, document.RootElement);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!commandStarted && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException
            or JsonException
            or InvalidOperationException
            or OperationCanceledException)
        {
            logger?.LogWarning(error, "风扇控制核心这次没应答（{Operation}）。", operation);
            Stop();
            return null;
        }
        finally
        {
            gate.Release();
            if (disposed || stopping) Stop();
        }
    }

    private Process? EnsureStarted()
    {
        if (process is { HasExited: false })
        {
            return stopping ? null : process;
        }
        // Called under inputGate: an exited process has no pending input ownership.
        ClearSoftwareCurves();
        process?.Dispose();
        process = null;

        if (ResolveExecutablePath() is not { } executablePath)
        {
            return null;
        }
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Both telemetry and the desktop fan backend use the product's official core DLL.
            // The host supplies the path; the child never searches a retired GUI installation.
            if (ResolveLibreHardwareMonitorPath() is { } libraryPath)
            {
                start.Environment["FANCONTROLCORE_LHM_PATH"] = libraryPath;
            }
            start.Environment["FANCONTROLCORE_STATE_DIRECTORY"] = Path.Combine(
                PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), "UserData", "Control", "FanControlCore");
            process = startFactory is not null ? startFactory(start) : Process.Start(start);
            if (process is { } child)
            {
                child.Exited += (_, _) => ClearSoftwareCurves(child);
                child.EnableRaisingEvents = true;
            }
            errorOutput = process is null ? null : new(process.StandardError);
            stopping = false;
            temperatureNotifications = false;
            return process;
        }
        catch (Exception error) when (error is IOException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "起不来风扇控制核心。");
            process = null;
            return null;
        }
    }

    /// <summary>
    /// Product-shipped official core library directory, shared with the fan helper.
    /// </summary>
    private string? ResolveLibreHardwareMonitorPath()
    {
        // The official core library ships with this product; never depend on the retired GUI install.
        var path = Path.GetDirectoryName(typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.Location);
        return path is not null && File.Exists(Path.Combine(path, "LibreHardwareMonitorLib.dll")) ? path : null;
    }

    private string? ResolveExecutablePath()
    {
        var path = Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "Dependencies",
            ComponentId,
            ExecutableName);
        return File.Exists(path) ? path : null;
    }

    private void Stop()
    {
        // StreamWriter cannot be closed while WriteLineAsync/FlushAsync is in progress.
        // The active writer finishes the accepted line and calls Stop after releasing inputGate.
        stopping = true;
        ClearSoftwareCurves();
        if (!inputGate.Wait(0)) return;
        var running = process;
        try
        {
            if (running is null) return;
            if (!running.HasExited)
            {
                // **关标准输入而不是杀进程**：那个核心读到流尾会把风扇交还固件再退出。
                // 直接杀掉会把风扇留在我们设的转速上 —— 那正是它要避免的事。
                running.StandardInput.Close();
                // A slow EC transaction must finish before normal EOF restoration. Keep its ownership
                // until exit instead of killing it or starting a second hardware owner.
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            // 已经没了，正是想要的结果。
        }
        finally
        {
            try
            {
                // Keep output available until the command has consumed its reply.
                if (running is { HasExited: true } && gate.Wait(0))
                {
                    try
                    {
                        running.Dispose();
                        process = null;
                        stopping = false;
                    }
                    finally { gate.Release(); }
                }
            }
            finally { inputGate.Release(); }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Stop();
        // Pending command/temperature writers must still release these managed semaphores.
    }
}

/// <summary>
/// 风扇控制核心报上来的一个风扇：它是谁、归哪条通道。
///
/// **只有"有什么"，没有"现在多少"。** 转速和占空比属于读取那条路，
/// 混进来会让调用方以为问一次清单就顺带拿到了当前值。
/// </summary>
public sealed record FanCoreFan(
    int Index,
    string Name,
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
