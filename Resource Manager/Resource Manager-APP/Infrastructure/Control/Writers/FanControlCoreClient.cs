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
/// 而且那个核心自己带一条不可协商的规矩：退出时把风扇交还固件。
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
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private long nextRequestId;
    private bool disposed;

    public bool IsInstalled => ResolveExecutablePath() is not null;

    /// <summary>
    /// 这台机器上有哪几个风扇、各自叫什么。
    ///
    /// **这是"有什么"，不是"现在多少"。** 核心那边 describe 明确不碰硬件 ——
    /// 挂着哪几个风扇在开通道那一刻就定了，所以这份清单一个进程里问一次就够。
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
        }
        // 曲线也一起丢掉：用户点"重新检测"要的是重新问一遍硬件，
        // 不是看我们上次记下的那份答案。别的软件、或者换了性能档位，
        // 固件那张表都可能已经不是我们存的那条了。
        lock (curveGate)
        {
            curves.Clear();
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
            response = SendAsync("describe", null, CancellationToken.None)
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
        needsTemperatures = Flag(result, "needsTemperatures");
        /*
         * 通道用不上时那句**具体**原因。
         *
         * "这台机器上没有 Uniwill 的 ACPI WMI 接口" 和 "没有管理员权限" 对用户
         * 是完全不同的两件事，笼统说一句"没有可用的风扇控制通道"，
         * 前者只能去换机器，后者其实重启一下就好了。
         */
        unavailableReason = Flag(result, "available") ? null : Text(result, "reason");

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
                Flag(fan, "requiresPeriodicReassert")));
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
    /// 这个风扇现在跑的曲线。核心给不出来就是 null。
    ///
    /// **单独一次往返，不跟着 describe 走**：读曲线要把固件的三张表都过一遍，
    /// 几十次 EC 往返；describe 是画一次界面就要问一遍的，混进去整个控制页都得等。
    /// </summary>
    public async Task<IReadOnlyList<ControlCurvePoint>?> ReadCurveAsync(
        int fanIndex,
        CancellationToken cancellationToken)
    {
        /*
         * **缓存是必须的，不是优化。**
         *
         * 读一条曲线要把固件的三张表都过一遍，每个字节还得连读到一致才算数 ——
         * 本机实测一次 11~16 秒。用户勾一下"转速曲线"就得干等十几秒，
         * 那这项功能等于没有。
         *
         * 缓存是安全的，因为**这张表只有被写才会变**：它存在 EC RAM 里，
         * 固件按它查表，不会自己改。我们写完会把回读的结果直接存进来
         * （<see cref="StoreCurve"/>），用户点"重新检测硬件"会清掉
         * （<see cref="ResetDetection"/>）。
         */
        lock (curveGate)
        {
            if (curves.TryGetValue(fanIndex, out var cached))
            {
                return cached;
            }
        }

        var response = await SendAsync(
                "curve",
                new Dictionary<string, object?> { ["fan"] = fanIndex },
                cancellationToken)
            .ConfigureAwait(false);
        if (response is not { } result
            || !result.TryGetProperty("curve", out var curve)
            || curve.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var points = new List<ControlCurvePoint>();
        foreach (var point in curve.EnumerateArray())
        {
            if (!point.TryGetProperty("temperatureCelsius", out var celsius)
                || celsius.ValueKind != JsonValueKind.Number
                || !point.TryGetProperty("percent", out var percent)
                || percent.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
            points.Add(new ControlCurvePoint(celsius.GetDouble(), percent.GetDouble()));
        }
        if (points.Count == 0)
        {
            return null;
        }
        StoreCurve(fanIndex, points);
        return points;
    }

    /// <summary>
    /// 这台机器要不要我们喂温度。
    ///
    /// 曲线归固件执行的机器**一次都不用推** —— 由核心在 describe 里回答，
    /// 我们不去猜。和风扇清单同一份缓存，问它很便宜。
    /// </summary>
    public bool NeedsTemperatures()
    {
        lock (describeGate)
        {
            // 清单没探过就先探一遍：这一条和风扇清单是同一次 describe 的结果。
            if (describedFans is null)
            {
                describedFans = ReadDescription();
            }
        }
        return needsTemperatures;
    }

    private volatile bool needsTemperatures;

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
    public async Task PushTemperaturesAsync(
        IReadOnlyDictionary<string, double> samples,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);
        await SendAsync(
                "temp",
                new Dictionary<string, object?> { ["samples"] = samples },
                cancellationToken,
                skipIfBusy: true)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 记下这个风扇现在跑的曲线。
    ///
    /// 写完曲线之后由写入器调：核心在应答里带回的是**写完重新读出来的**那条，
    /// 那正是新的当前值，不必再花十几秒去读一遍。
    /// </summary>
    public void StoreCurve(int fanIndex, IReadOnlyList<ControlCurvePoint> curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        lock (curveGate)
        {
            curves[fanIndex] = curve;
        }
    }

    private readonly object curveGate = new();
    private readonly Dictionary<int, IReadOnlyList<ControlCurvePoint>> curves = [];

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
        try
        {
            if (EnsureStarted() is not { } running)
            {
                return null;
            }

            var request = new Dictionary<string, object?>(
                arguments ?? new Dictionary<string, object?>())
            {
                ["id"] = Interlocked.Increment(ref nextRequestId),
                ["op"] = operation
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            await running.StandardInput
                .WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token)
                .ConfigureAwait(false);
            await running.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

            var line = await running.StandardOutput.ReadLineAsync(timeout.Token)
                .ConfigureAwait(false);
            if (line is null)
            {
                Stop();
                return null;
            }
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
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
        }
    }

    private Process? EnsureStarted()
    {
        if (process is { HasExited: false })
        {
            return process;
        }
        Stop();

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
            /*
             * 告诉核心 LibreHardwareMonitor 装在哪儿 —— 台式机主板风扇接头那条路要用它
             * （它内置 PawnIO 的 LpcIO，不需要 WinRing0）。
             *
             * **由我们告诉它，不让它自己猜路径。** 组件目录的布局是这一侧的事；
             * 核心猜一个相对路径，装法一变就找不到，而且找不到时报出来的原因会变成
             * "这台机器没有主板风扇接头" —— 那是假的。
             *
             * 组件没装就不设这个变量，核心那边会如实说缺什么。
             */
            if (ResolveLibreHardwareMonitorPath() is { } libraryPath)
            {
                start.Environment["FANCONTROLCORE_LHM_PATH"] = libraryPath;
            }
            process = Process.Start(start);
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
    /// LibreHardwareMonitor 组件装在哪儿。没装就是 null。
    ///
    /// 它和风扇核心在同一个组件根目录下，各占一个子目录。
    /// </summary>
    private string? ResolveLibreHardwareMonitorPath()
    {
        var path = Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "Dependencies",
            "librehardwaremonitor-provider");
        return Directory.Exists(path) ? path : null;
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
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                // **关标准输入而不是杀进程**：那个核心读到流尾会把风扇交还固件再退出。
                // 直接杀掉会把风扇留在我们设的转速上 —— 那正是它要避免的事。
                process.StandardInput.Close();
                if (!process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            // 已经没了，正是想要的结果。
        }
        finally
        {
            process.Dispose();
            process = null;
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
        gate.Dispose();
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
    string? FirmwareCurveRequires = null);
