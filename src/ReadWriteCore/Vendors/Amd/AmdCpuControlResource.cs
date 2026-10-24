using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.ReadWrite.Helpers;


namespace ResourceManager.ReadWrite.Vendors.Amd;
/// <summary>AMD CPU owner. Only an explicit write captures a durable, target-bound restore value.</summary>
public sealed class AmdCpuControlResource : IDisposable
{
    private readonly IHardwareBridgeProcessClient bridge;
    private readonly AmdCpuControlOptions options;
    private readonly Func<AmdCpuControlMessage, IReadOnlyList<object?>, string>? describe;
    private readonly Action<Exception?, AmdCpuControlMessage, IReadOnlyList<object?>>? warning;
    private readonly bool ownsBridge;
    private int disposed;
    public AmdCpuControlResource(IHardwareBridgeProcessClient bridge, AmdCpuControlOptions options,
        Func<AmdCpuControlMessage, IReadOnlyList<object?>, string>? describe = null,
        Action<Exception?, AmdCpuControlMessage, IReadOnlyList<object?>>? warning = null)
        : this(bridge, options, describe, warning, false) { }
    private AmdCpuControlResource(IHardwareBridgeProcessClient bridge, AmdCpuControlOptions options,
        Func<AmdCpuControlMessage, IReadOnlyList<object?>, string>? describe,
        Action<Exception?, AmdCpuControlMessage, IReadOnlyList<object?>>? warning, bool ownsBridge)
    {
        ArgumentNullException.ThrowIfNull(bridge); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BaselinePath);
        this.bridge = bridge; this.options = options; this.describe = describe; this.warning = warning; this.ownsBridge = ownsBridge;
    }
    /// <summary>Constructs the actual existing process client; no helper starts until an explicit operation.</summary>
    public static AmdCpuControlResource Create(string executablePath, HardwareBridgeProcessOptions helperOptions, AmdCpuControlOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var path = Path.GetFullPath(executablePath);
        return new(new HardwareBridgeProcessClient(() => File.Exists(path) ? path : null, helperOptions), options, null, null, true);
    }
    private string Describe(AmdCpuControlMessage code, params object?[] arguments)
        => describe?.Invoke(code, arguments) ?? "amd-cpu-control-" + code + (arguments.Length == 0 ? "" : ": " + string.Join(", ", arguments));
    private void Warn(Exception? error, AmdCpuControlMessage code, params object?[] arguments) => warning?.Invoke(error, code, arguments);
    private void CheckLifetime() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsBridge) bridge.Dispose(); }
    public async Task<bool> RestoreAsync(AmdCpuControlTarget target, AmdCpuControlCapability capability, CancellationToken token = default)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        if (!IsMine(target, capability.Id) || WritableOf(capability.Id) is not { } writable || writable.Unverified is not null) return false;
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!TryReadBaseline(target, writable, out var baseline) || baseline is not { } value) return false;
            if ((await WriteValueAsync(target, capability, writable, value, token).ConfigureAwait(false)).Status != AmdCpuControlStatus.Applied) return false;
            try { File.Delete(BaselinePath(target, writable)); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Warn(error, AmdCpuControlMessage.WriteBaselineWarning, BaselinePath(target, writable)); return false; }
        }
        finally { writeGate.Release(); }
    }
    internal const string PowerLimitCapabilityId = "cpu.power-limit";
    internal const string SlowPowerLimitCapabilityId = "cpu.slow-power-limit";
    internal const string FastPowerLimitCapabilityId = "cpu.fast-power-limit";
    internal const string TdcLimitCapabilityId = "cpu.tdc-limit";
    internal const string EdcLimitCapabilityId = "cpu.edc-limit";
    internal const string TemperatureLimitCapabilityId = "cpu.temperature-limit";
    internal const string PowerBoostScalarCapabilityId = "cpu.pbo-scalar";
    internal const string CurveOptimizerCapabilityId = "cpu.curve-optimizer";
    private sealed record CpuWritable(
        string CapabilityId,
        string Operation,
        string? Which,
        string? ReadingField,
        string Unit,
        string BaselineFileName,
        string DescribeFlag,
        double Tolerance,
        AmdCpuControlMessage? Unverified = null);

    private static readonly CpuWritable[] Writables =
    [
        // 三条功耗墙。**它们是三件不同的事，所以是三项。**
        // 持续决定机器长期跑在多少瓦，短时决定几十秒的爬坡能到多高，
        // 瞬时决定毫秒级的峰值。先前持续和短时合成一项，等于替用户砍掉一条。
        new(PowerLimitCapabilityId, "set-power-limit", "stapm",
            "stapmLimitWatts", "W", "cpu-power-baseline.txt", "powerTable", 1),
        new(SlowPowerLimitCapabilityId, "set-power-limit", "slow",
            "slowLimitWatts", "W", "cpu-slow-power-baseline.txt", "powerTable", 1),
        new(FastPowerLimitCapabilityId, "set-power-limit", "fast",
            "fastLimitWatts", "W", "cpu-fast-power-baseline.txt", "powerTable", 1),

        // 两条电流墙。功耗墙管的是瓦，这两条管的是安 —— 供电扛不扛得住
        // 和散热扛不扛得住是两件事，别家（Ryzen Master）也是分开摆的。
        new(TdcLimitCapabilityId, "set-current-limit", "tdc-vdd",
            "tdcLimitAmps", "A", "cpu-tdc-baseline.txt", "powerTable", 1,
            AmdCpuControlMessage.CurrentLimitMappingUnconfirmed),
        new(EdcLimitCapabilityId, "set-current-limit", "edc-vdd",
            "edcLimitAmps", "A", "cpu-edc-baseline.txt", "powerTable", 1,
            AmdCpuControlMessage.CurrentLimitMappingUnconfirmed),

        // 温度墙。**读不回来**，见 ReadingField 的说明。
        new(TemperatureLimitCapabilityId, "set-temperature-limit", null,
            null, "°C", "cpu-temperature-baseline.txt", "powerTable", 1,
            AmdCpuControlMessage.TemperatureLimitUnverifiable),

        // PBO 标量。1 倍就是厂商默认，所以基线不用读 —— 它是有文档的固定值。
        new(PowerBoostScalarCapabilityId, "set-pbo-scalar", null,
            null, "×", "cpu-pbo-scalar-baseline.txt", "powerTable", 0.5),

        new(CurveOptimizerCapabilityId, "set-curve-optimizer", null,
            "curveOptimizerCounts", "档", "cpu-curve-optimizer-baseline.txt",
            "curveOptimizer", 0.5)
    ];

    private static CpuWritable? WritableOf(string capabilityId)
        => Writables.FirstOrDefault(
            (entry) => string.Equals(entry.CapabilityId, capabilityId, StringComparison.Ordinal));

    private string BridgeMissing => Describe(AmdCpuControlMessage.BridgeMissing);
    private string BridgeSilent => Describe(AmdCpuControlMessage.BridgeSilent);
    private string NotSupported => Describe(AmdCpuControlMessage.NotSupported);
    private string NotEffective => Describe(AmdCpuControlMessage.NotEffective);
    private string TemperatureLimitUnverifiable => Describe(AmdCpuControlMessage.TemperatureLimitUnverifiable);
    private string CurrentLimitMappingUnconfirmed => Describe(AmdCpuControlMessage.CurrentLimitMappingUnconfirmed);

    private readonly SemaphoreSlim writeGate = new(1, 1);
    public string? ChannelOf(AmdCpuControlTarget target, string capabilityId)
        => IsMine(target, capabilityId) && WritableOf(capabilityId) is not null
            ? "amd-smu"
            : null;

    public AmdCpuControlAvailability Probe(AmdCpuControlTarget target, AmdCpuControlCapability capability)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id) || WritableOf(capability.Id) is not { } writable)
        {
            return AmdCpuControlAvailability.NotMine;
        }
        if (!bridge.IsInstalled)
        {
            return AmdCpuControlAvailability.No(BridgeMissing, kind: AmdCpuControlUnavailableKind.Component);
        }

        var described = Ask("describe", null);
        if (described is not { } description || !Flag(description, "available"))
        {
            return AmdCpuControlAvailability.No(Text(described, "reason") ?? bridge.UnavailableReason ?? BridgeSilent);
        }

        // 能设什么就显示什么：辅助进程按这代处理器有没有这条 SMU 命令来报。
        if (!Flag(description, writable.DescribeFlag))
        {
            return AmdCpuControlAvailability.No(NotSupported);
        }

        if (writable.Unverified is { } unverified)
        {
            // 这几条是**我们**还没查清（电流墙映射、温度墙回读），不是硬件做不到。
            // 标成平台不支持等于把自己欠的说成用户的机器不行。
            return AmdCpuControlAvailability.No(Describe(unverified), kind: AmdCpuControlUnavailableKind.NotImplemented);
        }

        if (Ask("read", null) is not { } reading || !IsUsableReading(reading, writable.ReadingField))
        {
            return AmdCpuControlAvailability.No(BridgeSilent);
        }

        if (!TryReadBaseline(target, writable, out var baseline)
            || baseline is null && File.Exists(options.BaselinePath(writable.BaselineFileName)))
            return AmdCpuControlAvailability.No(Describe(AmdCpuControlMessage.BaselineUnavailable));
        return AmdCpuControlAvailability.Yes(RangeOf(writable, capability, reading, baseline));
    }
    public static bool IsUsableReading(JsonElement reading, string? field)
        => reading.ValueKind == JsonValueKind.Object && Flag(reading, "ok")
            && (field is null || Number(reading, field) is { } value && double.IsFinite(value));

    private AmdCpuControlRange RangeOf(
        CpuWritable writable,
        AmdCpuControlCapability capability,
        JsonElement reading,
        double? saved)
    {
        var declared = capability.Range;
        // 读不回来的项（PBO 标量）用目录里写的默认值 —— 那个 1 倍是有文档的固定值，
        // 不是猜的。
        var current = writable.ReadingField is { } field
            ? Number(reading, field)
            : declared?.DefaultValue;
        double? baseline = saved ?? current;

        return declared is { } shape
            ? shape with { DefaultValue = baseline is { } found ? Math.Round(found, 2) : null }
            : new AmdCpuControlRange(0, 100, 1, writable.Unit, baseline);
    }

    public async Task<AmdCpuControlOutcome> WriteAsync(
        AmdCpuControlTarget target,
        AmdCpuControlCapability capability,
        AmdCpuControlSetting setting,
        CancellationToken cancellationToken = default)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(setting);

        if (!IsMine(target, capability.Id) || WritableOf(capability.Id) is not { } writable)
        {
            return Failed(target, capability, NotSupported);
        }
        if (setting.Field != capability.Field) return Failed(target, capability, NotSupported);
        if (writable.Unverified is { } unverified) return Failed(target, capability, Describe(unverified));
        if (setting.Number is not { } value || !double.IsFinite(value))
        {
            return Failed(target, capability, Describe(AmdCpuControlMessage.NumberRequired));
        }
        var bounds = capability.Range;
        var wanted = bounds is null ? value : Math.Clamp(value, bounds.Minimum, bounds.Maximum);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryReadBaseline(target, writable, out var baseline))
                return Failed(target, capability, Describe(AmdCpuControlMessage.BaselineUnavailable));
            if (baseline is null)
            {
                // Capability-only files cannot prove which CPU or explicit operation they belonged to.
                if (File.Exists(options.BaselinePath(writable.BaselineFileName)))
                    return Failed(target, capability, Describe(AmdCpuControlMessage.BaselineUnavailable));
                var reading = await bridge.SendAsync("read", null, cancellationToken).ConfigureAwait(false);
                var original = writable.ReadingField is { } field && reading is { } confirmed && IsUsableReading(confirmed, field)
                    ? Number(confirmed, field)
                    : writable.ReadingField is null ? 1d : null;
                if (original is not { } before || !double.IsFinite(before))
                    return Failed(target, capability, Describe(AmdCpuControlMessage.BaselineUnavailable));
                if (!WritePersisted(target, writable, before))
                    return Failed(target, capability, Describe(AmdCpuControlMessage.BaselineUnavailable));
            }
            return await WriteValueAsync(target, capability, writable, wanted, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    private async Task<AmdCpuControlOutcome> WriteValueAsync(AmdCpuControlTarget target,
        AmdCpuControlCapability capability, CpuWritable writable, double wanted, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["value"] = wanted,
            ["unit"] = writable.Unit
        };
        if (writable.Which is { } which)
        {
            arguments["which"] = which;
        }

        var response = await bridge.SendAsync(writable.Operation, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (response is not { } result || !Flag(result, "ok"))
        {
            return Failed(target, capability, BridgeSilent);
        }

        // 辅助进程说 SMU 拒绝了就是拒绝，不用再看回读。
        if (result.TryGetProperty("accepted", out var accepted)
            && accepted.ValueKind == JsonValueKind.False)
        {
            return Failed(target, capability, Describe(AmdCpuControlMessage.SmuRejected));
        }

        
        if (Text(result, "status") is { } status
            && !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            Warn(null, AmdCpuControlMessage.StatusWarning,
                capability.Id,
                status);
            return Failed(target, capability, Describe(AmdCpuControlMessage.StatusRejected, status));
        }

        // 回执只说"收到了"。真正算数的是回读那个**上限** —— 不是实际值，
        // 实际值由固件按温度调度，通常远低于上限。
        if (writable.ReadingField is not { } field)
        {
            // 读不回来的项只能以状态码为准，上面已经看过了。
            return Applied(target, capability);
        }
        if (Number(result, field) is not { } after
            || Math.Abs(after - wanted) > writable.Tolerance)
        {
            Warn(null, AmdCpuControlMessage.ReadbackWarning,
                capability.Id,
                field,
                wanted,
                Number(result, field));
            return Failed(target, capability, NotEffective);
        }

        return Applied(target, capability);
    }
    public async Task<AmdCpuControlValue?> ReadAsync(
        AmdCpuControlTarget target,
        AmdCpuControlCapability capability,
        CancellationToken cancellationToken = default)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id) || WritableOf(capability.Id) is not { } writable)
        {
            return null;
        }
        if (writable.ReadingField is not { } field)
        {
            return new AmdCpuControlValue(
                target.Id,
                capability.Id,
                UnreadableReason: Describe(AmdCpuControlMessage.Unreadable));
        }

        var reading = await bridge.SendAsync("read", null, cancellationToken)
            .ConfigureAwait(false);
        if (reading is not { } result || !Flag(result, "ok"))
        {
            return new AmdCpuControlValue(
                target.Id,
                capability.Id,
                UnreadableReason: BridgeSilent);
        }

        return Number(result, field) is { } value
            ? new AmdCpuControlValue(
                target.Id,
                capability.Id,
                Number: Math.Round(value, 1),
                Unit: writable.Unit)
            : new AmdCpuControlValue(
                target.Id,
                capability.Id,
                UnreadableReason: Describe(AmdCpuControlMessage.ReadingMissing));
    }
    private sealed record RestoreBaseline(int Version, string TargetId, string CapabilityId, double? Value);
    private bool TryReadBaseline(AmdCpuControlTarget target, CpuWritable writable, out double? value)
    {
        value = null;
        var path = BaselinePath(target, writable);
        try
        {
            var baseline = JsonSerializer.Deserialize<RestoreBaseline>(File.ReadAllText(path));
            if (baseline is not { Version: 1, Value: { } original } || baseline.TargetId != target.Id
                || baseline.CapabilityId != writable.CapabilityId || !double.IsFinite(original))
            { Warn(null, AmdCpuControlMessage.ReadBaselineWarning, path); return false; }
            value = original;
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Warn(error, AmdCpuControlMessage.ReadBaselineWarning, path);
            return false;
        }
    }

    private bool WritePersisted(AmdCpuControlTarget target, CpuWritable writable, double value)
    {
        var path = BaselinePath(target, writable);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new RestoreBaseline(1, target.Id, writable.CapabilityId, value));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Warn(error, AmdCpuControlMessage.WriteBaselineWarning, path);
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Warn(error, AmdCpuControlMessage.WriteBaselineWarning, temporary); }
        }
    }

    private string BaselinePath(AmdCpuControlTarget target, CpuWritable writable)
        => options.BaselinePath(Path.GetFileNameWithoutExtension(writable.BaselineFileName) + "-"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Id))) + ".json");
    private JsonElement? Ask(string operation, IReadOnlyDictionary<string, object?>? arguments)
        => bridge.SendAsync(operation, arguments, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static bool Flag(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
                ? property.GetDouble()
                : null;

    private static string? Text(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static bool IsMine(AmdCpuControlTarget target, string capabilityId)
        => target.IsAmdCpu
            && WritableOf(capabilityId) is not null;

    private static AmdCpuControlOutcome Applied(AmdCpuControlTarget target, AmdCpuControlCapability capability)
        => new(target.Id, capability.Id, AmdCpuControlStatus.Applied, null);

    private static AmdCpuControlOutcome Failed(
        AmdCpuControlTarget target,
        AmdCpuControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, AmdCpuControlStatus.Failed, reason);
}
