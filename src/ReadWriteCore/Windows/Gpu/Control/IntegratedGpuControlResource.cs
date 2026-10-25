using System.Text.Json;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Vendors.Intel;


namespace ResourceManager.ReadWrite.Windows.Gpu.Control;
public sealed class IntegratedGpuControlResource : IDisposable
{
    internal const string CurveOptimizerCapabilityId = "gpu.curve-optimizer";
    internal const string CoreClockOffsetCapabilityId = "gpu.core-clock-offset";

    private readonly IIntelGraphicsControlHardware intel;
    private readonly IHardwareBridgeProcessClient? bridge;
    private readonly Func<CancellationToken,Task<bool>> isOverclockAccepted;
    private readonly Func<IntegratedGpuControlMessage,IReadOnlyList<object?>,string>? describe;
    private readonly Action<Exception?,IntegratedGpuControlMessage,IReadOnlyList<object?>>? warning;
    private readonly IntegratedGpuControlBaselineStore amdBaseline;
    private readonly bool ownsBridge;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int disposed;
    /// <summary>Borrow the same helper as AMD CPU. Intel hardware remains borrowed; consent is explicit read-only state.</summary>
    public IntegratedGpuControlResource(IHardwareBridgeProcessClient? bridge, IIntelGraphicsControlHardware intel, IntegratedGpuControlOptions options,
        Func<IntegratedGpuControlMessage,IReadOnlyList<object?>,string>? describe = null,
        Action<Exception?,IntegratedGpuControlMessage,IReadOnlyList<object?>>? warning = null)
        : this(bridge,intel,options,describe,warning,false) { }
    private IntegratedGpuControlResource(IHardwareBridgeProcessClient? bridge,IIntelGraphicsControlHardware intel,IntegratedGpuControlOptions options,
        Func<IntegratedGpuControlMessage,IReadOnlyList<object?>,string>? describe,
        Action<Exception?,IntegratedGpuControlMessage,IReadOnlyList<object?>>? warning,bool ownsBridge)
    {
        ArgumentNullException.ThrowIfNull(intel); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.IsOverclockAccepted);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AmdBaselinePath);
        this.bridge=bridge;this.intel=intel;this.isOverclockAccepted=options.IsOverclockAccepted;
        this.describe=describe;this.warning=warning;this.ownsBridge=ownsBridge;
        amdBaseline=new(Path.GetFullPath(options.AmdBaselinePath));
    }
    /// <summary>Real process client/IGCL construction only; no process starts or driver is probed here.</summary>
    public static IntegratedGpuControlResource Create(string executablePath,HardwareBridgeProcessOptions helperOptions,IntegratedGpuControlOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);var path=Path.GetFullPath(executablePath);
        return new(new HardwareBridgeProcessClient(() => File.Exists(path) ? path : null,helperOptions),new IntelGraphicsControlBridge(),options,null,null,true);
    }
    private string Describe(IntegratedGpuControlMessage code,params object?[] args)
        => describe?.Invoke(code,args) ?? "integrated-gpu-control-"+code+(args.Length == 0 ? "" : ": "+string.Join(", ",args));
    private void Warn(Exception? error,IntegratedGpuControlMessage code,params object?[] args) => warning?.Invoke(error,code,args);
    private void CheckLifetime() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0,this);
    public void Dispose() { if (Interlocked.Exchange(ref disposed,1) == 0 && ownsBridge) bridge?.Dispose(); }
    public async Task<bool> RestoreAsync(IntegratedGpuControlTarget target,IntegratedGpuControlCapability capability,CancellationToken token=default)
    {
        CheckLifetime();
        double? original;
        if (target.Route == IntegratedGpuControlRoute.AmdIntegrated)
        {
            try { if (!amdBaseline.TryRead(target.Id, out original)) return false; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Warn(error, IntegratedGpuControlMessage.BaselineWarning); return false; }
        }
        else original = capability.Range?.DefaultValue;
        if (original is not { } value) return false;
        return (await WriteAsync(target,capability,new(capability.Field,value,capability.Range?.Unit),token).ConfigureAwait(false)).Status == IntegratedGpuControlStatus.Applied;
    }
    private string BridgeMissing => Describe(IntegratedGpuControlMessage.BridgeMissing);
    private string BridgeSilent => Describe(IntegratedGpuControlMessage.BridgeSilent);
    private string NotSupported => Describe(IntegratedGpuControlMessage.NotSupported);
    private string NotEffective => Describe(IntegratedGpuControlMessage.NotEffective);
    private string IntelDriverMissing => Describe(IntegratedGpuControlMessage.IntelDriverMissing);
    private string IntelTargetMissing => Describe(IntegratedGpuControlMessage.IntelTargetMissing);
    private string OverclockNotAccepted => Describe(IntegratedGpuControlMessage.OverclockNotAccepted);
    public string? ChannelOf(IntegratedGpuControlTarget target, string capabilityId)
        => !IsMine(target, capabilityId) ? null : capabilityId switch
    {
        CurveOptimizerCapabilityId => "amd-smu",
        CoreClockOffsetCapabilityId => "igcl",
        _ => null
    };

    public IntegratedGpuControlAvailability Probe(IntegratedGpuControlTarget target, IntegratedGpuControlCapability capability)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id))
        {
            return IntegratedGpuControlAvailability.NotMine;
        }
        if (target.Route == IntegratedGpuControlRoute.IntelIntegrated)
        {
            return ProbeIntel(target);
        }
        if (bridge is null || !bridge.IsInstalled)
        {
            return IntegratedGpuControlAvailability.No(BridgeMissing, kind: IntegratedGpuControlUnavailableKind.Component);
        }

        var described = Ask("describe");
        if (described is not { } description || !Flag(description, "available"))
        {
            return IntegratedGpuControlAvailability.No(Text(described, "reason") ?? bridge?.UnavailableReason ?? BridgeSilent);
        }
        if (!Flag(description, "integratedGpuCurveOptimizer"))
        {
            return IntegratedGpuControlAvailability.No(NotSupported);
        }

        double? baseline;
        bool valid;
        try { valid = amdBaseline.TryRead(target.Id, out baseline); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Warn(error,IntegratedGpuControlMessage.BaselineWarning);
            baseline = null;
            valid = false;
        }
        if (!valid)
            return IntegratedGpuControlAvailability.No(Describe(IntegratedGpuControlMessage.BaselineUnavailable), canRead: true);
        return IntegratedGpuControlAvailability.Yes(
            capability.Range is { } declared
                ? declared with { DefaultValue = baseline }
                : new IntegratedGpuControlRange(-30, 10, 1, "档", baseline));
    }

    public async Task<IntegratedGpuControlOutcome> WriteAsync(
        IntegratedGpuControlTarget target,
        IntegratedGpuControlCapability capability,
        IntegratedGpuControlSetting setting,
        CancellationToken cancellationToken = default)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(setting);

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id) || setting.CapabilityId != capability.Id)
            return Failed(target, capability, NotSupported);
        if (setting.Number is not { } value || !double.IsFinite(value))
        {
            return Failed(target, capability, Describe(IntegratedGpuControlMessage.NumberRequired));
        }
        var bounds = capability.Range;
        var wanted = bounds is null
            ? value
            : Math.Clamp(value, bounds.Minimum, bounds.Maximum);

        // Intel 那条的单位是 MHz，不是档位 —— 别把它当整数档位取整。
        if (target.Route == IntegratedGpuControlRoute.IntelIntegrated)
        {
            return await WriteIntelAsync(target, capability, wanted).ConfigureAwait(false);
        }

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                if (!amdBaseline.TryRead(target.Id, out var baseline))
                    return Failed(target, capability, Describe(IntegratedGpuControlMessage.BaselineUnavailable));
                if (baseline is null)
                {
                    var before = await SendAsync("read", null, cancellationToken).ConfigureAwait(false);
                    var current = before is { } reading && Flag(reading, "ok")
                        ? Number(reading, "integratedGpuCurveOptimizerCounts") : null;
                    if (amdBaseline.Capture(target.Id, current) is null)
                        return Failed(target, capability, Describe(IntegratedGpuControlMessage.BaselineUnavailable));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Warn(error, IntegratedGpuControlMessage.BaselineWarning);
                return Failed(target, capability, Describe(IntegratedGpuControlMessage.BaselineUnavailable));
            }
            var counts = (int)wanted;
            // 和处理器那几项一样，值连同单位一起发；辅助进程不收裸数字。
            var response = await SendAsync(
                "set-igpu-curve-optimizer",
                new Dictionary<string, object?> { ["value"] = counts, ["unit"] = "档" },
                cancellationToken).ConfigureAwait(false);
            if (response is not { } result || !Flag(result, "ok"))
            {
                return Failed(target, capability, BridgeSilent);
            }
            if (!Flag(result, "accepted"))
            {
                Warn(null,IntegratedGpuControlMessage.SmuRejectedWarning,counts);
                return Failed(target, capability, Describe(IntegratedGpuControlMessage.SmuRejected));
            }
            if (Number(result, "integratedGpuCurveOptimizerCounts") is { } after
                && (int)after != counts)
            {
                return Failed(target, capability, NotEffective);
            }

            return new IntegratedGpuControlOutcome(
                target.Id,
                capability.Id,
                IntegratedGpuControlStatus.Applied,
                null) { ReadbackVerified = Number(result, "integratedGpuCurveOptimizerCounts") is not null };
        }
        finally { writeGate.Release(); }
    }
    public async Task<IntegratedGpuControlValue?> ReadAsync(
        IntegratedGpuControlTarget target,
        IntegratedGpuControlCapability capability,
        CancellationToken cancellationToken = default)
    {
        CheckLifetime();
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id)) return null;
        // Intel 那条走 IGCL，和辅助进程没关系。
        if (target.Route == IntegratedGpuControlRoute.IntelIntegrated)
        {
            return intel.FindIntegratedDevice(target.AdapterKey) is { } device
                && intel.ReadFrequencyOffsetMhz(device) is { } offset
                    ? new IntegratedGpuControlValue(
                        target.Id,
                        capability.Id,
                        Number: offset,
                        Unit: "MHz")
                    : new IntegratedGpuControlValue(
                        target.Id,
                        capability.Id,
                        UnreadableReason: intel.IsAvailable ? IntelTargetMissing : IntelDriverMissing);
        }

        var description = await SendAsync("describe", null, cancellationToken).ConfigureAwait(false);
        if (description is not { } facts || !Flag(facts, "available") || !Flag(facts, "integratedGpuCurveOptimizer"))
            return new(target.Id, capability.Id, UnreadableReason: Text(description, "reason") ?? bridge?.UnavailableReason ?? BridgeSilent);
        var reading = await SendAsync("read", null, cancellationToken)
            .ConfigureAwait(false);
        if (reading is not { } result || !Flag(result, "ok"))
        {
            return new IntegratedGpuControlValue(
                target.Id,
                capability.Id,
                UnreadableReason: BridgeSilent);
        }
        return Number(result, "integratedGpuCurveOptimizerCounts") is { } value
            ? new IntegratedGpuControlValue(
                target.Id,
                capability.Id,
                Number: Math.Round(value),
                Unit: "档")
            : new IntegratedGpuControlValue(
                target.Id,
                capability.Id,
                UnreadableReason: Describe(IntegratedGpuControlMessage.ReadingMissing));
    }
    private IntegratedGpuControlAvailability ProbeIntel(IntegratedGpuControlTarget target)
    {
        if (!intel.IsAvailable)
        {
            return IntegratedGpuControlAvailability.No(IntelDriverMissing, kind: IntegratedGpuControlUnavailableKind.Component);
        }
        if (!isOverclockAccepted(CancellationToken.None).GetAwaiter().GetResult())
        {
            return IntegratedGpuControlAvailability.No(OverclockNotAccepted, kind: IntegratedGpuControlUnavailableKind.Prerequisite);
        }
        if (intel.FindIntegratedDevice(target.AdapterKey) is null)
        {
            return IntegratedGpuControlAvailability.No(IntelTargetMissing);
        }

        // Driver consent is submitted only by the explicit write operation.
        return IntegratedGpuControlAvailability.Yes();
    }

    private async Task<IntegratedGpuControlOutcome> WriteIntelAsync(
        IntegratedGpuControlTarget target,
        IntegratedGpuControlCapability capability,
        double offsetMhz)
    {
        if (intel.FindIntegratedDevice(target.AdapterKey) is not { } device)
        {
            return Failed(target, capability, intel.IsAvailable ? IntelTargetMissing : IntelDriverMissing);
        }
        if (!await isOverclockAccepted(CancellationToken.None).ConfigureAwait(false))
        {
            return Failed(target, capability, OverclockNotAccepted);
        }
        if (!intel.TryAcceptOverclockWaiver(device))
        {
            return Failed(target, capability, Describe(IntegratedGpuControlMessage.WaiverRejected));
        }

        var result = intel.WriteFrequencyOffsetMhz(device, offsetMhz);
        if (result != 0)
        {
            Warn(null,IntegratedGpuControlMessage.IntelRejectedWarning,
                offsetMhz,
                result);
            return Failed(target, capability, Describe(IntegratedGpuControlMessage.DriverRejected));
        }

        // 回读核对。驱动会按硬件余量夹值，夹过之后就不是用户要的那个数了。
        var readBack = intel.ReadFrequencyOffsetMhz(device);
        return readBack is not { } applied || !double.IsFinite(applied) || Math.Abs(applied - offsetMhz) > 1
            ? Failed(target, capability, NotEffective)
            : new IntegratedGpuControlOutcome(
                target.Id,
                capability.Id,
                IntegratedGpuControlStatus.Applied,
                null) { ReadbackVerified = true };
    }

    private Task<JsonElement?> SendAsync(string operation,IReadOnlyDictionary<string,object?>? arguments,CancellationToken token)
        => bridge is null ? Task.FromResult<JsonElement?>(null) : bridge.SendAsync(operation,arguments,token);
    private JsonElement? Ask(string operation)
        => SendAsync(operation, null, CancellationToken.None).GetAwaiter().GetResult();

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

    private static bool IsMine(IntegratedGpuControlTarget target,string capabilityId)
        => target.Route == IntegratedGpuControlRoute.AmdIntegrated && capabilityId == CurveOptimizerCapabilityId
            || target.Route == IntegratedGpuControlRoute.IntelIntegrated && capabilityId == CoreClockOffsetCapabilityId;

    private static IntegratedGpuControlOutcome Failed(
        IntegratedGpuControlTarget target,
        IntegratedGpuControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, IntegratedGpuControlStatus.Failed, reason);
}
